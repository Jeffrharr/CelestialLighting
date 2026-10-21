namespace CelestialLighting.Tests;

// Offline coverage for what zooming in does to the drawn cloud sheets (Source/CloudZoomMath.cs),
// linked into this project via <Compile Include> so these exercise the exact code that ships.
[TestFixture]
public class CloudZoomMathTests
{
    private const float Tolerance = 1e-5f;

    // Vanilla's PC and Steam Deck zoom ranges (CameraMapConfig.sizeRange).
    private const float PcMin = 11f;
    private const float DeckMin = 7.2f;
    private const float Max = 60f;

    [TestCase(11f, 0f, TestName = "ClosestZoomIsZero")]
    [TestCase(60f, 1f, TestName = "FurthestZoomIsOne")]
    [TestCase(35.5f, 0.5f, TestName = "MidRangeIsHalf")]
    [TestCase(5f, 0f, TestName = "BelowRangeClampsToZero")]
    [TestCase(90f, 1f, TestName = "AboveRangeClampsToOne")]
    public void ZoomFractionSpansThePcRange(float rootSize, float expected)
    {
        Assert.That(CloudZoomMath.ZoomFraction(rootSize, PcMin, Max), Is.EqualTo(expected).Within(Tolerance));
    }

    // The reason thresholds are fractions: the closest zoom is 0 on BOTH platforms, so a fade lands
    // at the same point on the scroll wheel whichever minimum vanilla picked.
    [Test]
    public void ClosestZoomIsZeroOnTheSteamDeckToo()
    {
        Assert.That(CloudZoomMath.ZoomFraction(DeckMin, DeckMin, Max), Is.EqualTo(0f).Within(Tolerance));
    }

    // A corrupt camera config must give the shipped sky, not an invisible one: every degenerate input
    // answers "fully zoomed out", where every mode is a no-op.
    [TestCase(30f, 60f, 60f, TestName = "EmptyRangeReadsZoomedOut")]
    [TestCase(30f, 60f, 11f, TestName = "InvertedRangeReadsZoomedOut")]
    [TestCase(float.NaN, 11f, 60f, TestName = "NaNSizeReadsZoomedOut")]
    [TestCase(30f, float.NaN, 60f, TestName = "NaNRangeReadsZoomedOut")]
    public void DegenerateCameraReadsAsZoomedOut(float rootSize, float min, float max)
    {
        Assert.That(CloudZoomMath.ZoomFraction(rootSize, min, max), Is.EqualTo(1f));
    }

    // Off is the shipped look and must stay byte-identical at every zoom: full opacity, unit scale.
    [TestCase(0f)]
    [TestCase(0.1f)]
    [TestCase(0.5f)]
    [TestCase(1f)]
    public void OffChangesNothingAtAnyZoom(float zoom)
    {
        Assert.That(CloudZoomMath.OpacityAt(CloudZoomMath.Mode.Off, zoom), Is.EqualTo(1f));
        Assert.That(CloudZoomMath.ScaleAt(CloudZoomMath.Mode.Off, zoom), Is.EqualTo(1f));
    }

    // Zoomed all the way out, every mode is the shipped look.
    [TestCase(CloudZoomMath.Mode.Fade)]
    [TestCase(CloudZoomMath.Mode.FlyThrough)]
    public void EveryModeIsInertZoomedOut(CloudZoomMath.Mode mode)
    {
        Assert.That(CloudZoomMath.OpacityAt(mode, 1f), Is.EqualTo(1f));
        Assert.That(CloudZoomMath.ScaleAt(mode, 1f), Is.EqualTo(1f));
    }

    // Fully gone at the closest zoom — a floor would be exactly the fog-like wash this removes.
    [TestCase(CloudZoomMath.Mode.Fade)]
    [TestCase(CloudZoomMath.Mode.FlyThrough)]
    public void EveryModeIsGoneAtTheClosestZoom(CloudZoomMath.Mode mode)
    {
        Assert.That(CloudZoomMath.OpacityAt(mode, 0f), Is.EqualTo(0f));
    }

