using ThousandLi.Contracts;

namespace ThousandLi.Testing;

/// <summary>
/// 类型化专家 facade 的可配置 fake：按抽象专家类别注册工厂，<c>Use&lt;TAbstract&gt;()</c>
/// 每次调用都通过工厂返回新实例（请求作用域语义，与生产 facade 一致）。
/// 工厂闭包内自行完成运行时上下文绑定——对
/// <c>ThousandLi.ExpertAuthoring.RuntimeLongTextWritingExpertBase</c> 派生的专家，
/// 在闭包里 <c>new</c> 之后调用其公共 <c>Bind(IExpertExecutionContext)</c> 再返回。
/// </summary>
public sealed class FakeExpertFacade : IExpertFacade
{
    private readonly Dictionary<Type, Func<AbstractLongTextWritingExpert>> _factories = [];

    /// <summary>为抽象专家类别 <typeparamref name="TAbstract" /> 注册实例工厂。</summary>
    /// <typeparam name="TAbstract">抽象专家类别（如 <c>AbstractLongTextWritingExpert</c>）。</typeparam>
    /// <param name="factory">每次 <c>Use</c> 调用时执行；返回 null 会在 <c>Use</c> 时抛出。</param>
    public void Register<TAbstract>(Func<TAbstract> factory)
        where TAbstract : AbstractLongTextWritingExpert
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[typeof(TAbstract)] = factory;
    }

    /// <inheritdoc />
    public TAbstract Use<TAbstract>()
        where TAbstract : AbstractLongTextWritingExpert
    {
        if (!_factories.TryGetValue(typeof(TAbstract), out var factory))
        {
            throw new InvalidOperationException(
                $"No Expert implementation is registered for abstract expert '{typeof(TAbstract).FullName}'.");
        }

        var expert = factory() ?? throw new InvalidOperationException(
            $"Expert factory for abstract expert '{typeof(TAbstract).FullName}' returned null.");
        return (TAbstract)expert;
    }
}

/// <summary>
/// 未注册任何专家时的禁用 facade：任何 <c>Use&lt;T&gt;()</c> 都抛
/// <see cref="InvalidOperationException" />，避免静默返回空实现。作为测试上下文的默认注入。
/// </summary>
public sealed class ThrowingExpertFacade : IExpertFacade
{
    /// <summary>共享实例。</summary>
    public static ThrowingExpertFacade Instance { get; } = new();

    private ThrowingExpertFacade()
    {
    }

    /// <inheritdoc />
    public TAbstract Use<TAbstract>()
        where TAbstract : AbstractLongTextWritingExpert
    {
        throw new InvalidOperationException(
            $"Typed expert facade '{typeof(TAbstract).FullName}' is not configured for this test context.");
    }
}
