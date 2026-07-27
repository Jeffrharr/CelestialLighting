using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin adapter for GeometryMemo's key: reads the four things that decide whether a cached solar or
// lunar answer is still the answer, off live Unity/Verse state. Split from GeometryMemo.cs so the
// memo itself stays Verse-free and offline-testable (same split as SunClockMath/SunClockAdapter).
//
// One shared stamp serves both memos rather than a tailored one each. The sun does not care about
// MoonPosition.WarpMoonClock, so including that bit costs the sun one extra miss on the single frame
// a dev flips it — in exchange, there is exactly one place to look for "what invalidates cached
// geometry", instead of two lists that can silently drift apart.
public static class FrameStamp
{
    // Bit layout of GeometryStamp.Variant. Explicit constants rather than an ad-hoc shift chain so
    // adding a fifth mode input later is a one-line change with no chance of overlapping an existing
    // field.
    private const int WarpEnabledBit = 1 << 0;
    private const int WarpMoonClockBit = 1 << 1;
    private const int SunClockModeShift = 2;

    // Time.frameCount, not Time.renderedFrameCount: frameCount advances once per Update, which is the
    // pass every one of our callers runs in (Game.UpdatePlay -> MapUpdate -> SkyManagerUpdate, the
    // WeatherWorker.CurSkyTarget postfixes underneath it, and the harness's own Root_Play.Update
    // postfix).
    public static GeometryStamp Current() =>
        new GeometryStamp(Time.frameCount, Find.TickManager.TicksAbs, Variant(), 0f);

    private static int Variant()
    {
        // Both flags are dev-only escape hatches the live test harness writes mid-run, and the
        // SunClock mode is a real user setting the settings screen writes. None of the three is
        // derivable from (map, tick), so all three belong in the key — see GeometryStamp.Variant for
        // why we key on the values instead of trusting the harness to leave a frame between the write
        // and the read.
        int variant = SunClockAdapter.WarpEnabled ? WarpEnabledBit : 0;
        variant |= MoonPosition.WarpMoonClock ? WarpMoonClockBit : 0;
        variant |= (int)CelestialLightingFeatures.SunClock << SunClockModeShift;
        return variant;
    }
}
