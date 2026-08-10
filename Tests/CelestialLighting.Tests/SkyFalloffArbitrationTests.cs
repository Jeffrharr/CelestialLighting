namespace CelestialLighting.Tests;

// Offline tests for which under-roof sky source answers a cell (Source/SkyFalloffArbitration.cs).
// The live halves — IndoorGlowPassthrough's glow-grid read, UnderRoofFalloffOwner's mod-list scan and
// §7c's BFS — all need Verse; what is testable here is the ordering, which is where the design lives.
[TestFixture]
public class SkyFalloffArbitrationTests
{
    private const float Tolerance = 1e-5f;

    [Test]
    public void UnmoddedInstall_FallsThroughToTheNativeGradient()
    {
        // Nothing external, nobody owning: §7c answers, exactly as it did before the passthrough
        // existed. This is the path almost every player is on.
        Assert.That(
            SkyFalloffArbitration.Resolve(
                fromOtherMod: 0f, ownerPresent: false, nativeEnabled: true, nativeFraction: 0.2625f),
            Is.EqualTo(0.2625f).Within(Tolerance));
    }

    [Test]
    public void AnotherModsValue_WinsOutright()
    {
        // Gameplay-authoritative: Ambient Light's own mouseover readout reports exactly this number,
        // so the render must agree with it rather than average it with ours.
        Assert.That(
            SkyFalloffArbitration.Resolve(
                fromOtherMod: 0.4583f, ownerPresent: true, nativeEnabled: true, nativeFraction: 0.2625f),
            Is.EqualTo(0.4583f).Within(Tolerance));
    }

    [Test]
    public void OwnerPresent_ZeroMeansZero_RatherThanFallingBackToOurs()
    {
        // THE seam test, and the reason this is whole-map rather than per-cell. A cell just past the
        // owning mod's maxDepth reports 0. Answering it with our own gradient — which has an
        // independently tuned maxDepth — puts a visible discontinuity INSIDE a single room, where
        // their reach ends and ours carries on. Neither gradient has that edge on its own.
        Assert.That(
            SkyFalloffArbitration.Resolve(
                fromOtherMod: 0f, ownerPresent: true, nativeEnabled: true, nativeFraction: 0.2625f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void NativeDisabled_WithNoOwner_IsZero()
    {
        // §7c's own feature flag off and nothing external: the pre-§7c baseline, where CapOcclusion's
        // only floor was minIndoorBrightness.
        Assert.That(
            SkyFalloffArbitration.Resolve(
                fromOtherMod: 0f, ownerPresent: false, nativeEnabled: false, nativeFraction: 0.2625f),
            Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void NativeDisabled_StillLetsAnotherModThrough()
    {
        // §7c's flag governs OUR gradient, not somebody else's light. A player who turned our falloff
        // off has not asked us to start hiding Ambient Light's — or ReBuild's glass walls.
        Assert.That(
            SkyFalloffArbitration.Resolve(
                fromOtherMod: 0.4583f, ownerPresent: false, nativeEnabled: false, nativeFraction: 0f),
            Is.EqualTo(0.4583f).Within(Tolerance));
    }

    [Test]
    public void NonOwningMod_LeavesTheNativeGradientRunning()
    {
        // ReBuild: Doors and Corners lights cells near its GLASS WALLS and supplies no door gradient at
        // all, so it is not an owner. Treating it as one — e.g. by testing "has anyone patched
        // GroundGlowAt" instead of naming owners — would silently delete under-roof falloff for every
        // player who has ReBuild. That is a regression, not a compat fix; see UnderRoofFalloffOwner.
        Assert.That(
            SkyFalloffArbitration.Resolve(
                fromOtherMod: 0f, ownerPresent: false, nativeEnabled: true, nativeFraction: 0.2625f),
            Is.EqualTo(0.2625f).Within(Tolerance));
    }
}
