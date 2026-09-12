using System;
using System.Collections.Generic;

namespace CelestialLighting;

// The pawn-shadow pass's illuminance-sharing arithmetic, pulled out of VectorLightPawnShadows.cs so
// it can be linked into the offline Tests project (no UnityEngine, no Verse) the way every other
// *Math.cs file in this repo is. VectorLightPawnShadows.Build/Gather are now thin adapters that
// resolve a Pawn/ShadowData/VectorLightField.LightEntry down to the primitive structs below and call
// straight through to Gather/BuildFrom here, in the same two-call shape the pre-split code had.
//
// WHY THIS SPLIT EXISTS NOW RATHER THAN WHEN THE FILE WAS FIRST WRITTEN: the batch-and-parallelise
// task's VectorLightShadowParallelBuild flag fans BuildAll's Build calls out across Parallel.For, and
// the brief's offline concurrency oracle test has to drive a REAL Parallel.For against the exact
// function that fan-out calls -- a hand-rolled loop over synthetic "workers" would only prove that a
// loop runs, not that this file's [ThreadStatic] scratch-per-thread pattern actually isolates two
// pawns' Builds the way VectorLightField.Scratch already had to for its own threaded bake (see
// PawnShadowMathTests.ConcurrentBuildsMatchSerialOnes, mirroring
// VectorLightCoverageBoundsTests.ConcurrentBakesMatchSerialOnes). That test cannot compile a file
// with `Mesh`/`Graphics`/`Pawn`/`Vector3` in it, so the arithmetic had to move here first. The test
// drives the combined `Build` entry point below; the live adapter drives `Gather` and `BuildFrom`
// separately instead, for a reason that entry point's own header explains.
public static class PawnShadowMath
{
    // A primitive projection of VectorLightField.LightEntry -- every field Gather/Build/BoundaryFor/
    // OtherIlluminanceAt actually read, with IntVec3 Cell reduced to its two ints. Built once per
    // Draw, serially, before the fan-out -- see VectorLightPawnShadows.RefreshLampsData.
    public struct LightEntryData
    {
        public int CellX;
        public int CellZ;
        public float Radius;
        public int CoverageRadius;
        public byte[] Coverage;
        public VectorLightMath.LightPolygon Polygon;
    }

    // Everything Build needs about one pawn, resolved from live game state before any fan-out --
    // the primitive half of VectorLightPawnShadows.PawnShadowInput, which still carries the
    // UnityEngine/Verse-flavoured fields (Vector3, ShadowData) this is boiled down from. Anchor and
    // centre travel separately because Build samples the ground from the anchor (the footprint, at
    // the feet) but resolves the shadow's on-screen angle from the centre (the draw position) -- see
    // VectorLightPawnShadows.AnchorOf's header.
    public struct PawnShadowInputData
    {
        public float CentreX;
        public float CentreZ;
        public float AnchorX;
        public float AnchorZ;
        public int PositionX;
        public int PositionZ;
        public float HalfX;
        public float HalfZ;
        public float CasterHeightShaped;
        public bool Roofed;
    }

    // One lamp's contribution to the pawn currently being built, carried from Gather to Build --
    // same shape as VectorLightPawnShadows' old private Contribution struct, just naming the
    // primitive LightEntryData instead of the live VectorLightField.LightEntry.
    public struct ContributionData
    {
        public LightEntryData Entry;
        public float Illuminance;
        public float Distance;
        public float LightX;
        public float LightZ;
        public float UnitX;
        public float UnitZ;
        public float Bearing;
    }

    // Every shadow one pawn casts, geometry and opacity both -- unchanged in shape from
    // VectorLightPawnShadows.DrawnShadow, just declared where the offline test can reach it. That
    // file aliases the name back (`using DrawnShadow = CelestialLighting.PawnShadowMath.DrawnShadow;`)
    // so every existing call site keeps compiling unchanged.
    public struct DrawnShadow
    {
        public float Taper;
        public float Opacity;
        public float Length;
        public float Half;
        public float TrailingEdge;
        public float AngleDegrees;
        public float UnitX;
        public float UnitZ;
    }

