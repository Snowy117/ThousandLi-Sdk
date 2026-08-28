using System.ComponentModel.DataAnnotations;
using JetBrains.Annotations;
using ThousandLi.ExpertAuthoring;

namespace ThousandLi.UserConfigurableSettingsFixture;

/// <summary>
/// The single legal user-configurable settings type of this fixture assembly; consumed through
/// <c>UserConfigurableSettingsContract.Discover</c> to exercise the single-marked-type success path.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
[UserConfigurableSettings]
public sealed class FixtureSettings
{
    [UserConfigurableSetting("Enabled", "Feature toggle", "Whether the fixture feature is enabled.")]
    public bool Enabled { get; set; } = true;

    [UserConfigurableSetting("Precision", "Sampling precision", "Controls the sampling precision.")]
    public decimal Precision { get; set; } = 0.75m;

    [UserConfigurableSetting("Ratio", "Sampling ratio", "Controls the sampling ratio.")]
    public double Ratio { get; set; } = 0.5;

    [UserConfigurableSetting("Style", "Narrative style", "Controls the narrative style of generated text.")]
    public string Style { get; set; } = "balanced";

    [UserConfigurableSetting("Tone", "Narrative tone", "Selects the narrative tone.")]
    public FixtureTone Tone { get; set; } = FixtureTone.Warm;

    [UserConfigurableSetting("Verbosity", "Narrative verbosity", "Controls how verbose generated text is.")]
    public int Verbosity { get; set; } = 3;
}

/// <summary>Tone options with explicit values and display annotations.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public enum FixtureTone
{
    [Display(Name = "Neutral", Description = "Plain narration.")]
    Neutral = 0,

    [Display(Name = "Warm", Description = "Warm-hearted narration.")]
    Warm = 1,

    [Display(Name = "Cold", Description = "Detached narration.")]
    Cold = 2,
}
