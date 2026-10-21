namespace CelestialLighting;

// What zooming the camera in does to the drawn cloud sheets: the pure core behind the "Clouds when
// zoomed in" setting. Primitives in, primitives out — the adapter in CloudLayers reads the camera.
//
// WHY THIS EXISTS. A cloud sheet is drawn above FogOfWar over the whole map, which is right from a
// distance: it reads as weather passing over the colony. Zoomed in on a handful of pawns the same
// sheet fills the screen as a flat translucent wash with no edge in view, and a wash with no edge
// reads as ground fog rather than as cloud overhead. Neither way of fixing that is obviously the
// better one, so both ship and the player picks:
//
//   Fade        opacity falls smoothly over a long stretch of the zoom range, so the closer you get
//               the less cloud is between you and the colony. No threshold to notice, at the price
//               of a faint haze in the middle of the range.
//
//   FlyThrough  full opacity until close in, then a short window in which the sheets fade AND grow
//               outward from the screen centre, so the deck appears to rush past the camera — the
//               feeling of descending through a cloud layer rather than of it switching off. The
//               window has width rather than being a cut because RimWorld eases its zoom over
//               several frames, and a cut would pop as the eased size crossed the line.
//
// ONLY THE DRAWN SHEETS. Cloud shadows (§23c) and underlight (§23b) are light reaching the ground,
// and the camera zooming in does not move the cloud making them — so they are untouched, which is
// also what stops a zoomed-in colony reading as if the weather had cleared.
//
// IN ZOOM FRACTION, NOT ROOT SIZE. Vanilla's zoom range is not one constant: CameraMapConfig's
// minimum is 11 on a PC and 7.2 on a Steam Deck. Thresholds expressed as a fraction of the range
// land at the same point on the scroll wheel on both, where raw sizes would fade at a different
// framing on each.
public static class CloudZoomMath
{
    // Scribed by name, so renaming a member silently resets a player's choice to the default —
    // keep these names stable.
    public enum Mode
    {
        Off = 0,
        Fade = 1,
        FlyThrough = 2,
    }

    // Fade: full opacity from here outward. 0.40 is a root size of ~31 on a PC — roughly where a
    // whole base still fits on screen, i.e. the last framing at which a sheet's edges are visible
    // and it still reads as a cloud.
    public const float FadeStart = 0.40f;

    // Fade: zero opacity at the closest zoom. Fully gone rather than a floor, because any floor is
    // exactly the fog-like wash this exists to remove.
    public const float FadeEnd = 0f;

    // FlyThrough: full opacity down to here (root size ~18 on a PC, inside vanilla's "Close" band
    // on the Deck)...
    public const float FlyThroughStart = 0.15f;

    // ...and gone by here (~14.5). The 0.08 window is a few notches of the scroll wheel: long enough
    // that the eased zoom crosses it over several frames, short enough to read as passing through.
    public const float FlyThroughEnd = 0.07f;

    // FlyThrough: how much bigger a sheet is drawn at the moment it vanishes. 1.5 extra means a sheet
    // has grown to 2.5x as it fades out, which carries most on-screen cloud past the screen edges.
    public const float FlyThroughGrowth = 1.5f;

    // Where the camera sits in its zoom range: 0 at the closest it will go, 1 at the furthest.
    // A degenerate or corrupt range answers 1 (fully zoomed out), the value at which every mode is
    // a no-op — a bad camera config should give the shipped sky, not an invisible one.
    public static float ZoomFraction(float rootSize, float sizeMin, float sizeMax)
    {
        float span = sizeMax - sizeMin;
        if (!(span > 0f) || float.IsNaN(rootSize))
            return 1f;

        return Clamp01((rootSize - sizeMin) / span);
    }

    // The multiplier on sheet opacity at this zoom fraction.
    public static float OpacityAt(Mode mode, float zoom)
    {
        switch (mode)
        {
            case Mode.Fade:
                return SmoothStep(FadeEnd, FadeStart, zoom);
            case Mode.FlyThrough:
                return SmoothStep(FlyThroughEnd, FlyThroughStart, zoom);
            default:
                return 1f;
        }
    }

    // The multiplier on sheet size at this zoom fraction. Only FlyThrough grows, and it grows in
    // lockstep with its own fade — tied to the opacity rather than to the zoom separately, so the
    // two can never disagree about where the window is.
    public static float ScaleAt(Mode mode, float zoom)
    {
        if (mode != Mode.FlyThrough)
            return 1f;

        return 1f + FlyThroughGrowth * (1f - OpacityAt(mode, zoom));
    }

    // One coordinate of a sheet's centre after scaling the deck about the camera. Scaling each sheet
    // about its OWN centre would grow them in place and pile them on top of each other; scaling about
    // the camera spreads them outward from the middle of the screen, which is what flying through a
    // layer looks like.
    public static float ScaleAbout(float centre, float pivot, float scale) =>
        pivot + (centre - pivot) * scale;

    // Hermite smoothstep from `edge0` (answers 0) to `edge1` (answers 1). Ease at both ends so
    // neither end of the window has a visible kink as the eased zoom crosses it.
    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (float.IsNaN(x))
            return 1f;

        float t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
