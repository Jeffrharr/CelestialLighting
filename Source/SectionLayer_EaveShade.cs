using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §15b's eave shade, as a map draw layer of our own — the shadow a roof casts on the ground directly
// beneath it, which the sun-shadow mesh structurally cannot draw (EaveShadeMath's header has the
// measurement and the reason).
//
// Verse.Section's constructor instantiates every non-abstract SectionLayer subclass it can find
// (typeof(SectionLayer).AllSubclassesNonAbstract()), so this needs no Harmony patch to be drawn —
// declaring the class is the registration. Same free-registration trick as §9's
// SectionLayer_NightDesaturation, and the same reason it collides with nothing: it is a new mesh
// drawn alongside vanilla's, not a hijack of one.
//
// FAN-OUT. This is one of four separate places that add work to a map-mesh dirty flag (the other
// three are SectionLayer_NightDesaturation, Patch_ShadowRoofInvalidation and
// Patch_IndoorSkyOcclusion), and no one of the four can show the total. DESIGN.md §16 has the
// flag-to-layers table and the live timings, including why the Buildings subscription below stays
// despite issue #10 naming it the narrowing candidate: measured, this layer is the cheapest of the
// four at 18.6 µs per section regenerate — but 13.2 of those µs used to be paid by players who had
// eave shade switched off, which is what the gate in Regenerate below removes.
//
// ALTITUDE. AltitudeLayer.Shadows, the same altitude vanilla's own sun shadows use, because this IS
// one — it lands on terrain, under things and pawns, exactly as the cast band beside it does. The two
// meshes are coplanar, which is harmless here: both are transparent-queue multiplies, so they
// compose in any draw order and neither writes depth for the other to fight over.
public class SectionLayer_EaveShade : SectionLayer
{
    // Vertex alpha is per-CELL only: an eave is shaded, everything else is not. Deliberately not
    // averaged across neighbouring cells the way §9's wash is — a roofline is a physical edge and the
    // shadow it throws has a hard one, so softening this side of it would leave a bright lip along
    // the boundary, which is a smaller version of the artifact the subsystem exists to remove.
    private static readonly Color32 Shaded = new Color32(255, 255, 255, 255);
    private static readonly Color32 Unshaded = new Color32(255, 255, 255, 0);

    // The feature flag, plus the two conditions under which vanilla stops drawing sun shadows at all
    // (SectionLayer_SunShadows.Visible checks exactly these): the dev-mode shadow toggle, and a biome
    // that declares no shadows. This IS a sun shadow, so it has to disappear with them — leaving a
    // porch shaded on a map whose cast shadows do not exist would be the mismatch this subsystem
    // removes, inverted.
    //
    // "How deep is the shadow right now" is deliberately NOT here: it belongs to the material's
    // alpha, which is zero when there is no shadow anyway, and gating visibility on a brightness test
    // would make the layer flicker in and out across dusk for no gain.
    public override bool Visible =>
        CelestialLightingFeatures.EaveShade
        && DebugViewSettings.drawShadows
        && MapSky.DrawsShadows(base.Map);

    // The night twin of SectionLayer_NightDesaturation.DrawLayer, and the same trade: skip the
    // submission on frames where the shade material is fully transparent, which is every night plus
    // any day the cast shadow itself has been zeroed. Read that override's comment for why this is a
    // draw-time skip rather than a clause on Visible — Section.TryUpdate ignores Visible and clears
    // Dirty, so hiding this layer by brightness would strand a stale bake at dawn.
    //
    // Note this skip and Regenerate's !Visible gate below are answering different questions and are
    // both needed. This one is "the shade is transparent right now", which is a fact about the clock
    // and reverses itself every dawn, so the bake must survive it. That one is "the player switched
    // the feature off", which is a fact about the settings and is what makes the bake pointless.
    public override void DrawLayer()
    {
        if (!EaveShadeOverlay.Drawing)
            return;

        base.DrawLayer();
    }

    public SectionLayer_EaveShade(Section section)
        : base(section)
    {
        // Roofs is the obvious signal. Buildings is needed too, and for a subtler reason: eave status
        // is a question about the cell's ROOM, so enclosing or opening a room re-answers it for cells
        // nowhere near the wall that changed — walling in a porch stops it being a porch. This is the
        // same subscription SectionLayer_SunShadows carries once Patch_ShadowRoofInvalidation has
        // widened it, so the two halves of §15 refresh on exactly the same events.
        relevantChangeTypes = (ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.Buildings;
    }

    public override void Regenerate()
    {
        // Verse.Section.TryUpdate is what turns a dirty flag into a call to this method, and it does
        // NOT consult Visible first — RegenerateAllLayers and RegenerateDirtyLayers do, TryUpdate
        // does not (decompiled 1.6; DESIGN.md §16 has the loop). So without this gate every player
        // who has eave shade switched off still bakes a mesh nothing will ever draw, on every roof
        // edit and — because GlowGrid.DirtyCell raises Roofs as well as GroundGlow — on every lamp
        // toggle and every fire. Measured live at 13.2 µs per section regenerate with the feature
        // off, 71% of the 18.6 µs the layer costs with it on.
        //
        // Night desaturation had exactly this bug and this fix; the two layers are deliberately the
        // same shape here so the next reader only has to learn it once.
        if (!Visible)
        {
            DiscardMesh();
            return;
        }

        LayerSubMesh subMesh = GetSubMesh(EaveShadeOverlay.Material);
        if (subMesh.mesh.vertexCount == 0)
            SectionLayerGeometryMaker_Solid.MakeBaseGeometry(section, subMesh, AltitudeLayer.Shadows);

        subMesh.Clear(MeshParts.Colors);

        CellRect rect = section.CellRect;
        for (int x = rect.minX; x <= rect.maxX; x++)
        {
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                AddCellColors(subMesh, new IntVec3(x, 0, z));
            }
        }

        subMesh.disabled = false;
        subMesh.FinalizeMesh(MeshParts.Colors);
    }

    // The other half of the gate: leaves nothing drawable behind while the feature is off.
    //
    // DrawLayer's own Visible check already stops the shade appearing, so this is not what makes it
    // vanish when the box is unticked. What it prevents is the reverse: TryUpdate clears the layer's
    // Dirty flag whether or not Regenerate did anything, so a mesh baked before the toggle would sit
    // in subMeshes describing a roofline that has since been rebuilt, ready to draw the instant the
    // feature came back. Disabled, the worst case is one frame of nothing instead of one frame of a
    // stale shade — and EaveShadowRedraw dirties the map on the toggle, so that frame does not
    // happen either.
    //
    // Disabled rather than cleared, specifically: Clear + FinalizeMesh would re-upload the mesh to
    // the GPU, and that upload is most of the cost this gate exists to avoid. Regenerate's tail sets
    // `disabled = false` again on the first bake after the feature returns.
    private void DiscardMesh()
    {
        for (int i = 0; i < subMeshes.Count; i++)
            subMeshes[i].disabled = true;
    }

    // SectionLayerGeometryMaker_Solid emits nine vertices per cell (four corners, four edge midpoints,
    // then the centre) and they all take this cell's own verdict — see Shaded/Unshaded above for why
    // no averaging happens here.
    private void AddCellColors(LayerSubMesh subMesh, IntVec3 cell)
    {
        Color32 color = EaveCells.IsEave(base.Map, cell) ? Shaded : Unshaded;
        for (int i = 0; i < 9; i++)
            subMesh.colors.Add(color);
    }
}
