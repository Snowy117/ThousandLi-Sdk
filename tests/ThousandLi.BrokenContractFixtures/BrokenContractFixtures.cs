using ThousandLi.ExpertContracts;

using JetBrains.Annotations;

namespace ThousandLi.BrokenContractFixtures;

// Every fixture contract below is intentionally malformed and is discovered only through the
// [ExpertContract] assembly scan performed by ExpertContractRegistry tests; no code references
// these types directly.

public static class FixtureAssembly;

[ExpertContract("fixtures.example/duplicate", 1, 0, "6cc2764016dca0ec8dc3bbab31b036d47e34b2c25af213cbac3d528acb90351d")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal abstract class DuplicateContractOne : IExpertContract
{
    public static ExpertContractDefinition Definition { get; } = new();
}

[ExpertContract("fixtures.example/duplicate", 1, 0, "6cc2764016dca0ec8dc3bbab31b036d47e34b2c25af213cbac3d528acb90351d")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal abstract class DuplicateContractTwo : IExpertContract
{
    public static ExpertContractDefinition Definition { get; } = new();
}

[ExpertContract("fixtures.example/not-abstract", 1, 0, "d9833b3afb407cfe7dd702f5f79ea92866eddb6ea8231c40d814f1ff52311f99")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class NotAbstractContract : IExpertContract
{
    public static ExpertContractDefinition Definition { get; } = new();
}

[ExpertContract("fixtures.example/missing-definition", 1, 0, "unreachable-fingerprint-validation")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal abstract class MissingDefinitionContract;

[ExpertContract("thousandli.expert/squatter", 1, 0, "0c3e8b1520c3d457897ef6f1e465fc885ee880522098903d37a12fc03a4ba533")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal abstract class OfficialNamespaceSquatterContract : IExpertContract
{
    public static ExpertContractDefinition Definition { get; } = new();
}

[ExpertContract("fixtures.example/bad-fingerprint", 1, 0, "declared-but-not-computed")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal abstract class BadFingerprintContract : IExpertContract
{
    public static ExpertContractDefinition Definition { get; } = new();
}

[ExpertContract("fixtures.example/Bad_Name", 1, 0, "3cecb4ad6902ecb64a02c3ded65748ec45911dd2b363d1ae9c93fe957ce77643")]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal abstract class BadIdFormatContract : IExpertContract
{
    public static ExpertContractDefinition Definition { get; } = new();
}
