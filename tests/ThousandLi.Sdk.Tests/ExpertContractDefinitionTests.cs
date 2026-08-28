using System.Text.Json;
using ThousandLi.ExpertContracts;

namespace ThousandLi.Sdk.Tests;

public sealed class ExpertContractDefinitionTests
{
    [Fact]
    public void NullSemanticEventTypesYieldAnEmptyList()
    {
        var definition = new ExpertContractDefinition();

        Assert.NotNull(definition.SemanticEventTypes);
        Assert.Empty(definition.SemanticEventTypes);
    }

    [Fact]
    public void NullEventTypesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ExpertContractDefinition(semanticEventTypes: [null!]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankEventTypesAreRejected(string eventType)
    {
        Assert.Throws<ArgumentException>(
            () => new ExpertContractDefinition(semanticEventTypes: [eventType]));
    }

    [Fact]
    public void DuplicateEventTypesAreRejected()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ExpertContractDefinition(semanticEventTypes: ["chunk", "chunk"]));

        Assert.Contains("unique", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndefinedSchemasAreRejected(bool rejectInput)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => rejectInput
                ? new ExpertContractDefinition(inputSchema: default(JsonElement))
                : new ExpertContractDefinition(outputSchema: default(JsonElement)));

        Assert.Contains("undefined", exception.Message);
    }

    [Fact]
    public void SchemasAreDefensivelyClonedFromTheSourceDocument()
    {
        var document = JsonDocument.Parse("""{"type":"object"}""");
        var definition = new ExpertContractDefinition(inputSchema: document.RootElement);
        document.Dispose();

        Assert.Equal(JsonValueKind.Object, definition.InputSchema!.Value.ValueKind);
        Assert.Equal("object", definition.InputSchema!.Value.GetProperty("type").GetString());
    }
}
