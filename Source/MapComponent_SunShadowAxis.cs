using Verse;

namespace CelestialLighting;

// TOMBSTONE. This class has no behaviour and is scheduled for deletion.
//
// It used to be the live-state half of section 3's across-map shadow tilt: it owned the shadow
// AXIS each section's vertex alpha was baked against, and raised a private map-mesh flag once that
// axis had drifted far enough to matter. The tilt itself is gone (issue #26) — see DESIGN.md
// section 3 for what it was and why it went.
//
// WHY THE EMPTY CLASS SURVIVES THE FEATURE. Verse.Map.ExposeComponents scribes its component list
// with `Scribe_Collections.Look(ref components, "components", LookMode.Deep, this)`, so every save
// ever made with an older build of this mod carries a
// `<li Class="CelestialLighting.MapComponent_SunShadowAxis" />` node per map. Deleting the type
// outright does not silently drop that node: ScribeExtractor.SaveableFromNode cannot resolve the
// class, asks GetBestFallbackType for a substitute (which returns null — MapComponent matches none
// of its Thing/Hediff/Ability branches), logs "Could not find class ...", then dereferences that
// null on `type.IsAbstract`, throws, and logs a second error from its own catch. Two red errors per
// map. Map.FillComponents then strips the null entry and the next save omits it, so it is a
// one-time, self-healing, cosmetic failure — but it is a failure players see and report, and this
// mod is published, so a dozen inert lines are the cheaper side of that trade.
//
// SAFE TO DELETE once every save that could carry the node has been re-saved — in practice, one
// release after the one that removed the tilt. Nothing references this type, so deleting the file
// is the entire removal.
public class MapComponent_SunShadowAxis : MapComponent
{
    public MapComponent_SunShadowAxis(Map map)
        : base(map)
    {
    }
}
