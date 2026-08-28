using System.Text.Json;
using ThousandLi.ExpertContracts.Narration;

namespace ThousandLi.Sdk.Tests;

/// <summary>
/// Static contract tests for the packed <c>dotnet new</c> templates (AC9). Running the full
/// instantiation loop requires a local package feed and is documented in the README; these tests pin
/// the template identities, placeholder wiring, manifest shape, entry-point binding, and packing
/// registration that the generated game+expert joint run depends on.
/// </summary>
public sealed class TemplatePackageTests
{
    private static string TemplatesRoot =>
        Path.Combine(TestSupport.FindRepositoryRoot(), "templates");

    [Fact]
    public void GameTemplateKeepsItsPublishedIdentity()
    {
        var root = ReadTemplateConfig("ThousandLi.Game");

        Assert.Equal("ThousandLi.Templates.Game", RequiredProperty(root, "identity"));
        Assert.Equal("thousandli-game", RequiredProperty(root, "shortName"));
        Assert.Equal("ThousandLi.TemplateName", RequiredProperty(root, "sourceName"));
    }

    [Fact]
    public void ExpertTemplateDeclaresItsIdentityAndPlaceholderSymbols()
    {
        var root = ReadTemplateConfig("ThousandLi.Expert");

        Assert.Equal("ThousandLi.Templates.Expert", RequiredProperty(root, "identity"));
        Assert.Equal("thousandli-expert", RequiredProperty(root, "shortName"));
        Assert.Equal("ThousandLi.TemplateName", RequiredProperty(root, "sourceName"));
        Assert.Equal(
            "ThousandLi.TemplateAuthor",
            RequiredProperty(Symbol(root, "authorId"), "replaces"));
        Assert.Equal(
            "ThousandLi.TemplatePackage",
            RequiredProperty(Symbol(root, "packageName"), "replaces"));
    }

    [Fact]
    public void ExpertTemplateManifestIsAnExpertPackageBuiltFromPlaceholders()
    {
        using var document = JsonDocument.Parse(ReadTemplateFile("ThousandLi.Expert", "package.json"));
        var root = document.RootElement;

        Assert.Equal("ExpertPackage", RequiredProperty(root, "packageKind"));
        Assert.Equal("bin/ThousandLi.TemplateName.dll", RequiredProperty(root, "entryAssembly"));
        Assert.Equal("ThousandLi.TemplateAuthor", RequiredProperty(root, "authorId"));
        Assert.Equal("ThousandLi.TemplatePackage", RequiredProperty(root, "packageName"));
        Assert.False(
            root.TryGetProperty("compatibility", out _),
            "expert manifests bind contracts through the entry-point attribute, not a private compatibility copy");
    }

    [Fact]
    public void ExpertTemplateBindsTheOfficialNarratorContractThroughTheEntryPointAttribute()
    {
        var assemblyInfo = ReadTemplateFile("ThousandLi.Expert", "AssemblyInfo.cs");
        var expertSource = ReadTemplateFile("ThousandLi.Expert", "NarratorExpert.cs");

        Assert.Contains("[assembly: ExpertPackageEntryPoint(", assemblyInfo);
        Assert.Contains("typeof(AbstractNarratorExpert)", assemblyInfo);
        Assert.Contains("typeof(ThousandLi.TemplateName.NarratorExpert)", assemblyInfo);
        Assert.Contains(": AbstractNarratorExpert", expertSource);
    }

