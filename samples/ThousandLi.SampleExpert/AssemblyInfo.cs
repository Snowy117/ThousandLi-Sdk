using ThousandLi.Contracts;
using ThousandLi.ExpertContracts.Narration;

[assembly: ExpertPackageEntryPoint(
    typeof(AbstractNarratorExpert),
    typeof(ThousandLi.SampleExpert.SampleNarratorExpert))]
