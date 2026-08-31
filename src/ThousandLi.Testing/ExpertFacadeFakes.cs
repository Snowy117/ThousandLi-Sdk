using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThousandLi.Contracts;

namespace ThousandLi.Testing;

/// <summary>
/// 类型化专家 facade 的可配置 fake：按抽象专家类别注册工厂，<c>Use&lt;TAbstract&gt;()</c>
/// 每次调用都通过工厂返回新实例（请求作用域语义，与生产 facade 一致）。
/// <see cref="Register{TAbstract,TConcrete}" /> 便捷重载会自动把
/// <see cref="FakeExpertExecutionContext" /> 绑定到新实例；自定义工厂闭包也可自行调用
/// 专家的公共 <c>Bind(IExpertExecutionContext)</c> 后返回。
/// </summary>
public sealed class FakeExpertFacade(IExpertExecutionContext? executionContext = null) : IExpertFacade
{
    private readonly IExpertExecutionContext _executionContext =
        executionContext ?? FakeExpertExecutionContext.Instance;

    private readonly Dictionary<Type, Func<ExpertBase>> _factories = [];

    /// <summary>为抽象专家类别 <typeparamref name="TAbstract" /> 注册实例工厂。</summary>
    /// <typeparam name="TAbstract">抽象专家类别（如 <c>AbstractLongTextWritingExpert</c>）。</typeparam>
    /// <param name="factory">每次 <c>Use</c> 调用时执行；返回 null 会在 <c>Use</c> 时抛出。</param>
    public void Register<TAbstract>(Func<TAbstract> factory)
        where TAbstract : ExpertBase
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[typeof(TAbstract)] = factory;
    }

    /// <summary>注册一个公共无参构造的具体专家类型；<c>Use</c> 时创建新实例并绑定执行上下文。</summary>
    /// <typeparam name="TAbstract">抽象专家类别。</typeparam>
    /// <typeparam name="TConcrete">具体专家实现（须有公共无参构造）。</typeparam>
    public void Register<TAbstract, TConcrete>()
        where TAbstract : ExpertBase
        where TConcrete : TAbstract, new()
    {
        Register<TAbstract>(() =>
        {
            var expert = new TConcrete();
            expert.Bind(_executionContext);
            return expert;
        });
    }

    /// <inheritdoc />
    public TAbstract Use<TAbstract>()
        where TAbstract : ExpertBase
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
/// 测试用专家执行上下文：<c>BasicAi</c> 访问一律失败（假专家不应真正调用模型），
/// 设置解析返回默认实例，玩家档案与日志器为固定假值。
/// </summary>
public sealed class FakeExpertExecutionContext : IExpertExecutionContext
{
    /// <summary>共享实例（无状态）。</summary>
    public static FakeExpertExecutionContext Instance { get; } = new();

    private FakeExpertExecutionContext()
    {
    }

    /// <inheritdoc />
    public IRuntimeBasicAi BasicAi => throw new NotSupportedException(
        "Fake experts never invoke a model; provide a custom IExpertExecutionContext to test BasicAi flows.");

    /// <inheritdoc />
    public ValueTask<TSettings> GetExpertSettingsAsync<TSettings>(
        CancellationToken cancellationToken = default)
        where TSettings : class, new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TSettings());
    }

    /// <inheritdoc />
    public BoundPlayerProfile PlayerProfile => new(
        new PlayerId("test-player"),
        "TestPlayer",
        "Test persona");

    /// <inheritdoc />
    public ILogger Logger => NullLogger.Instance;
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
        where TAbstract : ExpertBase
    {
        throw new InvalidOperationException(
            $"Typed expert facade '{typeof(TAbstract).FullName}' is not configured for this test context.");
    }
}