    [Fact]
    public void ExpertTemplateBindsRequiredInputPropertiesWithActionableDiagnostics()
    {
        var expertSource = ReadTemplateFile("ThousandLi.Expert", "NarratorExpert.cs");

        Assert.Contains("RequiredInputProperty(\"turn\")", expertSource, StringComparison.Ordinal);
        Assert.Contains("RequiredInputProperty(\"player\")", expertSource, StringComparison.Ordinal);
        Assert.Contains("RequiredInputProperty(\"action\")", expertSource, StringComparison.Ordinal);
        Assert.Contains("turn:int, player:string, action:*", expertSource, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedGameAndExpertPairOnTheSameOfficialNarratorContract()
    {
        using var gameManifest = JsonDocument.Parse(ReadTemplateFile("ThousandLi.Game", "package.json"));
        var required = gameManifest.RootElement
            .GetProperty("compatibility")
            .GetProperty("expertContracts")[0];

        Assert.Equal(AbstractNarratorExpert.Descriptor.Id, required.GetProperty("id").GetString());
        Assert.Equal(
            AbstractNarratorExpert.Descriptor.Fingerprint,
            required.GetProperty("fingerprint").GetString());
        Assert.Equal(
            AbstractNarratorExpert.Descriptor.Version.Major,
            required.GetProperty("version").GetProperty("major").GetInt32());
        Assert.Equal(
            AbstractNarratorExpert.Descriptor.Version.Minor,
            required.GetProperty("version").GetProperty("minor").GetInt32());
    }

    [Fact]
    public void ExpertTemplateProjectProducesAnExpertPackageArtifactFromReleasedPackages()
    {
        var project = ReadTemplateFile("ThousandLi.Expert", "ThousandLi.TemplateName.csproj");

        Assert.Contains("<IsThousandLiPackageArtifact>true</IsThousandLiPackageArtifact>", project);
        Assert.Contains("<IsThousandLiExpertPackageArtifact>true</IsThousandLiExpertPackageArtifact>", project);
        Assert.Contains("<PackageReference Include=\"ThousandLi.Contracts\" Version=\"0.3.0-preview.1\" />", project);
        Assert.Contains("<PackageReference Include=\"ThousandLi.ExpertContracts\" Version=\"0.3.0-preview.1\" />", project);
    }

    [Fact]
    public void ExpertTemplateShipsASolutionWiringProjectAndTests()
    {
        var solution = ReadTemplateFile("ThousandLi.Expert", "ThousandLi.TemplateName.slnx");

        Assert.Contains("ThousandLi.TemplateName.csproj", solution);
        Assert.Contains("tests/ThousandLi.TemplateName.Tests.csproj", solution);
    }

    [Fact]
    public void TemplatesPackingRegistersBothTemplates()
    {
        var packing = File.ReadAllText(
            Path.Combine(TestSupport.FindRepositoryRoot(), "templates", "ThousandLi.Templates.csproj"));

        Assert.Contains("Include=\"ThousandLi.Game/**/*\"", packing);
        Assert.Contains("Include=\"ThousandLi.Expert/**/*\"", packing);
    }

    [Fact]
    public void ExpertContractsPackageCarriesTheSharedArtifactTargetsForExpertOnlyAuthors()
    {
        var project = File.ReadAllText(Path.Combine(
            TestSupport.FindRepositoryRoot(),
            "src",
            "ThousandLi.ExpertContracts",
            "ThousandLi.ExpertContracts.csproj"));

        Assert.Contains("../../eng/ThousandLi.GameAuthoring.targets", project);
        Assert.Contains("buildTransitive/ThousandLi.ExpertContracts.targets", project);
    }

    private static JsonElement ReadTemplateConfig(string templateName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TemplatesRoot, templateName, ".template.config", "template.json")));
        return document.RootElement.Clone();
    }

    private static string ReadTemplateFile(string templateName, params string[] relativePath) =>
        File.ReadAllText(Path.Combine([TemplatesRoot, templateName, "content", .. relativePath]));

    private static JsonElement Symbol(JsonElement templateRoot, string symbolName) =>
        templateRoot.GetProperty("symbols").GetProperty(symbolName);

    private static string RequiredProperty(JsonElement element, string propertyName)
    {
        Assert.True(element.TryGetProperty(propertyName, out var value), $"Missing property '{propertyName}'.");
        return value.GetString()!;
    }
}
