using ThousandLi.Contracts;

[assembly: ExpertPackageEntryPoint(
    typeof(ThousandLi.LocalExpertBadAnchorFixture.ForeignAbstractExpert),
    typeof(ThousandLi.LocalExpertBadAnchorFixture.ForeignConcreteExpert))]

namespace ThousandLi.LocalExpertBadAnchorFixture;

/// <summary>不继承 <see cref="ExpertBase"/> 的坏形状锚（装载校验应拒绝）。</summary>
[ExpertContract("tests.bad/anchor")]
public abstract class ForeignAbstractExpert;

/// <summary>坏形状锚的具体类（同样不在专家类型树内）。</summary>
public sealed class ForeignConcreteExpert : ForeignAbstractExpert;
