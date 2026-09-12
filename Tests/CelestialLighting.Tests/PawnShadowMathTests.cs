using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CelestialLighting.Tests;

// The one claim the pawn-shadow batch-and-parallelise task's VectorLightShadowParallelBuild flag
// makes: fanning PawnShadowMath.Build out across Parallel.For, one call per pawn, produces the
// exact same shadows a serial loop would.
//
// WHY THIS IS THE FUNCTION UNDER TEST RATHER THAN A HAND-ROLLED STAND-IN. Build is the exact unit
// of work VectorLightPawnShadows.BuildAll fans out across the pool (see PawnShadowMath.Build's own
// header), so a test driving anything else would prove a loop runs rather than proving THIS
// fan-out is safe. In particular it exercises the same per-call scratch list
// (List<ContributionData>) VectorLightPawnShadows.Contributions is — created fresh per thread via
// ThreadLocal here the same way VectorLightPawnShadows' own [ThreadStatic] backing field is, per
// VectorLightCoverageBoundsTests.ConcurrentBakesMatchSerialOnes' precedent for the coverage bake.
//
// NOT A TRIVIAL A-B==0 SELF-COMPARISON. Both arms call the same Build, which would be exactly the
// hollow differential test the "differential tests need an independent oracle" trap warns about IF
// the property under test were the arithmetic's correctness. It is not: the property under test is
// that two pawns built on two threads never corrupt each other's result, which is a fact about
// SHARED MUTABLE STATE (whether each thread's scratch list is really its own) that only a real
// Parallel.For can expose — a serial loop calling Build twice would pass even with a scratch list
// shared across every call, because nothing would ever be running at the same instant. Running the
// scenes at Repeats=40 gives the scheduler room to actually interleave two pawns' Builds on the
// same thread pool worker before either finishes, which is what would corrupt a shared list.
[TestFixture]
public class PawnShadowMathTests
{
    private struct Scene
    {
        public PawnShadowMath.PawnShadowInputData Input;
        public List<PawnShadowMath.LightEntryData> Lights;
        public float SkyGlow;
        public bool Shaped;
        public bool SharesOn;
        public bool GroundSharesOn;
        public bool ClipOn;
    }

    private static PawnShadowMath.LightEntryData Lamp(int cellX, int cellZ, float radius) =>
        new PawnShadowMath.LightEntryData
        {
            CellX = cellX,
            CellZ = cellZ,
            Radius = radius,
            CoverageRadius = 0,
            Coverage = Array.Empty<byte>(),
            Polygon = default,
        };

    private static PawnShadowMath.PawnShadowInputData Pawn(float x, float z, bool roofed) =>
        new PawnShadowMath.PawnShadowInputData
        {
            CentreX = x,
            CentreZ = z,
            AnchorX = x,
            AnchorZ = z - 0.3f,
            PositionX = (int)Math.Floor(x),
            PositionZ = (int)Math.Floor(z),
            HalfX = 0.2f,
            HalfZ = 0.2f,
            CasterHeightShaped = 0.8f,
            Roofed = roofed,
        };

    // Every arm the flags reach, so a corruption that only shows up with (say) ground shares on and
    // clip off is not left untested. Lamp counts run from one (ShareFor's non-grounded branch) up
    // to four (OtherIlluminanceAt's O(N²) walk over Contributions), same as
    // VectorLightPawnShadows' own comments describe as the shapes each arm answers.
    private static Scene[] Scenes() => new[]
    {
        new Scene
        {
            Input = Pawn(20.5f, 20.5f, roofed: false),
            Lights = new List<PawnShadowMath.LightEntryData> { Lamp(20, 15, 8f) },
            SkyGlow = 0f, Shaped = false, SharesOn = false, GroundSharesOn = false, ClipOn = false,
        },
        new Scene
        {
            Input = Pawn(20.5f, 20.5f, roofed: false),
            Lights = new List<PawnShadowMath.LightEntryData>
            {
                Lamp(20, 15, 8f), Lamp(26, 20, 8f),
            },
            SkyGlow = 0f, Shaped = true, SharesOn = true, GroundSharesOn = false, ClipOn = false,
        },
        new Scene
        {
            Input = Pawn(20.5f, 20.5f, roofed: true),
            Lights = new List<PawnShadowMath.LightEntryData>
            {
                Lamp(20, 15, 8f), Lamp(26, 20, 8f), Lamp(20, 26, 8f),
            },
            SkyGlow = 0f, Shaped = true, SharesOn = true, GroundSharesOn = true, ClipOn = true,
        },
        new Scene
        {
            Input = Pawn(20.2f, 15.4f, roofed: false),
            Lights = new List<PawnShadowMath.LightEntryData>
            {
                Lamp(20, 15, 8f), Lamp(26, 20, 8f),
            },
            SkyGlow = 0f, Shaped = false, SharesOn = true, GroundSharesOn = true, ClipOn = false,
        },
        new Scene
        {
            Input = Pawn(20.5f, 20.5f, roofed: false),
            Lights = new List<PawnShadowMath.LightEntryData>
            {
                Lamp(20, 15, 8f), Lamp(26, 20, 8f), Lamp(20, 26, 8f), Lamp(15, 20, 8f),
            },
            SkyGlow = 0f, Shaped = true, SharesOn = true, GroundSharesOn = true, ClipOn = true,
        },
    };

