using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ThousandLi.RemoteExperts;

/// <summary>A decoded SSE frame of the remote invocation event stream.</summary>
public abstract record RemoteExpertStreamFrame;

/// <summary>
/// A semantic event frame. <see cref="RemoteExpertEventFrame.Ordinal" /> is the frame id
/// (<c>id:</c> line) and equals the platform's persisted event ordinal; ordinals are strictly
/// increasing within one stream and across reconnects.
/// </summary>
public sealed record RemoteExpertEventFrame(long Ordinal, string EventType, JsonElement Payload)
    : RemoteExpertStreamFrame;

/// <summary>The terminal <c>completed</c> frame; <see cref="RemoteExpertCompletedFrame.Output" /> is <see langword="null" /> when the platform wrote none.</summary>
public sealed record RemoteExpertCompletedFrame(JsonElement? Output) : RemoteExpertStreamFrame;

/// <summary>The terminal <c>failed</c> frame carrying a stable error code.</summary>
public sealed record RemoteExpertFailedFrame(string Code, string Message) : RemoteExpertStreamFrame;

/// <summary>The terminal <c>cancelled</c> frame.</summary>
public sealed record RemoteExpertCancelledFrame : RemoteExpertStreamFrame;

/// <summary>
/// Decodes the platform invocation SSE text stream into typed frames. Validates that event-frame
/// ordinals are strictly monotonic within one decoded stream, skips comment lines (for example
/// <c>: keepalive</c>), and concatenates multi-line <c>data:</c> payloads with newlines per the SSE
/// specification. Cross-reconnect deduplication (dropping ordinals the caller has already seen) is
/// owned by the executor, not the decoder.
/// </summary>
public static class RemoteSemanticEventDecoder
{
    /// <summary>Decodes an SSE byte stream into typed frames until the stream ends or cancellation fires.</summary>
    /// <param name="stream">The raw response body stream.</param>
    /// <param name="cancellationToken">Propagated to line reads.</param>
    /// <exception cref="RemoteProtocolException">A frame violates the wire contract.</exception>
    public static async IAsyncEnumerable<RemoteExpertStreamFrame> DecodeAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var pendingId = (long?)null;
        var pendingData = new List<string>();
        long? lastOrdinal = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                var frame = TryDispatch(ref pendingId, pendingData, ref lastOrdinal);
                pendingData.Clear();
                if (frame is not null)
                    yield return frame;
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                pendingId = ParseOrdinal(line[3..].Trim());
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
                pendingData.Add(line["data:".Length..].Trim());

            // Unknown SSE fields (event:, retry:) are ignored; the platform does not use them.
        }

        var trailingFrame = TryDispatch(ref pendingId, pendingData, ref lastOrdinal);
        if (trailingFrame is not null)
            yield return trailingFrame;
    }

    private static RemoteExpertStreamFrame? TryDispatch(
        ref long? pendingId,
        List<string> pendingData,
        ref long? lastOrdinal)
    {
        if (pendingData.Count == 0)
        {
            pendingId = null;
            return null;
        }

        var payload = string.Join("\n", pendingData);
        var id = pendingId;
        pendingId = null;
        return DecodeFrame(id, payload, ref lastOrdinal);
    }

    private static RemoteExpertStreamFrame DecodeFrame(long? id, string payload, ref long? lastOrdinal)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new RemoteProtocolException(
                $"An SSE data payload is not valid JSON: {Truncate(payload)}.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
                throw new RemoteProtocolException(
                    $"An SSE data payload is not a frame object with a 'type' string: {Truncate(payload)}.");

            switch (typeElement.GetString())
            {
                case "event":
                    {
                        if (id is null)
                            throw new RemoteProtocolException(
                                $"An event frame is missing its 'id' ordinal: {Truncate(payload)}.");
                        if (lastOrdinal is { } previous && id.Value <= previous)
                            throw new RemoteProtocolException(
                                $"Event frame ordinals must be strictly monotonic, but {id.Value} followed {previous}.");
                        if (!root.TryGetProperty("eventType", out var eventTypeElement) ||
                            eventTypeElement.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(eventTypeElement.GetString()))
                            throw new RemoteProtocolException(
                                $"An event frame is missing its 'eventType' string: {Truncate(payload)}.");
                        if (!root.TryGetProperty("payload", out var payloadElement))
                            throw new RemoteProtocolException(
                                $"An event frame is missing its 'payload': {Truncate(payload)}.");

                        lastOrdinal = id.Value;
                        return new RemoteExpertEventFrame(
                            id.Value,
                            eventTypeElement.GetString()!,
                            payloadElement.Clone());
                    }
                case "completed":
                    {
                        return new RemoteExpertCompletedFrame(
                            root.TryGetProperty("output", out var outputElement) &&
                            outputElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                                ? outputElement.Clone()
                                : null);
                    }
                case "failed":
                    {
                        if (!root.TryGetProperty("code", out var codeElement) ||
                            codeElement.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(codeElement.GetString()))
                            throw new RemoteProtocolException(
                                $"A failed frame is missing its 'code' string: {Truncate(payload)}.");
                        var message = root.TryGetProperty("message", out var messageElement) &&
                                      messageElement.ValueKind == JsonValueKind.String
                            ? messageElement.GetString() ?? string.Empty
                            : string.Empty;
                        return new RemoteExpertFailedFrame(codeElement.GetString()!, message);
                    }
                case "cancelled":
                    return new RemoteExpertCancelledFrame();
                default:
                    throw new RemoteProtocolException(
                        $"An SSE frame has unknown type '{typeElement.GetString()}': {Truncate(payload)}.");
            }
        }
    }

    private static long ParseOrdinal(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) ||
            ordinal < 0)
            throw new RemoteProtocolException($"An SSE frame id '{value}' is not a non-negative ordinal.");
        return ordinal;
    }

    private static string Truncate(string text) =>
        text.Length <= 256 ? text : $"{text.AsSpan(0, 256)}…";
}
