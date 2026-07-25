namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for EclipseModeRules — the pure decision table behind the three-way eclipse mode
/// radio (DESIGN.md §10). These pin the truth table the Verse adapters (trigger, suppression patch,
/// darkening patch, probe) all route through, so a mode can never mean different things in different
/// places. In particular: Both fires the geometric trigger yet keeps the storyteller's random eclipse,
/// and renders each active eclipse by whether we fired it.
/// </summary>
[TestFixture]
public class EclipseModeRulesTests
{
    // The geometric trigger runs for the two modes that include natural eclipses, not for unnatural-only.
    [TestCase(EclipseMode.NaturalOnly, true)]
    [TestCase(EclipseMode.Both, true)]
    [TestCase(EclipseMode.UnnaturalOnly, false)]
    public void NaturalTriggerActive_MatchesMode(EclipseMode mode, bool expected)
    {
        Assert.That(EclipseModeRules.NaturalTriggerActive(mode), Is.EqualTo(expected));
    }

    // Only NaturalOnly stands the storyteller's random eclipse down; Both deliberately keeps it.
    [TestCase(EclipseMode.NaturalOnly, true)]
    [TestCase(EclipseMode.Both, false)]
    [TestCase(EclipseMode.UnnaturalOnly, false)]
    public void SuppressRandomEclipse_OnlyInNaturalOnly(EclipseMode mode, bool expected)
    {
        Assert.That(EclipseModeRules.SuppressRandomEclipse(mode), Is.EqualTo(expected));
    }

    // Single-flavour modes render every eclipse that flavour regardless of who fired it; Both defers to
    // whether the condition is one of ours (geometric => natural, storyteller => unnatural).
    [TestCase(EclipseMode.NaturalOnly, true, true)]
    [TestCase(EclipseMode.NaturalOnly, false, true)]
    [TestCase(EclipseMode.UnnaturalOnly, true, false)]
    [TestCase(EclipseMode.UnnaturalOnly, false, false)]
    [TestCase(EclipseMode.Both, true, true)]
    [TestCase(EclipseMode.Both, false, false)]
    public void RendersNatural_MatchesModeAndOrigin(EclipseMode mode, bool conditionIsNatural, bool expected)
    {
        Assert.That(EclipseModeRules.RendersNatural(mode, conditionIsNatural), Is.EqualTo(expected));
    }

    [Test]
    public void Both_IsTheShippedDefault_AndMeansEverythingOn()
    {
        // Guards the intended default: geometric eclipses fire AND the storyteller's random ones stay.
        Assert.That(EclipseModeRules.NaturalTriggerActive(EclipseMode.Both), Is.True);
        Assert.That(EclipseModeRules.SuppressRandomEclipse(EclipseMode.Both), Is.False);
        // The runtime flag holder ships defaulted to Both.
        Assert.That(EclipseSettings.Mode, Is.EqualTo(EclipseMode.Both));
    }
}
