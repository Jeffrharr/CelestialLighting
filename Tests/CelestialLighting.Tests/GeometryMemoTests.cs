namespace CelestialLighting.Tests;

/// <summary>
/// Covers issue #12's per-frame memo. These tests exist because a memo bug is invisible from the
/// outside — a stale value is a plausible-looking number, not an exception — so the only place to
/// catch one is here, against the memo's own hit/miss accounting.
///
/// The stamp fields stand in for live state that this project cannot reference offline:
/// Frame = UnityEngine.Time.frameCount, Tick = Find.TickManager.TicksAbs, Variant = the packed mode
/// flags FrameStamp.Variant() builds, Scalar = the moon's synodic cycle position.
/// </summary>
[TestFixture]
public class GeometryMemoTests
{
    // Bit positions mirroring FrameStamp's private layout. Duplicated rather than exposed, because
    // the memo genuinely does not care what the bits mean — only that different modes produce
    // different ints — and widening FrameStamp's API just to test it would invite callers to build
    // their own stamps.
    private const int WarpEnabledBit = 1 << 0;
    private const int WarpMoonClockBit = 1 << 1;

    private static GeometryStamp Stamp(int frame, int tick = 100, int variant = 0, float scalar = 0f) =>
        new GeometryStamp(frame, tick, variant, scalar);

    private const int MapA = 1;
    private const int MapB = 2;

    // Counts how many times the memo actually ran the expensive computation, which is the thing under
    // test — the returned value is deliberately derived from the call count so a stale hit is
    // distinguishable from a fresh miss by value alone.
    private sealed class CountingSource
    {
        public int Calls;

        public float Next(float seed)
        {
            Calls++;
            return seed + Calls;
        }
    }

