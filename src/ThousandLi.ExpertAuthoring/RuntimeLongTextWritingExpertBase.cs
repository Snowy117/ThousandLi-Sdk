using JetBrains.Annotations;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>
/// The execution-participant view of a concrete expert: the exact state <see cref="ExpertExecution"/>
/// reads. Declared as an interface so runtime-facing experts from either authoring universe can opt
/// in without coupling the execution tool to a specific base class.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IExpertExecutionParticipant
{
    /// <summary>The bound runtime BasicAi; never accessed before the participant is bound.</summary>
    IRuntimeBasicAi BasicAi { get; }

    /// <summary>Root property names the execution tool captures as turn metadata.</summary>
    IReadOnlySet<string> MetadataFieldNames { get; }

    /// <summary>The reasoning handler registered through <c>WithReasoningHandler</c>, if any.</summary>
    Func<ReasoningDeltaEvent, CancellationToken, ValueTask>? ReasoningHandler { get; }
}

/// <summary>
/// Runtime-facing base for concrete long-text-writing experts executed through
/// <see cref="ExpertExecution"/>. Extends the game-facing category contract from
/// <see cref="ThousandLi.Contracts"/> with the bound <see cref="IExpertExecutionContext"/> and the
/// <see cref="MetadataFieldNames"/> declaration surface. Instances are created unbound; the executor
/// or facade binds an execution context exactly once before invocation.
/// </summary>
public abstract class RuntimeLongTextWritingExpertBase : AbstractLongTextWritingExpert, IExpertExecutionParticipant
{
    private static readonly IReadOnlySet<string> EmptyMetadataFields = new HashSet<string>(StringComparer.Ordinal);

    private IExpertExecutionContext? _executionContext;

    /// <summary>The bound execution context; throws when the instance has not been bound yet.</summary>
    protected IExpertExecutionContext RuntimeContext =>
        _executionContext ?? throw new InvalidOperationException(
            "The expert instance has not been bound to an execution context. Expert instances must be created through a factory or facade that binds an IExpertExecutionContext before execution.");

    /// <summary>
    /// Binds the execution context to this instance, exactly once, before invocation. Callers are the
    /// facades/executors that hand runtime experts to game code: the production Host facade, local
    /// composition roots, and test doubles (for example the <c>ThousandLi.Testing</c> fake facade
    /// pattern where the registered factory creates, binds, and returns the expert).
    /// </summary>
    public void Bind(IExpertExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_executionContext is not null)
            throw new InvalidOperationException(
                "This expert instance is already bound to an execution context; expert instances must not be rebound.");
        _executionContext = context;
    }

    /// <summary>
    /// Root property names captured as turn metadata; concrete experts override to declare their
    /// intrinsic metadata fields (for example <c>afterThinking</c>/<c>afterFormat</c>).
    /// </summary>
    protected internal virtual IReadOnlySet<string> MetadataFieldNames => EmptyMetadataFields;

    IRuntimeBasicAi IExpertExecutionParticipant.BasicAi => RuntimeContext.BasicAi;

    IReadOnlySet<string> IExpertExecutionParticipant.MetadataFieldNames => MetadataFieldNames;

    Func<ReasoningDeltaEvent, CancellationToken, ValueTask>? IExpertExecutionParticipant.ReasoningHandler =>
        ReasoningHandler;
}
