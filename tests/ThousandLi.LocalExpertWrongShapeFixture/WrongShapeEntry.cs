using ThousandLi.Contracts;
using ThousandLi.ExpertContracts.Narration;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractNarratorExpert),
    typeof(ThousandLi.LocalExpertWrongShapeFixture.NotAnExpert))]

namespace ThousandLi.LocalExpertWrongShapeFixture;

public sealed class NotAnExpert;