    [Test]
    public void RepeatedCallsWithinAFrameComputeOnce()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        // 14 is the per-map, per-frame call count issue #12 measured: 9 CurSkyTarget postfixes across
        // two weather workers, plus the shadow-strength and shadow-tilt paths.
        for (int i = 0; i < 14; i++)
            memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(1), "geometry was recomputed within a single frame");
        Assert.That(memo.Misses, Is.EqualTo(1));
        Assert.That(memo.Hits, Is.EqualTo(13));
    }

    [Test]
    public void CachedValueIsReturnedUnchanged()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        float first = memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);
        float second = memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);

        // Bit-identical, not merely close: the whole premise of the change is that no pinned harness
        // value moves, which only holds if a hit is the same float a recompute would have produced.
        Assert.That(second, Is.EqualTo(first));
        Assert.That(second, Is.EqualTo(11f));
    }

    [Test]
    public void NextFrameRecomputes()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 8), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(2));
    }

    [Test]
    public void NewTickWithinTheSameFrameRecomputes()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        // At 3x/4x speed the TickManager runs several ticks inside one rendered frame. A frame-only
        // key would freeze geometry across that boundary for any caller on the tick path.
        memo.Get(MapA, Stamp(frame: 7, tick: 100), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 7, tick: 101), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(2));
    }

    [Test]
    public void PausedGameKeepsCachingAcrossFramesOnlyWithinTheFrame()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        // Paused: the tick never advances, so the frame is the only thing separating these calls.
        // Each frame must still recompute, which is what bounds any input we failed to name in the
        // key to a single frame of staleness instead of forever.
        memo.Get(MapA, Stamp(frame: 7, tick: 100), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 8, tick: 100), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 9, tick: 100), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(3));
    }

    [Test]
    public void EachMapIsCachedIndependently()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        // Game.UpdatePlay calls MapUpdate() on every loaded map in the same frame, so two maps must
        // not share one cached sun — they sit on different tiles and therefore different latitudes.
        float a = memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);
        float b = memo.Get(MapB, Stamp(frame: 7), 20f, source.Next);
        float aAgain = memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(2));
        Assert.That(a, Is.EqualTo(11f));
        Assert.That(b, Is.EqualTo(22f));
        Assert.That(aAgain, Is.EqualTo(a), "map A's entry was evicted by map B");
    }

    // --- The warp-flag caveat from issue #12 ---
    //
    // The live harness flips SunClockAdapter.WarpEnabled and MoonPosition.WarpMoonClock mid-run to
    // capture the pre-§14 "before" half of an A/B. It executes exactly one scenario step per frame
    // (ScenarioDriver.RunNextStep, pumped from a Root_Play.Update postfix), so in practice a probe
    // always reads on a later frame than the SetFeature step that moved the flag. These tests pin the
    // stronger guarantee that we do not depend on that scheduling detail at all.

    [TestCase(WarpEnabledBit, TestName = "SunClockWarpFlipMidFrameRecomputes")]
    [TestCase(WarpMoonClockBit, TestName = "MoonClockWarpFlipMidFrameRecomputes")]
    public void ModeFlipWithinTheSameFrameAndTickRecomputes(int flagBit)
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        float warped = memo.Get(MapA, Stamp(frame: 7, tick: 100, variant: flagBit), 10f, source.Next);
        float unwarped = memo.Get(MapA, Stamp(frame: 7, tick: 100, variant: 0), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(2),
            "a dev warp flag flipped mid-frame was served a stale geometry value");
        Assert.That(unwarped, Is.Not.EqualTo(warped));
    }

    [Test]
    public void FlippingBackWithinTheSameFrameRecomputesRatherThanResurrecting()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        memo.Get(MapA, Stamp(frame: 7, variant: WarpEnabledBit), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 7, variant: 0), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 7, variant: WarpEnabledBit), 10f, source.Next);

        // One entry per map, so flipping back is a miss rather than a second cached variant. That is
        // intentional: the alternative is a per-variant table whose entries nothing ever evicts.
        Assert.That(source.Calls, Is.EqualTo(3));
    }

    [Test]
    public void MoonCyclePositionShiftMidFrameRecomputes()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        // The harness's eclipse staging slides the synodic cycle by writing
        // GameComponent_MoonPhase.debugSynodicShiftTicks, which changes the moon without changing the
        // tick. MoonPosition folds the resulting cycle position into the stamp for exactly this case.
        memo.Get(MapA, Stamp(frame: 7, scalar: 0.5f), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 7, scalar: 0.0f), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(2), "a staged eclipse was served a pre-stage moon");
    }

    [Test]
    public void SolarScalarIsIgnoredBecauseTheSunNeverSetsIt()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        // The sun passes Scalar = 0 always, so its stamps collapse to (frame, tick, variant) — this
        // just documents that WithScalar is opt-in and the sun does not accidentally opt in.
        memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 7).WithScalar(0f), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(1));
    }

    [Test]
    public void WithScalarPreservesEveryOtherField()
    {
        GeometryStamp derived = Stamp(frame: 7, tick: 100, variant: 3).WithScalar(0.25f);

        // The moon's stamp must be a strict superset of the sun's, since SkyForMap's answer is only
        // valid while the InputsForMap it is built on is.
        Assert.That(derived.Frame, Is.EqualTo(7));
        Assert.That(derived.Tick, Is.EqualTo(100));
        Assert.That(derived.Variant, Is.EqualTo(3));
        Assert.That(derived.Scalar, Is.EqualTo(0.25f));
    }

    [Test]
    public void NaNScalarStillCaches()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        // float.Equals, not ==, so a NaN cycle position degrades to an ordinary cached value rather
        // than a permanent miss that silently reinstates the cost this memo exists to remove.
        memo.Get(MapA, Stamp(frame: 7, scalar: float.NaN), 10f, source.Next);
        memo.Get(MapA, Stamp(frame: 7, scalar: float.NaN), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(1));
    }

    [Test]
    public void ThrowingComputeCachesNothing()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        Assert.Throws<InvalidOperationException>(() =>
            memo.Get<float>(MapA, Stamp(frame: 7), 10f, _ => throw new InvalidOperationException()));

        // A patch that throws mid-frame must not leave a half-built value stamped as valid for the
        // rest of that frame.
        float value = memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(1));
        Assert.That(value, Is.EqualTo(11f));
    }

    [Test]
    public void ClearForcesRecompute()
    {
        var memo = new GeometryMemo<float>();
        var source = new CountingSource();

        memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);
        memo.Clear();
        memo.Get(MapA, Stamp(frame: 7), 10f, source.Next);

        Assert.That(source.Calls, Is.EqualTo(2));
        Assert.That(memo.Hits, Is.EqualTo(0));
        Assert.That(memo.Misses, Is.EqualTo(1), "Clear did not reset the counters");
    }

    [Test]
    public void StampEqualityDistinguishesEveryField()
    {
        GeometryStamp baseline = Stamp(frame: 7, tick: 100, variant: 1, scalar: 0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(baseline.Equals(Stamp(frame: 7, tick: 100, variant: 1, scalar: 0.5f)), Is.True);
            Assert.That(baseline.Equals(Stamp(frame: 8, tick: 100, variant: 1, scalar: 0.5f)), Is.False);
            Assert.That(baseline.Equals(Stamp(frame: 7, tick: 101, variant: 1, scalar: 0.5f)), Is.False);
            Assert.That(baseline.Equals(Stamp(frame: 7, tick: 100, variant: 2, scalar: 0.5f)), Is.False);
            Assert.That(baseline.Equals(Stamp(frame: 7, tick: 100, variant: 1, scalar: 0.6f)), Is.False);
        });
    }

    [Test]
    public void EqualStampsShareAHashCode()
    {
        GeometryStamp left = Stamp(frame: 7, tick: 100, variant: 1, scalar: 0.5f);
        GeometryStamp right = Stamp(frame: 7, tick: 100, variant: 1, scalar: 0.5f);

        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
        Assert.That(left.Equals((object)right), Is.True);
    }
}
