using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class ContractVersionTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    public void ConstructorRejectsNonPositiveMajor(int major, int minor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContractVersion(major, minor));
    }

    [Fact]
    public void ConstructorRejectsNegativeMinor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContractVersion(1, -1));
    }

    [Theory]
    [InlineData(1, 1, 1, 0)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(2, 3, 2, 1)]
    [InlineData(2, 0, 2, 0)]
    public void SupportsAcceptsEqualOrLowerMinorWithinSameMajor(
        int availableMajor, int availableMinor, int requiredMajor, int requiredMinor)
    {
        var available = new ContractVersion(availableMajor, availableMinor);
        var required = new ContractVersion(requiredMajor, requiredMinor);

        Assert.True(available.Supports(required));
    }

    [Fact]
    public void SupportsRejectsHigherMinorWithinSameMajor()
    {
        var available = new ContractVersion(1, 1);
        var required = new ContractVersion(1, 2);

        Assert.False(available.Supports(required));
    }

    [Theory]
    [InlineData(1, 1, 2, 0)]
    [InlineData(2, 0, 1, 9)]
    public void SupportsRejectsDifferentMajor(
        int availableMajor, int availableMinor, int requiredMajor, int requiredMinor)
    {
        var available = new ContractVersion(availableMajor, availableMinor);
        var required = new ContractVersion(requiredMajor, requiredMinor);

        Assert.False(available.Supports(required));
    }

    [Fact]
    public void RuntimeContractSupportsSlice1RuntimeRequirements()
    {
        var slice1Runtime = new ContractVersion(1, 0);

        Assert.True(SdkContracts.Runtime.Supports(slice1Runtime));
        Assert.True(SdkContracts.Runtime.Supports(SdkContracts.Runtime));
        Assert.False(SdkContracts.Runtime.Supports(new ContractVersion(2, 0)));
    }
}
