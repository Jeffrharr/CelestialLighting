namespace CelestialLighting;

// Which of §7b's two under-roof sky sources answers a given cell. Pure — no UnityEngine or Verse
// anywhere — so the rule can be unit-tested without booting the game; SkyFalloffSource is the thin
// adapter that feeds it live values.
//
// The rule is small but the ordering carries the whole design (see SkyFalloffSource's header):
// deferral rather than composition, at WHOLE-MAP scope rather than per cell.
public static class SkyFalloffArbitration
{
    // fromOtherMod: IndoorGlowPassthrough's per-cell answer, 0 when nothing external lit this cell.
    // ownerPresent: does a mod own under-roof falloff outright (UnderRoofFalloffOwner.Present)?
    // nativeEnabled / nativeFraction: §7c's feature flag and its own gradient at this cell.
    public static float Resolve(
        float fromOtherMod, bool ownerPresent, bool nativeEnabled, float nativeFraction)
    {
        // Gameplay-authoritative wherever it answers.
        if (fromOtherMod > 0f)
            return fromOtherMod;

        // A zero from a mod that OWNS the gradient is a real zero — "my falloff reaches no further
        // than here" — not an invitation for ours to fill in. Filling in is what puts a seam inside a
        // single room, where their maxDepth ends and ours carries on.
        if (ownerPresent)
            return 0f;

        return nativeEnabled ? nativeFraction : 0f;
    }
}
