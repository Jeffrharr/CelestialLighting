using System;
using System.Collections.Generic;
using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for where §11a's aurora sheets stand over a map.
//
// The property that matters most here cannot be seen in a screenshot of a small map and is glaring on
// a large one, hours in: that a bounded sheet shows EXACTLY ONE vertical repeat of the field. The
// field's v axis is altitude up the curtain, so any other v scale stamps the same three arcs up the
// map. AuroraSheetLayout makes that arithmetically impossible rather than tuned-away, and these tests
// are what stop someone reintroducing it.
[TestFixture]
public class AuroraSheetLayoutTests
{
    // Every stock RimWorld map size, plus the two test sizes, plus a non-square quest map and a pocket
    // map. Non-square matters because Map.Size is an IntVec3 and only the world-generation UI forces
    // square maps.
    private static readonly int[][] MapSizes =
    {
        new[] { 200, 200 }, new[] { 225, 225 }, new[] { 250, 250 }, new[] { 275, 275 },
        new[] { 300, 300 }, new[] { 325, 325 }, new[] { 350, 350 }, new[] { 400, 400 },
        new[] { 250, 150 }, new[] { 75, 75 },
    };

    [Test]
    public void BoundedSheets_AlwaysShowExactlyOneVerticalRepeat()
    {
        // The invariant the whole file exists for. Not "about one" — exactly one.
        ForEveryMap((x, z) =>
        {
            AuroraFieldSpec spec = AuroraFieldRegistry.HemRays;

            for (int i = 0; i < AuroraSheetLayout.PlacementCount(spec, z); i++)
                Assert.That(
                    AuroraSheetLayout.Placement(spec, i, x, z).VScale, Is.EqualTo(1f),
                    $"sheet {i} on {x}x{z} would tile vertically");
        });
    }

    [Test]
    public void SheetCount_StaysWithinTheMaterialsAllocatedAtStartup()
    {
        // Materials are built once in a static constructor because `new Material` must be on the main
        // thread, so a layout asking for more sheets than that would index past the array.
        ForEveryMap((x, z) =>
        {
            int count = AuroraSheetLayout.PlacementCount(AuroraFieldRegistry.HemRays, z);

            Assert.That(count, Is.GreaterThanOrEqualTo(1), $"{x}x{z} draws no aurora at all");
            Assert.That(count, Is.LessThanOrEqualTo(AuroraSheetLayout.MaxSheets), $"{x}x{z}");
        });
    }