    // Monotone in zoom for both modes: zooming in never brings cloud back.
    [TestCase(CloudZoomMath.Mode.Fade)]
    [TestCase(CloudZoomMath.Mode.FlyThrough)]
    public void OpacityNeverRisesAsYouZoomIn(CloudZoomMath.Mode mode)
    {
        float previous = CloudZoomMath.OpacityAt(mode, 1f);
        for (int step = 99; step >= 0; step--)
        {
            float opacity = CloudZoomMath.OpacityAt(mode, step / 100f);
            Assert.That(opacity, Is.LessThanOrEqualTo(previous + Tolerance), $"zoom {step / 100f}");
            previous = opacity;
        }
    }

    // The behavioural difference between the two: at a mid-close framing Fade is already partly
    // faded while FlyThrough is still fully drawn.
    [Test]
    public void FadeIsGradualWhereFlyThroughIsStillFull()
    {
        const float zoom = 0.2f;
        Assert.That(CloudZoomMath.OpacityAt(CloudZoomMath.Mode.Fade, zoom), Is.InRange(0.1f, 0.9f));
        Assert.That(CloudZoomMath.OpacityAt(CloudZoomMath.Mode.FlyThrough, zoom), Is.EqualTo(1f));
    }

    // The window's ends, and that the middle is genuinely between them (a window with width, not a
    // cut that would pop as RimWorld's eased zoom crossed it).
    [Test]
    public void FlyThroughFadesInsideItsWindow()
    {
        Assert.That(CloudZoomMath.OpacityAt(CloudZoomMath.Mode.FlyThrough, CloudZoomMath.FlyThroughStart),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(CloudZoomMath.OpacityAt(CloudZoomMath.Mode.FlyThrough, CloudZoomMath.FlyThroughEnd),
            Is.EqualTo(0f).Within(Tolerance));

        float middle = (CloudZoomMath.FlyThroughStart + CloudZoomMath.FlyThroughEnd) / 2f;
        Assert.That(CloudZoomMath.OpacityAt(CloudZoomMath.Mode.FlyThrough, middle), Is.EqualTo(0.5f).Within(Tolerance));
    }

    // Growth tracks the fade: unit scale while fully drawn, full growth once gone, and only
    // FlyThrough grows at all.
    [Test]
    public void FlyThroughGrowsInStepWithItsFade()
    {
        Assert.That(CloudZoomMath.ScaleAt(CloudZoomMath.Mode.FlyThrough, CloudZoomMath.FlyThroughStart),
            Is.EqualTo(1f).Within(Tolerance));
        Assert.That(CloudZoomMath.ScaleAt(CloudZoomMath.Mode.FlyThrough, 0f),
            Is.EqualTo(1f + CloudZoomMath.FlyThroughGrowth).Within(Tolerance));
        Assert.That(CloudZoomMath.ScaleAt(CloudZoomMath.Mode.Fade, 0f), Is.EqualTo(1f));
    }

    [TestCase(10f, 10f, 2f, 10f, TestName = "PivotStaysPut")]
    [TestCase(15f, 10f, 2f, 20f, TestName = "OffsetDoubles")]
    [TestCase(5f, 10f, 2f, 0f, TestName = "NegativeOffsetDoublesOutward")]
    [TestCase(15f, 10f, 1f, 15f, TestName = "UnitScaleIsIdentity")]
    public void ScaleAboutMovesSheetsAwayFromThePivot(float centre, float pivot, float scale, float expected)
    {
        Assert.That(CloudZoomMath.ScaleAbout(centre, pivot, scale), Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void NaNZoomIsInert()
    {
        Assert.That(CloudZoomMath.OpacityAt(CloudZoomMath.Mode.Fade, float.NaN), Is.EqualTo(1f));
        Assert.That(CloudZoomMath.ScaleAt(CloudZoomMath.Mode.FlyThrough, float.NaN), Is.EqualTo(1f));
    }
}
