using ThousandLi.Contracts;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractLongTextWritingExpert),
    typeof(ThousandLi.LocalExpertWrongShapeFixture.NotAnExpert))]

namespace ThousandLi.LocalExpertWrongShapeFixture;

public sealed class NotAnExpert;