    [Test]
    public void EverySheet_IsOnTheMapAndNotDegenerate()
    {
        ForEveryMap((x, z) =>
        {
            AuroraFieldSpec spec = AuroraFieldRegistry.HemRays;

            for (int i = 0; i < AuroraSheetLayout.PlacementCount(spec, z); i++)
            {
                AuroraSheetPlacement p = AuroraSheetLayout.Placement(spec, i, x, z);

                Assert.That(p.Width, Is.GreaterThan(0f), $"sheet {i} on {x}x{z} width");
                Assert.That(p.Height, Is.GreaterThan(0f), $"sheet {i} on {x}x{z} height");
                Assert.That(p.Alpha, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f), $"sheet {i} alpha");
                // Both axes bounded now, so the whole patch must sit on the map — otherwise its taper
                // happens off-screen and the edge reads as a cut instead of a fade.
                Assert.That(p.CenterX - p.Width * 0.5f, Is.GreaterThanOrEqualTo(-0.01f), $"sheet {i} off west edge");
                Assert.That(p.CenterX + p.Width * 0.5f, Is.LessThanOrEqualTo(x + 0.01f), $"sheet {i} off east edge");
                Assert.That(p.CenterZ - p.Height * 0.5f, Is.GreaterThanOrEqualTo(-0.01f), $"sheet {i} off south edge");
                Assert.That(p.CenterZ + p.Height * 0.5f, Is.LessThanOrEqualTo(z + 0.01f), $"sheet {i} off north edge");
                Assert.That(Math.Abs(p.UScale), Is.EqualTo(1f).Within(1e-4f), $"sheet {i} would tile in u");
            }
        });
    }

    [Test]
    public void SheetsAreNotEvenlySpaced()
    {
        // Evenly spaced sheets are periodicity wearing a different hat — patches at 0.25/0.50/0.75 read
        // as one pattern repeating, which is the defect this whole design removes.
        //
        // Asserted against the placement TABLES rather than against a particular map, because how many
        // sheets a map yields depends on the sheet height, and this test previously needed three. When
        // the height grew, a 400-cell map started yielding two, the Assume stopped holding, and the test
        // went quietly inconclusive — still green, no longer checking anything. Driving it from the
        // tables instead means it cannot be switched off by a tuning change.
        var gaps = new List<float>();

        for (int i = 1; i < AuroraSheetLayout.MaxSheets; i++)
        {
            AuroraSheetPlacement a = AuroraSheetLayout.Placement(AuroraFieldRegistry.HemRays, i - 1, 400, 400);
            AuroraSheetPlacement b = AuroraSheetLayout.Placement(AuroraFieldRegistry.HemRays, i, 400, 400);
            gaps.Add(b.CenterZ - a.CenterZ);
        }

        for (int i = 1; i < gaps.Count; i++)
            Assert.That(gaps[i], Is.Not.EqualTo(gaps[i - 1]).Within(1f),
                $"sheet spacing {i} repeats the previous one — that is periodicity again");

        // And the same across the map, since patches are bounded in u too.
        for (int i = 1; i < AuroraSheetLayout.MaxSheets; i++)
            Assert.That(
                AuroraSheetLayout.Placement(AuroraFieldRegistry.HemRays, i, 400, 400).CenterX,
                Is.Not.EqualTo(AuroraSheetLayout.Placement(AuroraFieldRegistry.HemRays, i - 1, 400, 400).CenterX)
                    .Within(1f),
                $"sheets {i - 1} and {i} sit in the same column of sky");
    }

    [Test]
    public void NoTwoSheets_ShowTheSameStretchOfTexture()
    {
        // All sheets share one texture, so without distinct u phases they would be literal copies of
        // each other stacked up the sky. The mirroring is a second line of defence on top of that.
        AuroraFieldSpec spec = AuroraFieldRegistry.HemRays;
        const int x = 400;
        const int z = 400;

        var seen = new List<float>();

        for (int i = 0; i < AuroraSheetLayout.PlacementCount(spec, z); i++)
        {
            AuroraSheetPlacement p = AuroraSheetLayout.Placement(spec, i, x, z);

            foreach (float other in seen)
                Assert.That(p.UPhase, Is.Not.EqualTo(other).Within(1e-4f), $"sheet {i} duplicates a phase");

            seen.Add(p.UPhase);
        }
    }

    [Test]
    public void Placement_UsesFloatArithmetic_NotMapCentresIntegerDivision()
    {
        // Map.Center is `Size.x / 2` in INTEGER arithmetic, so it is half a cell off true centre on
        // every even-sized map — and every stock RimWorld map size is even. If someone swaps the float
        // maths here for Map.Center, 250 and 251 would produce the same centre; they must differ by
        // exactly half a cell.
        AuroraFieldSpec spec = AuroraFieldRegistry.HemRays;

        float even = AuroraSheetLayout.Placement(spec, 0, 250, 250).CenterX;
        float odd = AuroraSheetLayout.Placement(spec, 0, 251, 250).CenterX;

        // Placement is a fraction of Size in floats, so a one-cell wider map moves the patch by that
        // fraction of a cell. Integer division would round both to the same place.
        Assert.That(odd - even, Is.GreaterThan(0f).And.LessThan(1f));
        Assert.That(odd, Is.Not.EqualTo(even));
    }

    [Test]
    public void SpanningField_CoversTheMapAndKeepsItsTiling()
    {
        // The contour field's opposite case, kept working: it is an overhead view, so covering the
        // ground is the point and vertical repeats are unobjectionable.
        AuroraFieldSpec spec = AuroraFieldRegistry.Contour;

        Assert.That(AuroraSheetLayout.PlacementCount(spec, 250), Is.EqualTo(spec.Sheets.Length));

        AuroraSheetPlacement p = AuroraSheetLayout.Placement(spec, 0, 250, 250);

        Assert.That(p.Width, Is.EqualTo(250f).Within(1e-4f));
        Assert.That(p.Height, Is.EqualTo(250f).Within(1e-4f));
        Assert.That(p.CenterX, Is.EqualTo(125f).Within(1e-4f));
        Assert.That(p.VScale, Is.GreaterThan(1f), "a whole-map contour sheet should tile in v");
    }

    private static void ForEveryMap(System.Action<int, int> check)
    {
        foreach (int[] size in MapSizes)
            check(size[0], size[1]);
    }
}
