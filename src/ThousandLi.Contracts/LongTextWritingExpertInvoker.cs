using System.Text.Json;
using JetBrains.Annotations;

namespace ThousandLi.Contracts;

/// <summary>
/// 长文本写作类别的结构化调用器：把 <see cref="ExpertInvocationRequest"/> 的 JSON 输入经
/// <see cref="LongTextWritingWireCodec"/> 解码成流畅配置链，驱动专家执行一次，再聚合出
/// 完成帧输出（<c>{primary, metadata?, reasoning?}</c>）。wire 校验沿用 codec 的稳定错误码；
/// 语义事件实时转发到调用方 sink。本地组合、DevHost 与远程适配共用同一实现。
/// </summary>
public static class LongTextWritingExpertInvoker
{
    /// <summary>
    /// 校验输入、解码为流畅配置并灌回专家、绑定上下文、执行一次流式调用，
    /// 返回完成帧聚合输出。
    /// </summary>
    /// <param name="expert">未绑定的具体专家实例（本方法负责恰好一次 <c>Bind</c> 并执行）。</param>
    /// <param name="request">结构化调用请求（<see cref="ExpertInvocationRequest.ContractId"/> 必须匹配本类别）。</param>
    /// <param name="events">语义事件出口（codec 解码出的回调事件实时转发到此处）。</param>
    /// <param name="context">绑定到本次专家实例的执行上下文。</param>
    /// <param name="bucketResolver">按 bucketId 解析历史桶引用的委托；为 null 时仅支持内联桶。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [UsedImplicitly]
    public static async Task<JsonElement> InvokeAsync(
        AbstractLongTextWritingExpert expert,
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        IExpertExecutionContext context,
        Func<string, IHistoryBucket>? bucketResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expert);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(request.ContractId, AbstractLongTextWritingExpert.ContractId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The long-text-writing invoker only serves contract '{AbstractLongTextWritingExpert.ContractId}', " +
                $"but the request targets '{request.ContractId}'.");
        }

        var codec = LongTextWritingWireCodec.Default;
        var errors = codec.Validate(request.Input);
        if (errors.Count > 0)
        {
            var first = errors[0];
            throw new ArgumentException(
                $"The invocation input is invalid ({first.Code} at {first.Pointer ?? first.Code}): {first.Message}");
        }

        var wireSink = new SemanticEventWireSink(events);
        var config = codec.DecodeInvocation(
            request.Input,
            wireSink,
            bucketResolver ?? ThrowUnknownBucketReference);
        LongTextWritingWireCodec.Apply(expert, config);
        expert.Bind(context);

        var completion = await expert.StreamAsync(cancellationToken).ConfigureAwait(false);
        return codec.EncodeCompletion(completion, config);
    }

    /// <summary>
    /// 匹配 <see cref="ExpertSessionBinding.Invoker"/> 委托形状的适配入口：接收未绑定实例并完成
    /// 一次结构化调用。组合引擎经此委托驱动长文本写作类别。
    /// </summary>
    /// <param name="expert">未绑定的具体专家实例。</param>
    /// <param name="request">结构化调用请求。</param>
    /// <param name="events">语义事件出口。</param>
    /// <param name="context">执行上下文（绑定到本次专家实例）。</param>
    /// <param name="bucketResolver">按 bucketId 解析历史桶引用的委托。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static Task<JsonElement> InvokeBindingAsync(
        ExpertBase expert,
        ExpertInvocationRequest request,
        IExpertSemanticEventSink events,
        IExpertExecutionContext context,
        Func<string, IHistoryBucket>? bucketResolver,
        CancellationToken cancellationToken) =>
        expert is AbstractLongTextWritingExpert longTextWriting
            ? InvokeAsync(longTextWriting, request, events, context, bucketResolver, cancellationToken)
            : throw new InvalidOperationException(
                $"The long-text-writing invoker received an incompatible expert type '{expert.GetType().FullName}'.");

    private static IHistoryBucket ThrowUnknownBucketReference(string bucketId)
    {
        throw new InvalidOperationException(
            $"No bucket resolver was provided; the reference bucket '{bucketId}' cannot be resolved.");
    }

    /// <summary>把 codec 的 wire 事件发射适配到结构化调用的语义事件出口。</summary>
    private sealed class SemanticEventWireSink(IExpertSemanticEventSink events) : IWireEventSink
    {
        public ValueTask SendAsync(
            string eventType,
            JsonElement payload,
            CancellationToken cancellationToken = default) =>
            events.WriteAsync(new ExpertSemanticEvent(eventType, payload), cancellationToken);
    }
}
