using ThousandLi.Contracts;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertContractFingerprintTests
{
    [Fact]
    public void SameDefinitionProducesTheSameFingerprintAcrossComputations()
    {
        var first = new ExpertContractDefinition(
            inputSchema: TestSupport.Json("""{"type":"object","properties":{"turn":{"type":"integer"}}}"""),
            semanticEventTypes: ["chunk", "completed"]);
        var second = new ExpertContractDefinition(
            inputSchema: TestSupport.Json("""{"type":"object","properties":{"turn":{"type":"integer"}}}"""),
            semanticEventTypes: ["chunk", "completed"]);

        const string id = "tests.acme/story";
        var version = new ContractVersion(1, 0);

        Assert.Equal(
            ExpertContractFingerprint.Compute(id, version, first),
            ExpertContractFingerprint.Compute(id, version, second));
    }

    [Fact]
    public void SchemaPropertyOrderDoesNotChangeTheFingerprint()
    {
        var ordered = new ExpertContractDefinition(
            inputSchema: TestSupport.Json("""{"alpha":1,"beta":{"nested":true,"deep":[1,2]}}"""));
        var reordered = new ExpertContractDefinition(
            inputSchema: TestSupport.Json("""{"beta":{"deep":[1,2],"nested":true},"alpha":1}"""));

        Assert.Equal(
            ExpertContractFingerprint.Compute("tests.acme/order", new ContractVersion(1, 0), ordered),
            ExpertContractFingerprint.Compute("tests.acme/order", new ContractVersion(1, 0), reordered));
    }

    [Fact]
    public void StringEscapeStyleDoesNotChangeTheFingerprint()
    {
        var escaped = new ExpertContractDefinition(
            inputSchema: TestSupport.Json("""{"description":"\u0061 story"}"""));
        var plain = new ExpertContractDefinition(
            inputSchema: TestSupport.Json("""{"description":"a story"}"""));

        Assert.Equal(
            ExpertContractFingerprint.Compute("tests.acme/escape", new ContractVersion(1, 0), escaped),
            ExpertContractFingerprint.Compute("tests.acme/escape", new ContractVersion(1, 0), plain));
    }

    [Fact]
    public void SemanticEventDeclarationOrderDoesNotChangeTheFingerprint()
    {
        var first = new ExpertContractDefinition(semanticEventTypes: ["chunk", "reasoning"]);
        var second = new ExpertContractDefinition(semanticEventTypes: ["reasoning", "chunk"]);

        Assert.Equal(
            ExpertContractFingerprint.Compute("tests.acme/events", new ContractVersion(1, 0), first),
            ExpertContractFingerprint.Compute("tests.acme/events", new ContractVersion(1, 0), second));
    }

    [Fact]
    public void DifferentDefinitionsProduceDifferentFingerprints()
    {
        const string id = "tests.acme/story";
        var version = new ContractVersion(1, 0);
        var minimal = new ExpertContractDefinition();
        var withEvents = new ExpertContractDefinition(semanticEventTypes: ["chunk"]);

        Assert.NotEqual(
            ExpertContractFingerprint.Compute(id, version, minimal),
            ExpertContractFingerprint.Compute(id, version, withEvents));
    }

    [Fact]
    public void DifferentIdOrVersionProduceDifferentFingerprints()
    {
        var definition = new ExpertContractDefinition();

        Assert.NotEqual(
            ExpertContractFingerprint.Compute("tests.acme/one", new ContractVersion(1, 0), definition),
            ExpertContractFingerprint.Compute("tests.acme/two", new ContractVersion(1, 0), definition));
        Assert.NotEqual(
            ExpertContractFingerprint.Compute("tests.acme/one", new ContractVersion(1, 0), definition),
            ExpertContractFingerprint.Compute("tests.acme/one", new ContractVersion(1, 1), definition));
    }

    [Fact]
    public void FingerprintIsSixtyFourLowercaseHexCharacters()
    {
        var fingerprint = ExpertContractFingerprint.Compute(
            "tests.acme/story", new ContractVersion(1, 0), new ExpertContractDefinition());

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, static c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void ComputeRejectsNullId()
    {
        Assert.Throws<ArgumentNullException>(
            () => ExpertContractFingerprint.Compute(null!, new ContractVersion(1, 0), new ExpertContractDefinition()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ComputeRejectsBlankId(string id)
    {
        Assert.Throws<ArgumentException>(
            () => ExpertContractFingerprint.Compute(id, new ContractVersion(1, 0), new ExpertContractDefinition()));
    }

    [Fact]
    public void ComputeRejectsMissingDefinition()
    {
        Assert.Throws<ArgumentNullException>(
            () => ExpertContractFingerprint.Compute("tests.acme/story", new ContractVersion(1, 0), null!));
    }

    [Fact]
    public void ArrayOrderChangesTheFingerprint()
    {
        var ascending = new ExpertContractDefinition(inputSchema: TestSupport.Json("""{"steps":[1,2,3]}"""));
        var descending = new ExpertContractDefinition(inputSchema: TestSupport.Json("""{"steps":[3,2,1]}"""));

        Assert.NotEqual(
            ExpertContractFingerprint.Compute("tests.acme/array", new ContractVersion(1, 0), ascending),
            ExpertContractFingerprint.Compute("tests.acme/array", new ContractVersion(1, 0), descending));
    }

    [Fact]
    public void NumberRawTextDifferencesChangeTheFingerprint()
    {
        var one = new ExpertContractDefinition(outputSchema: TestSupport.Json("""{"limit":1}"""));
        var onePointZero = new ExpertContractDefinition(outputSchema: TestSupport.Json("""{"limit":1.0}"""));
        var exponent = new ExpertContractDefinition(outputSchema: TestSupport.Json("""{"limit":1e2}"""));
        var version = new ContractVersion(1, 0);

        Assert.NotEqual(
            ExpertContractFingerprint.Compute("tests.acme/number", version, one),
            ExpertContractFingerprint.Compute("tests.acme/number", version, onePointZero));
        Assert.NotEqual(
            ExpertContractFingerprint.Compute("tests.acme/number", version, one),
            ExpertContractFingerprint.Compute("tests.acme/number", version, exponent));
    }

    [Fact]
    public void NonAsciiStringEscapeStyleDoesNotChangeTheFingerprint()
    {
        var escaped = new ExpertContractDefinition(inputSchema: TestSupport.Json("""{"title":"\u53d9"}"""));
        var literal = new ExpertContractDefinition(inputSchema: TestSupport.Json("""{"title":"叙"}"""));

        Assert.Equal(
            ExpertContractFingerprint.Compute("tests.acme/unicode", new ContractVersion(1, 0), escaped),
            ExpertContractFingerprint.Compute("tests.acme/unicode", new ContractVersion(1, 0), literal));
    }

    [Fact]
    public void JsonNullSchemaCanonicalizesLikeAnAbsentSchema()
    {
        var explicitNullSchema = new ExpertContractDefinition(inputSchema: TestSupport.Json("null"));
        var absentSchema = new ExpertContractDefinition();

        Assert.Equal(
            ExpertContractFingerprint.Compute("tests.acme/null-schema", new ContractVersion(1, 0), explicitNullSchema),
            ExpertContractFingerprint.Compute("tests.acme/null-schema", new ContractVersion(1, 0), absentSchema));
    }
}
