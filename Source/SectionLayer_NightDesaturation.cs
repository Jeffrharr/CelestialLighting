using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §9's per-cell desaturation, as a map draw layer of our own.
//
// Verse.Section's constructor instantiates every non-abstract SectionLayer subclass it can find
// (typeof(SectionLayer).AllSubclassesNonAbstract()), so this needs no patch to be drawn — declaring
// the class is the registration. It is regenerated on the same signal vanilla's own lighting overlay
// uses (MapMeshFlagDefOf.GroundGlow), because it answers a question about the same grid.
//
// WHY A WHOLE LAYER. See NightDesaturationMath's header for the two channels that cannot express
// this: the camera saturation is global (it greys campfires), and the sky tint multiplies (it cannot
// desaturate at all). Alpha compositing toward a grey IS lerp(colour, grey, t) — the desaturation
// operation — and a mesh can carry a different alpha per vertex. That is the whole trick, and it
// needs no replacement shader for MatBases.LightOverlay, which was the reason the per-cell version
// was previously judged impossible: it is a new mesh alongside vanilla's, not a hijack of one.
//
// FAN-OUT. This is one of four separate places that add work to a map-mesh dirty flag (the other
// three are SectionLayer_EaveShade, Patch_ShadowRoofInvalidation and Patch_IndoorSkyOcclusion), and
// no one of the four can show the total. DESIGN.md §16 has the flag-to-layers table and the live
// timings. Read it before adding a fifth or widening any of the four: measured, THIS layer is the
// expensive one — 271 µs per section regenerate, two thirds of everything the mod adds to a roof
// edit, and paid in full even when the feature below is switched off, because Verse.Section's
// TryUpdate does not consult Visible.
//
// ALTITUDE. AltitudeLayer.Weather, which is BELOW AltitudeLayer.LightingOverlay. So the wash lands on
// the scene first and vanilla's night multiply darkens the result afterwards, rather than sitting on
// top of the darkness as a grey haze. It is above every thing/pawn altitude, so an item lying on
// unlit ground desaturates with the ground it lies on.
public class SectionLayer_NightDesaturation : SectionLayer
{
    // Only the feature flag. "How dark is it" belongs to the material's alpha, not here: at zero
    // alpha the mesh is invisible anyway, and hiding the layer per-frame on a brightness test would
    // make the layer's visibility flicker across dusk for no gain.
    public override bool Visible => CelestialLightingFeatures.LowLightDesaturation;

    public SectionLayer_NightDesaturation(Section section)
        : base(section)
    {
        // Same signal as SectionLayer_LightingOverlay: light changes move the wash, and roof changes
        // move the light. Anything else that dirties a section leaves these alphas valid.
        relevantChangeTypes = (ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.GroundGlow;
    }

    public override void Regenerate()
    {
        // Verse.Section.TryUpdate does NOT consult Visible before calling this — only DrawLayer does
        // (see DESIGN.md §16) — so without this gate every player who has §9 switched off still pays
        // the full bake on every lamp toggle, fire, and roof edit for a mesh that is never drawn.
        // Measured live at 271 µs per section regenerate with the feature off.
        if (!Visible)
        {
            DiscardMesh();
            return;
        }

        LayerSubMesh subMesh = GetSubMesh(NightDesaturationOverlay.Material);
        if (subMesh.mesh.vertexCount == 0)
            SectionLayerGeometryMaker_Solid.MakeBaseGeometry(section, subMesh, AltitudeLayer.Weather);

        subMesh.Clear(MeshParts.Colors);

        CellRect rect = section.CellRect;

        // One glow query per cell of the section-plus-skirt window, instead of nine per cell inside
        // it. See NightWashWindow's header for the geometry and the measurement behind it.
        NightWashWindow wash = ResolveWash(rect);

        for (int x = rect.minX; x <= rect.maxX; x++)
        {
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                AddCellColors(subMesh, in wash, x, z);
            }
        }

        subMesh.disabled = false;
        subMesh.FinalizeMesh(MeshParts.Colors);
    }

    // Leaves nothing drawable behind when the feature is off.
    //
    // DrawLayer's own Visible check already stops the layer being drawn, so this is not what makes the
    // wash disappear when a player unticks the box mid-game. What it prevents is the opposite
    // direction: TryUpdate clears the layer's Dirty flag even when Regenerate returns early, so a mesh
    // baked before the toggle would otherwise sit in subMeshes describing a glow grid that has since
    // moved, ready to be drawn the instant the feature came back on. Disabling it means the worst case
    // there is one frame of nothing rather than one frame of a stale wash — and NightDesaturationRedraw
    // dirties the map on the toggle so that frame does not happen either.
    //
    // Disabling rather than clearing the colours: Clear + FinalizeMesh would re-upload the mesh to the
    // GPU, which is a large part of the cost this gate exists to avoid. Regenerate's tail sets
    // `disabled = false` again on the first bake after the feature comes back.
    private void DiscardMesh()
    {
        for (int i = 0; i < subMeshes.Count; i++)
            subMeshes[i].disabled = true;
    }

