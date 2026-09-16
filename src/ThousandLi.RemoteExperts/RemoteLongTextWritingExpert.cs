using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;

namespace ThousandLi.RemoteExperts;

/// <summary>
/// The execution context bound to remote proxy experts. Execution — and with it the model call and
/// the four-layer settings policy — lives on the platform side of the wire, so <see cref="BasicAi" />
/// access fails fast and settings resolve to plain defaults. The proxy expert itself never reads
/// either; the context only satisfies the <see cref="IExpertExecutionContext" /> bind contract.
/// </summary>
public sealed class RemoteExpertExecutionContext(BoundPlayerProfile? playerProfile = null)
    : IExpertExecutionContext
{
    /// <summary>The local player profile, or a documented placeholder when none was composed.</summary>
    public BoundPlayerProfile PlayerProfile { get; } = playerProfile ?? new BoundPlayerProfile(
        new PlayerId("remote-player"),
        "Remote platform player",
        "The player profile is not delivered over the remote invocation wire.");

    /// <summary>Always fails: a remote proxy never invokes a model locally.</summary>
    public IRuntimeBasicAi BasicAi => throw new NotSupportedException(
        "Remote proxy experts never invoke a model locally; execution happens on the platform side of the invocation wire.");

    /// <summary>
    /// Returns a plain default instance: the remote wire has no settings channel, and the proxy
    /// expert never consults settings (the platform-side expert resolves its own).
    /// </summary>
    public ValueTask<TSettings> GetExpertSettingsAsync<TSettings>(
        CancellationToken cancellationToken = default)
        where TSettings : class, new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TSettings());
    }

    /// <summary>A silent logger; remote proxy experts emit no local diagnostics.</summary>
    public ILogger Logger => NullLogger.Instance;
}

