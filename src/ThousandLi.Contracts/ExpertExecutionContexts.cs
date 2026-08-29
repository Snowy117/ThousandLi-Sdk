using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

namespace ThousandLi.Contracts;

/// <summary>
/// The restricted execution context held by a bound concrete expert: exactly four capabilities
/// (<see cref="BasicAi"/>, <see cref="GetExpertSettingsAsync{TSettings}"/>,
/// <see cref="PlayerProfile"/>, <see cref="Logger"/>). The full game-backend execution context is
/// deliberately absent so game-backend concerns stay unreachable at compile time from expert code.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public interface IExpertExecutionContext
{
    IRuntimeBasicAi BasicAi { get; }

    /// <summary>
    /// Returns the typed effective settings resolved by the composition root (local two-layer
    /// policy in the SDK, four-layer platform policy in production), resolved once per context.
    /// </summary>
    ValueTask<TSettings> GetExpertSettingsAsync<TSettings>(CancellationToken cancellationToken = default)
        where TSettings : class, new();

    BoundPlayerProfile PlayerProfile { get; }

    ILogger Logger { get; }
}

/// <summary>
/// The execution-participant view of a bound concrete expert: the exact state the
/// <c>ExpertExecution</c> tool reads. Declared as an interface so the execution tool stays decoupled
/// from any specific expert base class.
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
