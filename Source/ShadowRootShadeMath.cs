namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, the same discipline
// EavesMath.cs / EaveShadeMath.cs follow. Compiled into both Source (net481, runs inside RimWorld)
// and Tests (net8.0, runs standalone via `dotnet test`) through a linked <Compile Include>, so the
// exact shipped code is the code under test.
//
// Subsystem 30 (DESIGN.md §30): a caster whose sprite does not fill its own cell is drawn with a
// detached shadow — a lit strip between the sprite's edge and the shadow's root.
//
// THE HOLE IS THE ONE §15b ALREADY DOCUMENTED, met a second time. EaveShadeMath's header records
// it: vanilla's SectionLayer_SunShadows emits, per caster cell, a flat footprint quad whose four
// vertices carry alpha 0, and that alpha is both the displacement the shader applies AND what the
// fragment is drawn at — so the footprint renders FULLY TRANSPARENT and a caster never shades the
// cell it stands on. What that header then says is the reason nobody noticed:
//
//     "...because before §15 every caster was an edifice and a wall's sprite covers whatever
//      colour the ground under it is."
//
// That clause is true of a wooden wall and false of rough rock. Verse.Graphic_Linked.Print draws a
// linked building through Printer_Plane.PrintPlane(..., new Vector2(1f, 1f), ...) — the quad is
// EXACTLY one cell, so a sprite can never overhang. What varies is how much of that quad the
// texture's alpha actually covers, and the natural-rock atlas is deliberately jagged: its edge
// wanders inside the cell, leaving the ground beside it visible. Vanilla lights that ground,
// because the only thing that would have shaded it is the transparent footprint quad. The shadow
// therefore starts at the CELL outline while the sprite ends somewhere inside it, and the gap
// between the two reads as a shadow that has come unstuck from the thing casting it.
//
// WHY THIS NEEDS NO TEXTURE INSPECTION, which is the part worth internalising. There is no runtime
// question here about how much of its cell a given sprite covers, and asking would mean reading
// texture alpha per def. We do not have to: shade the cell unconditionally and a sprite that DOES
// fill its cell simply draws over the shade and hides it. The fix costs nothing where it is not
// needed and is exact where it is — which is why it can apply to every caster, vanilla or modded,
// without a def list.
//
// DOORS ARE THE ONE EXCLUSION, and it is a real case rather than a defensive one. Vanilla's DoorBase
// declares staticSunShadowHeight 1.0, so a door is a full-height caster like any wall, and while it
// is CLOSED its sprite fills the cell and shading underneath is invisible either way. But an open
// door is drawn as two halves slid aside (Building_Door, drawerType RealtimeOnly), so the middle of
// the cell shows floor — and shading it would paint a dark square across an open doorway, which is
// an artifact vanilla does not have. That is the opposite of this subsystem's purpose, so a door
// cell is left alone. The cost of excluding it is only the closed case, where nothing was visible.
//
// Note this is deliberately NOT gated on caster height. The shade's depth is not a function of how
// tall the caster is: the ground at the foot of any opaque object is out of the sun, whether the
// object is a 1.0 wall or a 0.17 dresser. Height controls how far the shadow is thrown, which is
// the skirt's job and already correct. See DESIGN.md §30 for the measurement.
public static class ShadowRootShadeMath
{
    // Does the cell under a caster take the shadow root's shade?
    //
    // `casterHeight` is vanilla's ThingDef.staticSunShadowHeight for whatever edifice stands here,
    // 0 for an empty cell — the same encoding EaveShadowGrid resolves and the same "casts nothing"
    // convention vanilla itself uses. Anything above 0 is a caster and takes the shade.
    //
    // Written as `> 0f` rather than `>= 0f` so a def that explicitly disables its static shadow
    // (vanilla's FenceGate does exactly this, `<staticSunShadowHeight>0</staticSunShadowHeight>`)
    // stays excluded: it casts no skirt, so there is no root for a root shade to join up with.
    public static bool TakesRootShade(float casterHeight, bool isDoor) =>
        !isDoor && casterHeight > 0f;
}
