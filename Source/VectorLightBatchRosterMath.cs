namespace CelestialLighting;

// When a batched emitter may join its batch, and when a batch rebuilds its combined mesh to let
// it. Pure: frame numbers in, verdicts out. The adapter is VectorLightDrawBatch.
//
// WHY THE BATCHED DRAW NEEDS RULES ABOUT TIME AT ALL. Measured on the 500-lamp, 50-pawn colony
// plate, rebuilding a batch's combined mesh whenever any member's geometry moved cost 2.5x the
// per-emitter draw on a frame with pawns walking (0.71 ms against 0.28), because a living colony
// moves some member of nearly every batch every frame. The roster turns that around: a member that
// moved leaves its slot by having its strength zeroed (no rebuild) and draws on its own, and the
// combined mesh is rebuilt only to let SETTLED emitters back in, rarely.
//
// TWO WAITS, AND BOTH ARE NEEDED. Settling alone does not cut the rebuild count. The colony
// dirties about one lamp a frame, mostly different lamps, so letting each one back the moment it
// settled would still rebuild about one batch a frame. The second wait gathers rejoins: a batch
// rebuilds only once its oldest joinable emitter has waited RejoinFrames, which bounds it to one
// rebuild per batch per RejoinFrames, however busy the colony is.
internal static class VectorLightBatchRosterMath
{
    // How long an emitter's geometry must have held still before it may join a batch. A lamp beside
    // a door that swings every few seconds never reaches this, so it stays on the per-emitter draw
    // and never costs a rebuild. One second at 60 fps.
    public const int SettleFrames = 60;

    // How long a batch gathers joinable emitters before it rebuilds to admit them. Five seconds at
    // 60 fps: long enough that a busy colony's rebuilds are a trickle, short enough that a lamp
    // built or a camera pan is batched again before anyone could profile the gap.
    public const int RejoinFrames = 300;

    // Sentinel for "nothing is waiting to join".
    public const int NotPending = -1;

    // Whether an emitter whose geometry last changed at `settledSince` has held still long enough.
    public static bool Settled(int frame, int settledSince, int settleFrames)
    {
        return frame - settledSince >= settleFrames;
    }

    // When a batch's wait for joiners started, given what it was before this frame. The first frame
    // with a joiner starts the clock; later joiners do not restart it, which is what bounds the wait
    // to RejoinFrames from the OLDEST joiner rather than letting a steady trickle defer it forever.
    // No joiner this frame clears it, because a joiner that went dirty again is no longer waiting.
    public static int PendingSince(int pendingSince, int joinable, int frame)
    {
        if (joinable == 0)
            return NotPending;

        return pendingSince == NotPending ? frame : pendingSince;
    }

    // Whether the batch rebuilds its combined mesh this frame. A batch with no mesh has nothing to
    // protect, so it takes its joiners at once — that is the first frame after a map settles.
    public static bool ShouldRebuild(
        bool hasMesh, int joinable, int pendingSince, int frame, int rejoinFrames)
    {
        if (joinable == 0)
            return false;

        if (!hasMesh)
            return true;

        return pendingSince != NotPending && frame - pendingSince >= rejoinFrames;
    }
}
