using System.Reflection;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

/// <summary>
/// Game-facing typed expert facade for the local DevHost mode. Structurally identical to the
/// production Host facade: <see cref="Use{TAbstract}"/> resolves the abstract category anchor's
/// contract id, asks the local composition to create and bind a fresh expert instance from the
/// configured Expert Package, and returns the fluent instance to game code.
/// </summary>
public sealed class LocalExpertFacade : IExpertFacade
{
    private readonly LocalExpertComposition _composition;

    /// <summary>以本地执行组合根构造 facade。</summary>
    /// <param name="composition">本地专家执行组合（提供契约解析与专家实例创建）。</param>
    public LocalExpertFacade(LocalExpertComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _composition = composition;
    }

    /// <summary>
    /// 解析类别锚类型：读取其 <c>[ExpertContract]</c> 契约身份，交由本地组合创建并绑定
    /// 一个新的具体专家实例，返回给游戏代码继续 fluent 配置。
    /// </summary>
    /// <typeparam name="TAbstract">抽象类别锚类型（必须带 <c>[ExpertContract]</c>）。</typeparam>
    public TAbstract Use<TAbstract>() where TAbstract : AbstractLongTextWritingExpert
    {
        var contractId = typeof(TAbstract).GetCustomAttribute<ExpertContractAttribute>()?.Id
            ?? throw new InvalidOperationException(
                $"The expert category type '{typeof(TAbstract).FullName}' does not carry '[ExpertContract]'; " +
                "the local facade resolves packages through the category's contract identity.");
        return (TAbstract)_composition.CreateExpert(contractId);
    }
}
