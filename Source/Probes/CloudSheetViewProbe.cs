using UnityEngine;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// How much §25 cloud is inside the CAMERA'S view right now (DESIGN.md §25, §25c).
//
// THIS EXISTS BECAUSE A CLOUDLESS FRAME IS INDISTINGUISHABLE FROM A WORKING ONE. Three live runs of
// §25c's fill-cost scenario produced complete, healthy, entirely plausible profiler tables and a full
// set of A/B captures — of a patch of sky with no cloud over it. Nothing failed. The frame times were
// real numbers about nothing, and the captures differed only by however far the camera had drifted.
// A scenario that photographs or profiles the cloud lane must pin `cloud_sheets_in_view` at 1 or more
// beside whatever it is really asking, so that "there was nothing to see" fails loudly instead of
// being reported as "the effect is subtle".
//
// NOT the same question CloudSheetLayout.OnScreen answers, which is despite its name "is this sheet
// on the MAP" — the test that decides whether to issue a draw call at all. A sheet can pass that and
// sit entirely outside a zoomed-in camera, which is exactly the case that wasted those runs.
public sealed class CloudSheetViewProbe : IProbe
{
    public enum Metric
    {
        // Sheets placed over the whole map, drawn or not. The denominator: 0 here means the cloud
        // lane is off or the sky is clear, which is a different problem from a badly aimed camera.
        Placed,

        // Sheets whose bounds intersect the camera's view rect. The one to pin beside a capture.
        InView,

        // The fraction of the camera's view covered by at least one sheet's bounding square, in
        // [0, 1]. Coarse on purpose — a sheet's alpha fades to nothing well inside its own bounds, so
        // this OVERSTATES real coverage and is a sanity check ("is there cloud over the lens"), not a
        // measurement of how much cloud a pixel sees.
        ViewCoverage,
    }

    private readonly Metric metric;

    public CloudSheetViewProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public string Name { get; }

    public float Read(Map map)
    {
        int count = CloudSheetDraw.PlaceSheets(map, out CloudSheetLayout.Placement[] placements);

        if (metric == Metric.Placed)
            return count;

        CameraDriver camera = Find.CameraDriver;
        if (camera == null)
            return 0f;

        CellRect view = camera.CurrentViewRect;
        if (metric == Metric.InView)
            return CountInView(placements, count, view);

        return Coverage(placements, count, view);
    }

    private static int CountInView(
        CloudSheetLayout.Placement[] placements, int count, CellRect view)
    {
        int inView = 0;

        for (int i = 0; i < count; i++)
        {
            if (Intersects(placements[i], view))
                inView++;
        }

        return inView;
    }

    // Sampled on a coarse grid rather than computed as a union of rectangles: the sheets overlap, and
    // an exact union area is a lot of geometry for a number whose only job is to say "yes there is
    // cloud over the camera". 32x32 samples resolves anything big enough to photograph.
    private static float Coverage(
        CloudSheetLayout.Placement[] placements, int count, CellRect view)
    {
        const int Samples = 32;

        if (count <= 0 || view.Width <= 0 || view.Height <= 0)
            return 0f;

        int covered = 0;

        for (int sy = 0; sy < Samples; sy++)
        {
            float z = view.minZ + (sy + 0.5f) * view.Height / Samples;

            for (int sx = 0; sx < Samples; sx++)
            {
                float x = view.minX + (sx + 0.5f) * view.Width / Samples;

                if (AnyCovers(placements, count, x, z))
                    covered++;
            }
        }

        return covered / (float)(Samples * Samples);
    }

    private static bool AnyCovers(
        CloudSheetLayout.Placement[] placements, int count, float x, float z)
    {
        for (int i = 0; i < count; i++)
        {
            float half = placements[i].Size * 0.5f;

            if (Mathf.Abs(x - placements[i].CenterX) < half
                && Mathf.Abs(z - placements[i].CenterZ) < half)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Intersects(in CloudSheetLayout.Placement placement, CellRect view)
    {
        float half = placement.Size * 0.5f;

        return placement.CenterX + half > view.minX
            && placement.CenterX - half < view.maxX + 1
            && placement.CenterZ + half > view.minZ
            && placement.CenterZ - half < view.maxZ + 1;
    }
}
