namespace CelestialLighting;

// The pure policy behind the Clouds interop (Source/CloudsCompat.cs): given one of this mod's
// cloud lanes and whether another mod is already drawing the clouds themselves, does our lane still
// draw? No UnityEngine, no Verse, no reflection — the live half is only "is that mod loaded".
//
// WHY THIS IS A TABLE AND NOT AN `if` AT EACH CALL SITE. The interesting decision here is not the
// boolean, it is WHICH of our six cloud lanes stand down, and that is a design ruling that has to be
// written down somewhere it can be read and tested rather than reconstructed by grepping three
// guard clauses. The split below is the whole of the interop; everything else is plumbing.
public enum CloudLane
{
    // §22 — a Clear day's sky drifting toward an overcast palette. A COLOUR, nothing drawn.
    SkyTint,

    // §22's "- N% cloudy" weather-panel suffix. Text.
    CoverLabel,

    // §23 — the flat scaling of §8's colour-temperature tint under a deck. A colour again.
    ColourTemperature,

    // §23b — warm underlit patches drawn AT our cloud sheets' positions.
    UnderlightLayer,

    // §23c — the daylight shadow wash drawn AT our cloud sheets' positions.
    GroundShadow,

    // §25 — the cloud sheets themselves, drawn between the camera and the map.
    DrawnSheet,
}

public static class CloudsCompatMath
{
    // The dividing line, and the only line: does this lane's appearance depend on WHERE we decided a
    // cloud is?
    //
    // That is the property that cannot survive a second mod drawing clouds. A sky tint is an opinion
    // about the whole sky and stays true whoever renders the deck — a 40%-cloudy afternoon is a
    // slightly greyer afternoon no matter which mod puts the shapes up there. A shadow blob crawling
    // across the colony is a claim about a SPECIFIC cloud being at a specific place, and with someone
    // else's particles overhead that claim is visibly false: the shadow passes under clear sky while
    // an actual cloud drifts past casting nothing. The player does not read that as two mods, they
    // read it as ours being broken.
    //
    // So positional lanes stand down and non-positional ones do not. Note that this is NOT a
    // duplicate-visuals rule — only §25 is an outright duplicate of what Clouds draws. §23b and §23c
    // draw things Clouds does not do at all (it is a pure overlay: it never touches the sky glow, the
    // shadow layer or the sky target). We give those up anyway, because a correct effect keyed to the
    // wrong sky is worse than no effect.
    public static bool LaneIsPositional(CloudLane lane) =>
        lane == CloudLane.UnderlightLayer
        || lane == CloudLane.GroundShadow
        || lane == CloudLane.DrawnSheet;

    // Whether the lane draws at all, folding its own feature flag in.
    //
    // The flag comes FIRST in the sense that matters: this can only ever take a lane from on to off,
    // never the reverse, so the interop can never resurrect something the player switched off. And
    // suppression here means the lane's adapter returns exactly 0 and skips its draw call, which is
    // the same "off is the pre-feature baseline exactly" contract every feature flag in this mod
    // keeps (CelestialLightingFeatures' header) — the interop reuses that path rather than opening a
    // second one.
    public static bool LaneDraws(CloudLane lane, bool featureEnabled, bool externalCloudsDrawn) =>
        featureEnabled && !(externalCloudsDrawn && LaneIsPositional(lane));
}