/// <summary>
/// The game-facing remote proxy of the long-text-writing expert category. Fluent configuration is
/// captured by the category anchor's protected state; <c>StreamAsync</c> encodes it through
/// <see cref="LongTextWritingWireCodec" /> (history buckets become lazily served <c>ref</c>
/// references), starts a platform invocation, and routes every SSE frame back to the game's real
/// callbacks — semantic events through the codec, <c>dataRequest</c> frames through the local bucket
/// registry, and the completion frame into an <see cref="ExpertCompletionResult" />. Behavior is
/// identical for streaming and completion entry points: the wire is always streaming, so both
/// receive every callback and differ only in intent.
/// </summary>
public sealed class RemoteLongTextWritingExpert(
    RemoteExpertClient client,
    RemoteInvocationRunnerOptions options,
    string contractId,
    LongTextWritingWireCodec? codec = null) : AbstractLongTextWritingExpert
{
    /// <summary>The dataResponse page size when the platform does not request one explicitly.</summary>
    private const int DefaultDataPageSize = 200;

    private readonly RemoteExpertClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly RemoteInvocationRunnerOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly string _contractId = !string.IsNullOrWhiteSpace(contractId)
        ? contractId
        : throw new ArgumentException("Contract id must be non-empty.", nameof(contractId));
    private readonly LongTextWritingWireCodec _codec = codec ?? LongTextWritingWireCodec.Default;

    /// <summary>
    /// Streams the invocation over the wire: semantic events drive the game callbacks while the
    /// task is in flight, and the completion frame produces the result artifact.
    /// </summary>
    protected override Task<ExpertCompletionResult> StreamAsyncCore(CancellationToken cancellationToken) =>
        InvokeRemoteAsync(cancellationToken);

    /// <summary>
    /// Completes the invocation over the same streaming wire as <see cref="StreamAsyncCore" />: the
    /// platform has no non-streaming mode, so callbacks still fire while the task is in flight and
    /// only the return path matches a plain completion. This differs from a local non-streaming
    /// execution, where no streaming callback is observed at all.
    /// </summary>
    protected override Task<ExpertCompletionResult> CompleteAsyncCore(CancellationToken cancellationToken) =>
        InvokeRemoteAsync(cancellationToken);

    private async Task<ExpertCompletionResult> InvokeRemoteAsync(CancellationToken cancellationToken)
    {
        ValidateCategoryInputs();
        var invocation = await _codec.EncodeInvocationAsync(
            WorldSettings!,
            PlayerInput!,
            PlayerPersona,
            CurrentState,
            StateSchema,
            ConfiguredPrimaryOutput,
            ConfiguredFeatures,
            ConfiguredHistoryBuckets,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // The platform defaults an omitted primary output to a text output; a game that never
        // configured one has no callback to deliver to, so dispatch targets a silent drop.
        var dispatchOutput = ConfiguredPrimaryOutput;

        var session = new RemoteInvocationSession(_client, _options);
        return await session.ExecuteAsync(
            _contractId,
            invocation.Input,
            idempotencyKey: null,
            clientCorrelation: null,
            (snapshot, frame, token) => HandleFrameAsync(snapshot, frame, invocation, dispatchOutput, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExpertCompletionResult?> HandleFrameAsync(
        RemoteInvocationSnapshot snapshot,
        RemoteExpertStreamFrame frame,
        LongTextWritingWireInvocation invocation,
        IExpertPrimaryOutput dispatchOutput,
        CancellationToken token)
    {
        switch (frame)
        {
            case RemoteExpertEventFrame eventFrame:
                await _codec.DispatchEventAsync(
                    dispatchOutput, ConfiguredFeatures, eventFrame.EventType, eventFrame.Payload, token)
                    .ConfigureAwait(false);
                return null;
            case RemoteExpertDataRequestFrame dataRequest:
                await ServeDataRequestAsync(snapshot, invocation, dataRequest, token).ConfigureAwait(false);
                return null;
            case RemoteExpertCompletedFrame completed:
                if (completed.Output is not { ValueKind: JsonValueKind.Object } output)
                    throw new RemoteProtocolException(
                        "The completed frame is missing the aggregated output object.");
                return await LongTextWritingWireCodec
                    .DecodeCompletionAsync(output, ConfiguredPrimaryOutput, token)
                    .ConfigureAwait(false);
            default:
                throw new RemoteProtocolException($"Unexpected stream frame '{frame.GetType().Name}'.");
        }
    }

    private async ValueTask ServeDataRequestAsync(
        RemoteInvocationSnapshot snapshot,
        LongTextWritingWireInvocation invocation,
        RemoteExpertDataRequestFrame dataRequest,
        CancellationToken token)
    {
        if (!invocation.BucketRegistry.TryGetValue(dataRequest.BucketId, out var bucket))
            throw new RemoteProtocolException(
                $"The platform requested data for unknown history bucket '{dataRequest.BucketId}'.");
        var body = await BuildDataResponseAsync(bucket, dataRequest, token).ConfigureAwait(false);
        await _client.PostInvocationDataAsync(
            snapshot.ExpertInvocationId, dataRequest.RequestId, body, token).ConfigureAwait(false);
    }

    private static async ValueTask<JsonElement> BuildDataResponseAsync(
        IHistoryBucket bucket,
        RemoteExpertDataRequestFrame frame,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            switch (frame.View)
            {
                case RemoteExpertDataView.Description:
                    writer.WriteString("description", bucket.Description);
                    break;
                case RemoteExpertDataView.RawTurns:
                    WritePagedView(
                        writer,
                        await bucket.GetRawTurnsAsync(cancellationToken).ConfigureAwait(false),
                        frame,
                        HistoryBucketWireProjection.WriteTurn);
                    break;
                case RemoteExpertDataView.CompressedView:
                    WritePagedView(
                        writer,
                        await bucket.GetCompressedViewAsync(cancellationToken: cancellationToken).ConfigureAwait(false),
                        frame,
                        HistoryBucketWireProjection.WriteProjectionEntry);
                    break;
                default:
                    throw new RemoteProtocolException($"Unknown data view '{frame.View}'.");
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Writes one cursor page of a bucket view. Cursors are item indices into the live view, which
    /// is stable only because buckets are contractually frozen during an invocation (only the Game
    /// appends, never mid-run — see the read-only bucket rules in the expert contracts).
    /// </summary>
    private static void WritePagedView<T>(
        Utf8JsonWriter writer,
        IReadOnlyList<T> view,
        RemoteExpertDataRequestFrame frame,
        Action<Utf8JsonWriter, T> writeItem)
    {
        var start = (int)Math.Min(frame.Cursor ?? 0, view.Count);
        var count = Math.Min(frame.Limit ?? DefaultDataPageSize, view.Count - start);

        writer.WriteStartArray("items");
        for (var index = start; index < start + count; index++)
            writeItem(writer, view[index]);
        writer.WriteEndArray();

        if (start + count < view.Count)
            writer.WriteNumber("nextCursor", start + count);
    }
}

/// <summary>
/// The game-facing typed expert facade for remote invocations: resolves the abstract category
/// anchor's <c>[ExpertContract]</c> identity and returns a freshly created and bound
/// <see cref="RemoteLongTextWritingExpert" /> per call, mirroring the local facade shape. The
/// category type is proxied through the long-text-writing wire codec, so <c>TAbstract</c> must be
/// the category anchor (or cast-compatible with the proxy).
/// </summary>
public sealed class RemoteExpertFacade(
    RemoteExpertClient client,
    RemoteInvocationRunnerOptions options,
    BoundPlayerProfile? playerProfile = null) : IExpertFacade
{
    private readonly RemoteExpertClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly RemoteInvocationRunnerOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly RemoteExpertExecutionContext _context = new(playerProfile);

    /// <summary>
    /// Resolves the category anchor type: reads its <c>[ExpertContract]</c> contract identity,
    /// creates a fresh remote proxy expert, binds it, and returns it to game code for fluent
    /// configuration.
    /// </summary>
    /// <typeparam name="TAbstract">The abstract category anchor type (must carry <c>[ExpertContract]</c>).</typeparam>
    public TAbstract Use<TAbstract>() where TAbstract : ExpertBase
    {
        var attribute = typeof(TAbstract).GetCustomAttribute<ExpertContractAttribute>()
            ?? throw new InvalidOperationException(
                $"The expert category type '{typeof(TAbstract).FullName}' does not carry '[ExpertContract]'; " +
                "the remote facade resolves the platform invocation through the category's contract identity.");
        var expert = new RemoteLongTextWritingExpert(
            _client,
            _options,
            attribute.Id);
        expert.Bind(_context);
        return (TAbstract)(object)expert;
    }
}