    // One call doing both passes back to back -- what the offline concurrency oracle test drives
    // under Parallel.For, because it is the exact unit of work VectorLightPawnShadows.BuildAll fans
    // out across the pool. The live adapter does NOT call this: VectorLightPawnShadows.Build calls
    // Gather and BuildFrom separately, because the Circinus arms circ_vlshadowgather and
    // circ_vlshadowbuild instrument those two calls independently, and collapsing them into one
    // call here would make both arms attribute to the same call site.
    public static void Build(
        PawnShadowInputData input, List<LightEntryData> lights, float skyGlow, bool shaped,
        bool sharesOn, bool groundSharesOn, bool clipOn, List<ContributionData> scratch,
        List<DrawnShadow> into)
    {
        float totalForShare = Gather(input, lights, sharesOn, scratch);
        BuildFrom(input, skyGlow, shaped, sharesOn, groundSharesOn, clipOn, totalForShare, scratch, into);
    }

    // Every shadow this pawn casts, geometry and opacity both, from an ALREADY-GATHERED
    // `scratch` and its already-summed `totalForShare` -- the second of Build's two passes, split out
    // so the live adapter's own Build can call Gather and this separately and keep them
    // Circinus-instrumented as two call sites, the way they always were. See
    // VectorLightPawnShadows.Build's header for the probe convention this preserves.
    //
    // `scratch` is the caller's [ThreadStatic] Contributions list: this function neither owns nor
    // allocates it, so two concurrent callers on two threads are safe exactly as long as each hands
    // in a list nobody else is touching, which is what makes the fan-out's safety a fact about the
    // CALLER rather than about this function.
    public static void BuildFrom(
        PawnShadowInputData input, float skyGlow, bool shaped, bool sharesOn, bool groundSharesOn,
        bool clipOn, float totalForShare, List<ContributionData> scratch, List<DrawnShadow> into)
    {
        into.Clear();

        float casterHeight = !shaped ? VectorLightMath.LegacyPawnHeight : input.CasterHeightShaped;

        float lampHeight = shaped
            ? VectorLightMath.DefaultLampHeight : VectorLightMath.LegacyLampHeight;

        for (int i = 0; i < scratch.Count; i++)
        {
            ContributionData light = scratch[i];

            // THE BRIGHTEST this shadow could possibly come out, asked before any geometry is
            // built. The ground question further down can only ever ADD to the denominator, so a
            // lamp already under the visibility threshold with nothing else lighting its shadow is
            // under it for good, and everything below is wasted on it.
            float brightest = VectorLightMath.PawnShadowOpacity(
                light.Illuminance, light.Illuminance, skyGlow, input.Roofed);

            if (brightest * 255f < 1f)
                continue;

            float length = VectorLightMath.PawnShadowLength(
                light.Distance, casterHeight, lampHeight,
                shaped ? VectorLightMath.MaxPawnShadowLength : VectorLightMath.LegacyMaxShadowLength);

            float half = System.Math.Max(
                VectorLightMath.FootprintExtent(input.HalfX, input.HalfZ, -light.UnitZ, light.UnitX),
                VectorLightMath.MinPawnShadowHalfWidth);

            float trailingEdge = VectorLightMath.FootprintExtent(
                input.HalfX, input.HalfZ, light.UnitX, light.UnitZ);

            // Stopped at the first thing that blocks the lamp (issue #166) -- see
            // VectorLightMath.ClipShadowLength.
            if (clipOn)
                length = VectorLightMath.ClipShadowLength(
                    length, BoundaryFor(light), light.Distance, trailingEdge);

            // A shadow with no room left to fall into is not drawn at all.
            if (length <= 0f)
                continue;

            // NOW the denominator, because it needs the shadow's own geometry -- see ShareFor.
            float opacity = VectorLightMath.PawnShadowOpacityOf(
                ShareFor(
                    i, light, totalForShare, input.AnchorX, input.AnchorZ, trailingEdge, length,
                    sharesOn, groundSharesOn, scratch),
                skyGlow, input.Roofed);

            // Below a level of 255 the shadow is a rounding artefact rather than a shadow.
            if (opacity * 255f < 1f)
                continue;

            into.Add(new DrawnShadow
            {
                Taper = shaped ? 1f : VectorLightMath.LegacyTipTaper,
                Opacity = opacity,
                Length = length,
                Half = half,
                TrailingEdge = trailingEdge,
                UnitX = light.UnitX,
                UnitZ = light.UnitZ,
                AngleDegrees = VectorLightMath.PawnShadowAngleDegrees(
                    light.LightX, light.LightZ, input.CentreX, input.CentreZ),
            });
        }
    }

