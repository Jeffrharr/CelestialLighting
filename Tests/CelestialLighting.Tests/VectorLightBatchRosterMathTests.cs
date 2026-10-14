namespace CelestialLighting.Tests;

/// <summary>
/// The batched draw's roster timing (<see cref="VectorLightBatchRosterMath"/>): when an emitter has
/// settled, and when a batch rebuilds to admit settled emitters.
/// </summary>
[TestFixture]
public class VectorLightBatchRosterMathTests
{
    private const int Settle = VectorLightBatchRosterMath.SettleFrames;
    private const int Rejoin = VectorLightBatchRosterMath.RejoinFrames;
    private const int None = VectorLightBatchRosterMath.NotPending;

    [TestCase(100, 100, false)]   // changed this very frame
    [TestCase(159, 100, false)]   // one frame short
    [TestCase(160, 100, true)]    // exactly the settle window
    [TestCase(1000, 100, true)]
    public void Settled_NeedsTheWholeWindow(int frame, int since, bool expected)
    {
        Assert.That(VectorLightBatchRosterMath.Settled(frame, since, Settle), Is.EqualTo(expected));
    }

    [Test]
    public void PendingSince_StartsOnTheFirstJoiner()
    {
        Assert.That(VectorLightBatchRosterMath.PendingSince(None, joinable: 1, frame: 500), Is.EqualTo(500));
    }

    // The bound on the wait depends on this: a steady trickle of new joiners must not keep pushing
    // the rebuild back.
    [Test]
    public void PendingSince_LaterJoinersDoNotRestartTheClock()
    {
        Assert.That(VectorLightBatchRosterMath.PendingSince(500, joinable: 3, frame: 650), Is.EqualTo(500));
    }

    [TestCase(None)]
    [TestCase(500)]
    public void PendingSince_ClearsWhenNobodyIsWaiting(int pending)
    {
        Assert.That(VectorLightBatchRosterMath.PendingSince(pending, joinable: 0, frame: 650), Is.EqualTo(None));
    }

    [Test]
    public void ShouldRebuild_NeverWithoutJoiners()
    {
        Assert.That(VectorLightBatchRosterMath.ShouldRebuild(false, 0, None, 1000, Rejoin), Is.False);
        Assert.That(VectorLightBatchRosterMath.ShouldRebuild(true, 0, 0, 1000, Rejoin), Is.False);
    }

    // A batch with no mesh has nothing a rebuild could cost, so it takes its first members at once.
    [Test]
    public void ShouldRebuild_AtOnceWhenThereIsNoMesh()
    {
        Assert.That(VectorLightBatchRosterMath.ShouldRebuild(false, 1, 500, 500, Rejoin), Is.True);
    }

    [TestCase(500, 500, false)]
    [TestCase(799, 500, false)]
    [TestCase(800, 500, true)]
    public void ShouldRebuild_WaitsTheRejoinWindowWhenItHasAMesh(int frame, int pending, bool expected)
    {
        Assert.That(VectorLightBatchRosterMath.ShouldRebuild(true, 2, pending, frame, Rejoin), Is.EqualTo(expected));
    }

    // Under steady churn (a new joiner every frame) the batch rebuilds once per window, not once
    // per joiner: the property the whole roster exists for. (Rejoin + 1) because the frame that
    // rebuilds clears the wait and the next joiner starts it again one frame later.
    [Test]
    public void ShouldRebuild_SteadyChurnRebuildsOncePerWindow()
    {
        int pending = None;
        bool hasMesh = true;
        int rebuilds = 0;

        for (int frame = 0; frame < (Rejoin + 1) * 10; frame++)
        {
            pending = VectorLightBatchRosterMath.PendingSince(pending, joinable: 1, frame);

            if (VectorLightBatchRosterMath.ShouldRebuild(hasMesh, 1, pending, frame, Rejoin))
            {
                rebuilds++;
                pending = None;
            }
        }

        Assert.That(rebuilds, Is.EqualTo(10));
    }
}
