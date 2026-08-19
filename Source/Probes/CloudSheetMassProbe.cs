using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// HOW MUCH CLOUD IS ACTUALLY PLACED over this map right now: the sum of every placed sheet's alpha,
// so twelve full sheets read 12 and four sheets at half strength read 2.
//
// WHY A SUM RATHER THAN A COUNT. The count is the thing that used to pop — crossing a twelfth of
// cover deleted a whole cloud from mid-sky in one tick — and a count probe would report exactly the
// same step function whether or not the fade that fixed it exists, because the marginal sheet is
// still counted while it fades. The sum is the quantity a viewer sees: it moves continuously through
// a threshold when the fade is working and jumps by a whole sheet when it is not. Scenarios read it
// on consecutive ticks and compare, which is the only way to catch a discontinuity in a probe rather
// than in a screenshot.
//
// Read through CloudSheetDraw.PlaceSheets, not by re-deriving the layout, per §18's rule that a probe
// reads the value its patch reads: the placements this returns are the ones the three cloud lanes
// draw this frame, coverage fade and all. Seeding that per-frame cache from here is harmless — it is
// keyed on tick and map, so the renderer either finds the same answer already computed or recomputes
// an identical one.
public sealed class CloudSheetMassProbe : IProbe
{
    public string Name => "cloud_sheet_mass";

    public float Read(Map map)
    {
        int count = CloudSheetDraw.PlaceSheets(map, out CloudSheetLayout.Placement[] placements);

        float mass = 0f;
        for (int i = 0; i < count; i++)
            mass += placements[i].Alpha;

        return mass;
    }
}
