using System;

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

    // WHERE EACH MODE STARTS CLEARING IS THE PLAYER'S, via the "Clouds clear below" slider — one
    // value per mode, because the two want very different framings. These are the defaults.
    //
    // Fade: full opacity from here outward. 0.40 is a root size of ~31 on a PC — roughly where a
    // whole base still fits on screen, i.e. the last framing at which a sheet's edges are visible
    // and it still reads as a cloud. Fade always reaches zero at the closest zoom: fully gone rather
    // than a floor, because any floor is exactly the fog-like wash this exists to remove.
    public const float DefaultFadeStart = 0.40f;

    // FlyThrough: full opacity down to here. 0.24 is a root size of ~20 on the Deck (~23 on a PC):
    // one zoom-key step (x1.3 in root size, CameraDriver's 4 x zoomSpeed / 35) further out than the
    // 0.15 first shipped for play-testing, which cut in a level later than it should.
    public const float DefaultFlyThroughStart = 0.24f;

    // FlyThrough: how far below its start the window ends. 0.11 is about one zoom-key step on the Deck:
    // long enough that the eased zoom crosses it over several frames (a cut would pop), short enough
    // to read as passing through.
    public const float FlyThroughWidth = 0.11f;

    // The slider's range. Not down to 0: a start at the closest zoom is a mode that never acts, which
    // is what "Always drawn" is for. Not up to 1: fully zoomed out must always show cloud.
    public const float MinStart = 0.05f;

    public const float MaxStart = 0.80f;

    public static float DefaultStartFor(Mode mode) =>
        mode == Mode.Fade ? DefaultFadeStart : DefaultFlyThroughStart;

    // A stored start brought back into range. NaN — a corrupt config — answers the mode's default
    // rather than a value that would blank or never clear the clouds.
    public static float ClampStart(Mode mode, float start) =>
        float.IsNaN(start) ? DefaultStartFor(mode)
        : start < MinStart ? MinStart
        : start > MaxStart ? MaxStart
        : start;

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

    // The multiplier on sheet opacity at this zoom fraction, for a mode that starts clearing at
    // `start` (the player's slider; see ClampStart).
    public static float OpacityAt(Mode mode, float zoom, float start)
    {
        float edge = ClampStart(mode, start);
        switch (mode)
        {
            case Mode.Fade:
                return SmoothStep(0f, edge, zoom);
            case Mode.FlyThrough:
                // The end is held at the closest zoom when a low start would push it past, so the
                // window shortens rather than leaving cloud on screen fully zoomed in.
                return SmoothStep(Math.Max(edge - FlyThroughWidth, 0f), edge, zoom);
            default:
                return 1f;
        }
    }

    // The multiplier on sheet size at this zoom fraction. Only FlyThrough grows, and it grows in
    // lockstep with its own fade — tied to the opacity rather than to the zoom separately, so the
    // two can never disagree about where the window is.
    public static float ScaleAt(Mode mode, float zoom, float start)
    {
        if (mode != Mode.FlyThrough)
            return 1f;

        return 1f + FlyThroughGrowth * (1f - OpacityAt(mode, zoom, start));
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