    // Reads local glow once for every cell the vertex loop can ask about — the section plus a one-cell
    // skirt, clipped at the map edge — and hands each reading to the pure window, which applies
    // NightDesaturationMath.CellWash and stores the result.
    //
    // Local light only — ignoreSky: true. The sky's own contribution is already the whole of
    // PurkinjeMath's factor, which scales this layer through the material's alpha; counting it again
    // per cell would both double it and, worse, invert the intent, since a brightening sky would start
    // exempting the outdoor cells the effect is for.
    private NightWashWindow ResolveWash(CellRect rect)
    {
        Map map = base.Map;
        NightWashWindow wash = NightWashWindow.ForSection(
            rect.minX, rect.minZ, rect.maxX, rect.maxZ, map.Size.x, map.Size.z);

        GlowGrid glowGrid = map.glowGrid;
        for (int z = wash.MinZ; z <= wash.MaxZ; z++)
        {
            for (int x = wash.MinX; x <= wash.MaxX; x++)
            {
                wash.Resolve(
                    x,
                    z,
                    glowGrid.GroundGlowAt(new IntVec3(x, 0, z), ignoreCavePlants: false, ignoreSky: true));
            }
        }

        return wash;
    }

    // The nine vertices SectionLayerGeometryMaker_Solid emits per cell, in its order: four corners,
    // four edge midpoints, then the centre. Each is averaged over the cells it actually touches — a
    // corner belongs to four cells, an edge to two, the centre to one — which is what makes the wash
    // fade smoothly across cell boundaries instead of drawing a visible grid of squares. Vanilla's
    // lighting overlay does the same averaging for the same reason — and walks a vertex lattice to
    // avoid paying for the overlap, which is what NightWashWindow now does here.
    //
    // The nine reads below are the whole reason the window exists: each cell in the section asks about
    // itself and its eight neighbours, so every interior cell's glow is read nine times over the
    // course of one regenerate.
    //
    // `in` rather than by value: this runs 289 times per regenerate and NightWashWindow is a readonly
    // struct, so passing by reference costs nothing and skips 289 copies of its header.
    private static void AddCellColors(LayerSubMesh subMesh, in NightWashWindow wash, int x, int z)
    {
        float here = wash.At(x, z);
        float west = wash.At(x - 1, z);
        float east = wash.At(x + 1, z);
        float south = wash.At(x, z - 1);
        float north = wash.At(x, z + 1);

        byte bottomLeft = ToAlpha((here + west + south + wash.At(x - 1, z - 1)) * 0.25f);
        byte topLeft = ToAlpha((here + west + north + wash.At(x - 1, z + 1)) * 0.25f);
        byte topRight = ToAlpha((here + east + north + wash.At(x + 1, z + 1)) * 0.25f);
        byte bottomRight = ToAlpha((here + east + south + wash.At(x + 1, z - 1)) * 0.25f);

        byte left = ToAlpha((here + west) * 0.5f);
        byte top = ToAlpha((here + north) * 0.5f);
        byte right = ToAlpha((here + east) * 0.5f);
        byte bottom = ToAlpha((here + south) * 0.5f);

        subMesh.colors.Add(WashColor(bottomLeft));
        subMesh.colors.Add(WashColor(left));
        subMesh.colors.Add(WashColor(topLeft));
        subMesh.colors.Add(WashColor(top));
        subMesh.colors.Add(WashColor(topRight));
        subMesh.colors.Add(WashColor(right));
        subMesh.colors.Add(WashColor(bottomRight));
        subMesh.colors.Add(WashColor(bottom));
        subMesh.colors.Add(WashColor(ToAlpha(here)));
    }

    // White RGB: the wash's colour comes from the material (a dark grey), so the vertex carries only
    // "how much of it applies here". The shader multiplies the two.
    private static Color32 WashColor(byte alpha) => new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, alpha);

    // Forwards to the pure core, which owns the exact rounding (see NightDesaturationMath.WashAlpha)
    // so the offline equivalence test compares shipped bytes rather than a transcription of them.
    private static byte ToAlpha(float wash) => NightDesaturationMath.WashAlpha(wash);
}
