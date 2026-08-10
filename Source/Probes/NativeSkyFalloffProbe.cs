using System;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// Issue #124 / §7c: the near-door cell in Tests/Scenarios/native_sky_falloff.json's 11x11 roofed room
// — same layout AmbientLightDoorCellProbe already established for issue #80, reused so the two
// scenarios are directly comparable. Depth reads the raw BFS layer NativeSkyFalloffGrid computes;
// Fraction reads what SkyFalloffSource actually hands CapOcclusion for the same cell — pairing them
// tells "the BFS never reached this cell" (Depth 0, e.g. Ambient Light active and deferred to, or the
// feature off) apart from "it reached the cell but the formula zeroed it" (Depth > 0, Fraction 0, e.g.
// a genuinely dark night).
public sealed class NativeSkyFalloffProbe : IProbe
{
    public enum Metric
    {
        // NativeSkyFalloffGrid.DepthAt(map, cell, maxDepth) — the raw BFS layer, independent of
        // curSkyGlow or the feature flag.
        Depth,

        // SkyFalloffSource.FractionAt(map, cell) — the dispatched value §7b's CapOcclusion actually
        // consumes, whichever source (if any) currently answers for this cell.
        Fraction,
    }

    // One cell north of the doorway gap: local (0, -4) off the room's offset (0, 45), i.e. map centre
    // + (0, 41) — identical placement to AmbientLightDoorCellProbe's NearDoorLocalOffset, matching
    // Tests/Scenarios/native_sky_falloff.json's room exactly.
    private static readonly IntVec3 DefaultRoomOffset = new IntVec3(0, 0, 45);
    private static readonly IntVec3 NearDoorLocalOffset = new IntVec3(0, 0, -4);

    private readonly Metric metric;
    private readonly IntVec3 roomOffset;

    public string Name { get; }

    public NativeSkyFalloffProbe(string name, Metric metric) : this(name, metric, DefaultRoomOffset)
    {
    }

    // §7d door_strength_leak.json: two copies of the same 11x11 room side by side, differing only in
    // which door def sits in the south wall, so this needs a second room offset rather than the one
    // native_sky_falloff.json's own room hardcodes above.
    public NativeSkyFalloffProbe(string name, Metric metric, IntVec3 roomOffset)
    {
        Name = name;
        this.metric = metric;
        this.roomOffset = roomOffset;
    }

    public float Read(Map map)
    {
        IntVec3 cell = map.Center + roomOffset + NearDoorLocalOffset;

        switch (metric)
        {
            case Metric.Depth:
                return NativeSkyFalloffGrid.DepthAt(map, cell, NativeSkyFalloffMath.DefaultMaxDepth, DoorLeakMath.DefaultSensitivity);
            case Metric.Fraction:
                return SkyFalloffSource.FractionAt(map, cell);
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }
}