    [Test]
    public void ConcurrentBuildsMatchSerialOnes()
    {
        Scene[] scenes = Scenes();

        List<PawnShadowMath.DrawnShadow>[] serial = new List<PawnShadowMath.DrawnShadow>[scenes.Length];

        for (int i = 0; i < scenes.Length; i++)
        {
            Scene scene = scenes[i];
            serial[i] = new List<PawnShadowMath.DrawnShadow>();

            PawnShadowMath.Build(
                scene.Input, scene.Lights, scene.SkyGlow, scene.Shaped, scene.SharesOn,
                scene.GroundSharesOn, scene.ClipOn, new List<PawnShadowMath.ContributionData>(),
                serial[i]);
        }

        // At least one scene has to actually cast a shadow, or this test would pass just as happily
        // against a Build that always clears `into` and does nothing — see
        // "differential tests need an independent oracle" for exactly this shape of false green.
        bool anyShadowsAtAll = false;
        for (int i = 0; i < serial.Length; i++)
            anyShadowsAtAll |= serial[i].Count > 0;
        Assert.That(anyShadowsAtAll, Is.True, "fixture produced no shadows to compare");

        // One scratch pair per thread, created on first use — the field's ownership rule
        // VectorLightPawnShadows.Contributions itself relies on, reproduced here so the test
        // exercises the same contract rather than a more forgiving one.
        ThreadLocal<List<PawnShadowMath.ContributionData>> scratch =
            new ThreadLocal<List<PawnShadowMath.ContributionData>>(
                () => new List<PawnShadowMath.ContributionData>());

        const int Repeats = 40;
        List<PawnShadowMath.DrawnShadow>[] threaded =
            new List<PawnShadowMath.DrawnShadow>[scenes.Length * Repeats];

        Parallel.For(0, scenes.Length * Repeats, job =>
        {
            int i = job % scenes.Length;
            Scene scene = scenes[i];
            List<PawnShadowMath.DrawnShadow> into = new List<PawnShadowMath.DrawnShadow>();

            PawnShadowMath.Build(
                scene.Input, scene.Lights, scene.SkyGlow, scene.Shaped, scene.SharesOn,
                scene.GroundSharesOn, scene.ClipOn, scratch.Value!, into);

            threaded[job] = into;
        });

        for (int job = 0; job < threaded.Length; job++)
        {
            List<PawnShadowMath.DrawnShadow> expected = serial[job % scenes.Length];
            List<PawnShadowMath.DrawnShadow> actual = threaded[job];

            Assert.That(actual.Count, Is.EqualTo(expected.Count), $"job {job} shadow count");

            for (int s = 0; s < expected.Count; s++)
            {
                // Field-by-field rather than a struct Equals: DrawnShadow carries no override and a
                // default Equals on a struct with float fields is exact bitwise comparison anyway,
                // but spelling it out means a future field addition fails to compile here rather
                // than silently going uncompared.
                PawnShadowMath.DrawnShadow e = expected[s];
                PawnShadowMath.DrawnShadow a = actual[s];

                Assert.That(a.Taper, Is.EqualTo(e.Taper), $"job {job} shadow {s} Taper");
                Assert.That(a.Opacity, Is.EqualTo(e.Opacity), $"job {job} shadow {s} Opacity");
                Assert.That(a.Length, Is.EqualTo(e.Length), $"job {job} shadow {s} Length");
                Assert.That(a.Half, Is.EqualTo(e.Half), $"job {job} shadow {s} Half");
                Assert.That(a.TrailingEdge, Is.EqualTo(e.TrailingEdge), $"job {job} shadow {s} TrailingEdge");
                Assert.That(a.AngleDegrees, Is.EqualTo(e.AngleDegrees), $"job {job} shadow {s} AngleDegrees");
                Assert.That(a.UnitX, Is.EqualTo(e.UnitX), $"job {job} shadow {s} UnitX");
                Assert.That(a.UnitZ, Is.EqualTo(e.UnitZ), $"job {job} shadow {s} UnitZ");
            }
        }
    }
}
