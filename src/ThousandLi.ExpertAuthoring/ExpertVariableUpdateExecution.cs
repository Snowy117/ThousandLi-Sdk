using ThousandLi.Contracts;
using JsonElement = System.Text.Json.JsonElement;
using JsonValueKind = System.Text.Json.JsonValueKind;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
///     变量更新第二遍调用所需的稳定上下文。
///     <paramref name="VariableUpdateModelId"/> 由调用专家显式指定（变量更新是廉价 pass，通常选轻量模型）。
/// </summary>
public sealed record ExpertVariableUpdateContext(
    string WorldSettings,
    string PreviousState,
    string StateSchema,
    string VariableUpdateModelId);

/// <summary>
/// 组合专家主调用与可选变量更新调用，统一负责主输出记录、提示词、schema、解析与 callback 交付。
/// 传入 <c>null</c> Feature 时只执行主调用并忽略 context；传入 Feature 时要求非空 context。
/// </summary>
public static class ExpertVariableUpdateExecution
{
    /// <summary>执行流式主调用，并在显式传入 Feature 时执行变量更新完成调用。</summary>
    public static async Task<ExpertCompletionResult> StreamAsync(
        IExpertExecutionParticipant expert,
        BasicAiRequest mainRequest,
        IJsonExpertStreamEventSink mainSink,
        VariableUpdateFeature? feature,
        ExpertVariableUpdateContext? context,
        CancellationToken cancellationToken)
    {
        ValidateCommonArguments(expert, mainRequest, mainSink);
        if (feature is null)
        {
            return await ExpertExecution.StreamOnceAsync(
                    expert, mainRequest, mainSink, cancellationToken)
                .ConfigureAwait(false);
        }

        var validatedContext = ValidateContext(context);
        var recordingSink = new RecordingStreamEventSink(mainSink);
        var result = await ExpertExecution.StreamOnceAsync(
                expert, mainRequest, recordingSink, cancellationToken)
            .ConfigureAwait(false);
        await ProposeAsync(
                expert,
                feature,
                validatedContext,
                ExpertJsonStreamReconstruction.BuildJsonElement(recordingSink.Events),
                result.Reasoning,
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>执行非流式主调用，并在显式传入 Feature 时执行变量更新完成调用。</summary>
    public static async Task<ExpertCompletionResult> CompleteAsync(
        IExpertExecutionParticipant expert,
        BasicAiRequest mainRequest,
        IJsonExpertStreamEventSink mainSink,
        VariableUpdateFeature? feature,
        ExpertVariableUpdateContext? context,
        CancellationToken cancellationToken)
    {
        ValidateCommonArguments(expert, mainRequest, mainSink);
        if (feature is null)
        {
            return await ExpertExecution.CompleteOnceAsync(
                    expert, mainRequest, mainSink, cancellationToken)
                .ConfigureAwait(false);
        }

        var validatedContext = ValidateContext(context);
        var (result, completion) = await ExpertExecution
            .CompleteOnceWithCompletionAsync(expert, mainRequest, mainSink, cancellationToken)
            .ConfigureAwait(false);
        await ProposeAsync(
                expert,
                feature,
                validatedContext,
                completion.Json,
                completion.Reasoning,
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private static async Task ProposeAsync(
        IExpertExecutionParticipant expert,
        VariableUpdateFeature feature,
        ExpertVariableUpdateContext context,
        JsonElement mainOutput,
        string? reasoning,
        CancellationToken cancellationToken)
    {
        var prompt = VariableUpdatePromptBuilder.Build(
            context.WorldSettings,
            context.PreviousState,
            VariableUpdatePromptBuilder.FormatNewInformation(mainOutput, reasoning),
            context.StateSchema);
        var request = new BasicAiRequest(
            context.VariableUpdateModelId,
            [BasicAiMessage.User(prompt)],
            CreateTargetSchema());

        var completion = await expert.BasicAi
            .CompleteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var proposal = CreateProposal(completion.Json);
        await feature.OnPatchProposed(proposal, cancellationToken).ConfigureAwait(false);
    }

    private static AiObjectSchema CreateTargetSchema() =>
        AiJsonSchema.Object(
            AiJsonSchema.Required(
                "variableUpdates",
                AiJsonSchema.Array(AiJsonSchema.Object()),
                description: "状态变量更新命令数组。"));

    private static VariableUpdatePatchProposal CreateProposal(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("variableUpdates", out var patch) &&
            patch.ValueKind == JsonValueKind.Array)
        {
            return VariableUpdatePatchProposal.FromJson(patch);
        }

        // TODO(variable-update-repair): 非法 variableUpdates 当前回退为空数组；未来可增加一次修复重试。
        return new VariableUpdatePatchProposal([]);
    }

    private static void ValidateCommonArguments(
        IExpertExecutionParticipant expert,
        BasicAiRequest mainRequest,
        IJsonExpertStreamEventSink mainSink)
    {
        ArgumentNullException.ThrowIfNull(expert);
        ArgumentNullException.ThrowIfNull(mainRequest);
        ArgumentNullException.ThrowIfNull(mainSink);
    }

    private static ExpertVariableUpdateContext ValidateContext(ExpertVariableUpdateContext? context)
    {
        var validatedContext = context ?? throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(validatedContext.WorldSettings))
            throw new ArgumentException("Variable-update world settings cannot be empty.", nameof(context));
        if (string.IsNullOrWhiteSpace(validatedContext.PreviousState))
            throw new ArgumentException("Variable-update previous state cannot be empty.", nameof(context));
        if (string.IsNullOrWhiteSpace(validatedContext.VariableUpdateModelId))
            throw new ArgumentException("Variable-update model id cannot be empty.", nameof(context));
        return string.IsNullOrWhiteSpace(validatedContext.StateSchema)
            ? throw new ArgumentException("Variable-update state schema cannot be empty.", nameof(context))
            : validatedContext;
    }

    private sealed class RecordingStreamEventSink(IJsonExpertStreamEventSink inner) : IJsonExpertStreamEventSink
    {
        private readonly IJsonExpertStreamEventSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public List<JsonStreamEvent> Events { get; } = [];

        public async ValueTask OnEventAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(streamEvent);
            await _inner.OnEventAsync(streamEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