    // Which lamps light this pawn and how much each contributes, left in `scratch`, with the
    // denominator their shares are taken against returned. See
    // VectorLightPawnShadows.Gather's own header for the probe convention and the two-pass reason.
    public static float Gather(
        PawnShadowInputData input, List<LightEntryData> lights, bool sharesOn,
        List<ContributionData> scratch)
    {
        scratch.Clear();

        float totalIlluminance = 0f;

        for (int lamp = 0; lamp < lights.Count; lamp++)
        {
            LightEntryData entry = lights[lamp];
            float lightX = entry.CellX + 0.5f;
            float lightZ = entry.CellZ + 0.5f;
            float dx = input.CentreX - lightX;
            float dz = input.CentreZ - lightZ;

            // SQUARED AGAINST SQUARED -- see VectorLightPawnShadows.Gather's own comment on why.
            float distanceSquared = dx * dx + dz * dz;

            if (distanceSquared > entry.Radius * entry.Radius)
                continue;

            float distance = MathF.Sqrt(distanceSquared);

            float coverage = VectorLightMath.CoverageAt(
                entry.Coverage, entry.CellX, entry.CellZ, entry.CoverageRadius,
                input.PositionX, input.PositionZ) / 255f;

            float illuminance = VectorLightMath.PawnIlluminance(distance, entry.Radius, coverage);

            if (illuminance <= 0f)
                continue;

            totalIlluminance += illuminance;

            scratch.Add(new ContributionData
            {
                Entry = entry,
                Bearing = MathF.Atan2(dz, dx),
                Illuminance = illuminance,
                Distance = distance,
                LightX = lightX,
                LightZ = lightZ,
                UnitX = distance > 0f ? dx / distance : 1f,
                UnitZ = distance > 0f ? dz / distance : 0f,
            });
        }

        // With the share model switched off the denominator stays at one -- see
        // VectorLightPawnShadows.Gather's own comment: this is the true pre-feature baseline, not a
        // second code path.
        return sharesOn ? totalIlluminance : VectorLightMath.FullIlluminance;
    }

    // This lamp's share of the light being blocked -- see VectorLightPawnShadows.ShareFor's own
    // header for why the arms are ordered the way they are.
    private static float ShareFor(
        int index, ContributionData light, float totalAtPawn, float anchorX, float anchorZ,
        float trailingEdge, float length, bool sharesOn, bool groundSharesOn,
        List<ContributionData> scratch)
    {
        bool grounded = sharesOn && groundSharesOn && scratch.Count > 1;

        if (!grounded)
            return VectorLightMath.PawnShadowShare(light.Illuminance, totalAtPawn);

        float sample = VectorLightMath.ShadowSampleDistance(trailingEdge, length);

        float other = OtherIlluminanceAt(
            scratch, index, anchorX + light.UnitX * sample, anchorZ + light.UnitZ * sample);

        return VectorLightMath.PawnShadowGroundShare(light.Illuminance, other);
    }

    // What every lamp OTHER than this one is putting on one point of ground -- see
    // VectorLightPawnShadows.OtherIlluminanceAt's own header for the O(N^2) and coverage notes.
    private static float OtherIlluminanceAt(
        List<ContributionData> scratch, int excluded, float x, float z)
    {
        int cellX = (int)System.Math.Floor(x);
        int cellZ = (int)System.Math.Floor(z);
        float total = 0f;

        for (int i = 0; i < scratch.Count; i++)
        {
            if (i != excluded)
            {
                ContributionData other = scratch[i];
                float dx = x - other.LightX;
                float dz = z - other.LightZ;
                float distance = MathF.Sqrt(dx * dx + dz * dz);

                float coverage = VectorLightMath.CoverageAt(
                    other.Entry.Coverage, other.Entry.CellX, other.Entry.CellZ,
                    other.Entry.CoverageRadius, cellX, cellZ) / 255f;

                total += VectorLightMath.PawnIlluminance(distance, other.Entry.Radius, coverage);
            }
        }

        return total;
    }

    // How far this lamp's light reaches along the shadow's own bearing -- see
    // VectorLightPawnShadows.BoundaryFor's own header for the unbuilt-polygon fallback reasoning.
    private static float BoundaryFor(ContributionData light)
    {
        if (light.Entry.Polygon.Count == 0)
            return light.Entry.Radius;

        return System.Math.Min(
            VectorLightMath.BoundaryDistanceAt(light.Entry.Polygon, light.Bearing),
            light.Entry.Radius);
    }
}
