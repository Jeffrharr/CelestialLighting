namespace CelestialLighting;

// A counter that changes whenever the mod's settings are pushed to runtime, so the per-frame
// geometry memos can key on "which settings are in force" without knowing what any of them are.
//
// WHY IT EXISTS, AND WHAT IT DOES *NOT* CURRENTLY DO. GeometryStamp keys on Time.frameCount, which
// means every cached value expires after one frame. That single field is quietly doing two jobs:
// bounding how long a forgotten input can be served stale, and — as a side effect nobody chose —
// making a settings change visible immediately, because a value that lives one frame cannot outlast
// the frame the player moved a slider on.
//
// So at today's one-frame span this counter is a NO-OP, and that is stated plainly rather than
// dressed up: it can only ever cause extra cache misses, never fewer, and there is no frame on which
// it changes what the player sees. It is here to make the second job DELIBERATE. The invariant
// "settings are an input to the geometry key" is currently true by accident of an unrelated field,
// and an accidental invariant is one that a later change can delete without noticing.
//
// The later change is a specific and likely one. §28 measured a paused colony at 96-100% of a
// running colony's cost, because a frozen tick still yields a fresh frame number every frame and so
// every tick-derived memo recomputes an answer that cannot have moved. The obvious fix is to let a
// cached value live across frames while the tick stands still — at which point the settings screen,
// which is paused by definition and where the player is dragging a slider watching the sky respond,
// becomes the worst possible place for stale reads. That attempt is on the `cadence-probe` branch
// and did not ship, for reasons recorded there. This half is merged on its own because it is right
// either way and costs an integer.
//
// UNCONDITIONAL, NOT CHANGE-DETECTED, which is the opposite call from EaveShadowRedraw and its two
// siblings. They compare against a remembered value first because what they trigger is expensive —
// WholeMapChanged rebuilds every section on the map, fine once on a click and ruinous at 60 Hz. This
// is an increment. Paying it on every ApplyToRuntime buys a property change detection cannot give:
// no list of fields to keep in sync. A change-detected version would need extending every time
// somebody adds a slider, and forgetting would not fail a test — it would surface as a setting that
// does nothing until the window is closed, which is exactly the bug class this exists to prevent.
//
// The practical consequence, once a span wider than one frame exists, is that nothing is memoised
// across frames while the settings window is open, because ApplyToRuntime runs every frame it is
// open. That is the right trade in both directions: full liveness exactly when the player is
// watching for it, and full caching the rest of the time, when ApplyToRuntime runs only at startup
// and on window close.
public static class SettingsGeneration
{
    // Not [ThreadStatic] and not interlocked: every writer and reader is on the main thread
    // (ApplyToRuntime from the settings window and from startup, FrameStamp.Current from the render
    // path), the same assumption GeometryMemo's own header records and for the same reason.
    public static int Current { get; private set; }

    // Wrapping is harmless and deliberately unguarded. The value is never compared for order, only
    // for difference, so the only failure would be a generation that wrapped to exactly the value a
    // live cache entry was stored under — which needs 2^23 settings writes inside one session, by
    // which point the player has been dragging a slider for a very long time.
    public static void Bump() => Current++;
}
