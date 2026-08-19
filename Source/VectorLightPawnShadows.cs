using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §27 phase 4: pawns cast shadows away from the lamps that light them.
//
// WHAT VANILLA DOES, AND WHY IT CANNOT DO THIS. A pawn's shadow is one mesh from ShadowMeshPool
// drawn at a fixed per-def offset with MatBases.SunShadowFade. Its direction and length come from
// `_CastVect`, a **global** the sky manager sets once a frame — so every shadow on the map leans the
// same way, which is right for the sun and useless for a torch. Per-lamp direction is not reachable
// through that material at all, and it is not reachable by setting the global between draws either:
// Graphics.DrawMesh is DEFERRED, so a global written between calls applies to whichever call resolves
// last. VectorLightOverlay's header records the same trap costing §17 a branch.
//
// SO THE EXTRUSION IS BAKED HERE INSTEAD. The mesh is the caster's footprint extruded along +X by
// the shadow's length, pointed with the transform — no per-frame mesh rebuild, and the meshes are
// cached per (footprint, length) bucket the way VectorLightOverlay caches a gradient per radius.
//
// AND IT IS NOT DRAWN THROUGH MatBases.SunShadowFade, which was the first attempt and rendered a
// small OPAQUE BLACK BOX rather than a long faint shadow. Two things were wrong with borrowing that
// material and both are inherent to it: its vertex ALPHA is the extrusion channel (`vertexAlpha *
// _CastVect`), so the mesh's own colours are not free for us to use, and its opacity comes from the
// material colour that SkyManager lerps for the whole map rather than from anything a per-draw
// property block can reach. A solid-colour transparent material has neither problem: no _CastVect,
// and alpha that means alpha. It costs one cached material per opacity bucket, which is the same
// trade SolidColorMaterials exists to make.
//
// WHY THE MASK IS WHAT MAKES IT CORRECT. A pawn behind a wall must not throw a shadow from a lamp
// that cannot see it. §27 phase 3 already bakes, per emitter, the share of every cell that emitter
// reaches — so the question costs one array lookup. Nothing else in the mod can answer it: the
// crossfade knows only a global constant, and vanilla's glow grid would say yes, because its light
// bends around corners.
//
// ROOFS AND EAVES ARE DELIBERATELY NOT SKIPPED. Vanilla's Graphic_Shadow bails on any roofed cell,
// which is correct for sunlight and exactly backwards here — indoors under a lamp is the whole point
// of the feature, and §15's eaves are likewise a sun concept with no bearing on a torch. This is the
// one place §27 knowingly renders a shadow where vanilla renders none.
[StaticConstructorOnStartup]
public static class VectorLightPawnShadows
{
    // Cached per (footprint, length) bucket, quarter-cell keys, on the same reasoning and in the
    // same shape as VectorLightOverlay's gradient cache: a colony has a handful of distinct pawn
    // sizes and the length quantises to a few dozen values, so the cache saturates almost at once.
    private static readonly Dictionary<long, Mesh> MeshCache = new Dictionary<long, Mesh>();

    private static readonly List<Vector3> Verts = new List<Vector3>();
    private static readonly List<Color32> Colors = new List<Color32>();
    private static readonly List<int> Tris = new List<int>();

    // One material per opacity step. Quantised to 16 levels because these are faint, overlapping
    // shadows: the eye cannot tell 0.104 from 0.110, and an unquantised key would mint a material
    // per pawn per lamp per frame — SolidColorMaterials caches for the life of the process.
    private const int OpacitySteps = 16;

    private static readonly Dictionary<int, Material> MaterialCache = new Dictionary<int, Material>();

    public static void Draw(Map map)
    {
        if (!CelestialLightingFeatures.VectorLightPawnShadows || map == null)
            return;

        // Nothing to cast from if §27 itself is off, and nothing trustworthy to ask about occlusion
        // if the mask is not the one composing — see the header.
        if (!CelestialLightingFeatures.VectorLights || !VectorLightMask.Active)
            return;

        IReadOnlyList<Pawn> pawns = map.mapPawns?.AllPawnsSpawned;

        if (pawns == null || pawns.Count == 0)
            return;

        Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights =
            VectorLightField.LightsFor(map);

        if (lights.Count == 0)
            return;

        CellRect view = Find.CameraDriver.CurrentViewRect;
        float skyGlow = map.skyManager.CurSkyGlow;
        float altitude = AltitudeLayer.Shadows.AltitudeFor();

        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];

            // Culled against the camera first, for the same reason VectorLightOverlay culls: a
            // colony's pawns are mostly off screen and this runs every frame.
            if (pawn == null || !pawn.Spawned || !view.Contains(pawn.Position))
                continue;

            // Then the states vanilla refuses to draw a shadow in, which are not about sunlight and
            // so are not ours to diverge from — see VectorLightMath.PawnCastsShadow. Asked after the
            // camera cull because it is the more expensive of the two (IsPsychologicallyInvisible
            // walks the hediff set) and the cull rejects most of a colony.
            if (!CastsShadow(pawn))
                continue;

            DrawFor(map, pawn, lights, skyGlow, altitude);
        }
    }

    private static void DrawFor(
        Map map, Pawn pawn, Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights,
        float skyGlow, float altitude)
    {
        Vector3 centre = pawn.DrawPos;
        ShadowData shadow = ShadowDataOf(pawn);

        // Where the caster's footprint actually sits, which is NOT where the pawn is drawn. Vanilla
        // offsets it by ShadowData.offset — (0, 0, -0.3) for a human, i.e. at the feet — and both
        // Graphic_Shadow.DrawWorker and Printer_Shadow.PrintShadow honour that. §27 anchored on
        // DrawPos instead, so a colonist's lamp shadow left their torso while their sun shadow left
        // their feet, 0.3 cells apart and both on screen at dusk (issue #159). The offset is applied
        // unrotated for the same reason vanilla's dynamic path applies it unrotated: PawnRenderer
        // draws pawn shadows as Rot4.North regardless of which way the pawn faces.
        Vector3 anchor = shadow == null ? centre : centre + shadow.offset;

        float halfX = shadow == null
            ? VectorLightMath.DefaultPawnShadowHalfExtent : shadow.BaseX * 0.5f;
        float halfZ = shadow == null
            ? VectorLightMath.DefaultPawnShadowHalfExtent : shadow.BaseZ * 0.5f;

        foreach (VectorLightField.LightEntry entry in lights)
        {
            float lightX = entry.Cell.x + 0.5f;
            float lightZ = entry.Cell.z + 0.5f;
            float dx = centre.x - lightX;
            float dz = centre.z - lightZ;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);

            if (distance > entry.Radius)
                continue;

            // The occlusion question, answered by phase 3's baked grid in one lookup. Asked before
            // any geometry is built, because a pawn the lamp cannot see is the common case in a
            // built-up colony and everything below it is wasted on one.
            float coverage = VectorLightMath.CoverageAt(
                entry.Coverage, entry.Cell.x, entry.Cell.z, entry.CoverageRadius,
                pawn.Position.x, pawn.Position.z) / 255f;

            float opacity = VectorLightMath.PawnShadowOpacity(distance, entry.Radius, coverage, skyGlow);

            // Below a level of 255 the shadow is a rounding artefact rather than a shadow, and
            // drawing it costs the same as drawing a visible one.
            if (opacity * 255f < 1f)
                continue;

            float length = VectorLightMath.PawnShadowLength(
                distance, VectorLightMath.DefaultPawnHeight, VectorLightMath.DefaultLampHeight);

            // The shadow's bearing as a unit vector, resolved once so the two footprint questions
            // below and the push-out that follows all agree about which way "away from the lamp"
            // points. A pawn standing on the lamp's own cell has no bearing at all, and +X is what
            // PawnShadowAngleDegrees resolves that to — agreeing with it matters more than the
            // choice does.
            float ux = distance > 0f ? dx / distance : 1f;
            float uz = distance > 0f ? dz / distance : 0f;

            // The caster's silhouette as this lamp sees it: how wide across, and how far out its
            // trailing edge is. Vanilla's shadow is the footprint rectangle PLUS a skirt extruded
            // from the edge facing away from the light, so both numbers come from the same rectangle
            // and both are direction-dependent — a human presents 0.15 half-cells to a lamp due east
            // and 0.20 to one due south.
            float half = Mathf.Max(
                VectorLightMath.FootprintExtent(halfX, halfZ, -uz, ux),
                VectorLightMath.MinPawnShadowHalfWidth);

            float trailingEdge = VectorLightMath.FootprintExtent(halfX, halfZ, ux, uz);

            Mesh mesh = MeshFor(half, length);

            if (mesh == null)
                continue;

            float angle = VectorLightMath.PawnShadowAngleDegrees(lightX, lightZ, centre.x, centre.z);

            // A material per opacity step rather than a property block. Graphics.DrawMesh is
            // deferred, so writing one shared material's colour between calls gives every shadow in
            // the frame whichever opacity was written last — the trap VectorLightOverlay's header
            // records §17 paying for. Distinct materials sidestep it without a property block.
            // Started at the silhouette's trailing edge rather than at its centre, so the length
            // computed above is length BEYOND the caster — the same thing it means for a sun shadow,
            // whose skirt is extruded from that edge too. Pushing the transform rather than baking
            // the offset into the mesh keeps the cache keyed on two numbers instead of three.
            Graphics.DrawMesh(
                mesh,
                new Vector3(anchor.x + ux * trailingEdge, altitude, anchor.z + uz * trailingEdge),
                Quaternion.Euler(0f, angle, 0f), MaterialFor(opacity), 0);
        }
    }

    private static Material MaterialFor(float opacity)
    {
        int step = Mathf.Clamp(Mathf.RoundToInt(opacity * OpacitySteps), 1, OpacitySteps);

        if (!MaterialCache.TryGetValue(step, out Material material))
        {
            // SimpleSolidColorMaterial rather than a hand-rolled one over ShaderDatabase
            // .Transparent: that shader samples _MainTex, and a material with no texture drew
            // NOTHING AT ALL — a silent nothing, which after the opaque black box is the second way
            // this has failed to look like a shadow. SolidColor is the shader meant for a coloured
            // quad with no texture, and SolidColorMaterials caches by colour for us.
            material = SolidColorMaterials.SimpleSolidColorMaterial(
                new Color(0f, 0f, 0f, (float)step / OpacitySteps));
            MaterialCache[step] = material;
        }

        return material;
    }

    // The caster's own shadow data, read where vanilla reads it — which is two places, not one.
    //
    // PawnRenderer.DrawShadowInternal consults `race.specialShadowData` and the body graphic's
    // `graphicData.shadowData`, and HUMANLIKES ONLY HAVE THE FIRST: Races_Humanlike.xml declares
    // specialShadowData (volume 0.3, 0.8, 0.4 and offset 0, 0, -0.3) and no graphicData.shadowData
    // at all. §27 read only the second, so this returned null for every colonist in the game and
    // they all fell through to a hardcoded 0.6-wide square against a real width of 0.3 — twice the
    // width of the sun shadow standing beside it, and with no offset (issue #159).
    //
    // Animals were unaffected, which is exactly why it survived being looked at: they declare theirs
    // inside graphicData, so they were reading the right rectangle all along.
    // The four live reads behind VectorLightMath.PawnCastsShadow, in one place. Public for the same
    // reason ShadowDataOf is: the probe has to ask the function the renderer asks, or it can report
    // a pawn as suppressed while the screen still draws them.
    //
    // Each read is the one vanilla itself uses, deliberately rather than a near-equivalent:
    // GetPosture() is what PawnRenderer gates on, IsPsychologicallyInvisible() is what sets the
    // PawnRenderFlags.Invisible it gates on alongside, and Swimming /
    // DrawNonHumanlikeSwimmingGraphic / Flying are the three DrawShadowInternal itself branches on.
    // Reaching for something that merely correlates — Downed instead of posture, say — would drift
    // from vanilla the moment Ludeon changed one of them.
    public static bool CastsShadow(Pawn pawn)
    {
        if (pawn?.def == null)
            return false;

        return VectorLightMath.PawnCastsShadow(
            standing:  pawn.GetPosture() == PawnPosture.Standing,
            invisible: pawn.IsPsychologicallyInvisible(),
            swimming:  pawn.Swimming || pawn.DrawNonHumanlikeSwimmingGraphic,
            flying:    pawn.Flying);
    }

    // Public because the probe asks THIS function rather than re-deriving the answer, which is the
    // repo's probe convention (see EaveCellProbe): a probe that recomputes can agree with a formula
    // the screen is not using, and this is precisely a bug about the screen using a different
    // rectangle from the one anyone expected.
    public static ShadowData ShadowDataOf(Pawn pawn) =>
        pawn.def?.race?.specialShadowData ?? pawn.def?.graphicData?.shadowData;

    // The silhouette extruded along +X, at alpha zero throughout so the shader leaves it where it is
    // put. Direction and the push out to the trailing edge both come from the transform; only the
    // half-width and the LENGTH are baked, which is what makes a couple of dozen cached meshes cover
    // a whole colony.
    //
    // The half-width is bucketed at a THIRTY-SECOND of a cell where the length is bucketed at a
    // quarter, and the asymmetry is the point: these widths are sub-cell (a human's silhouette runs
    // 0.15 to 0.20) so quarter-cell buckets would round every one of them to the same 0.25 and throw
    // away the direction-dependence this is here to express, while a length of 3.1 versus 3.25 cells
    // is invisible. A human sweeps two buckets over a full circuit of the lamp.
    private static Mesh MeshFor(float half, float length)
    {
        long key = ((long)Mathf.RoundToInt(half * 32f) << 32) | (uint)Mathf.RoundToInt(length * 4f);

        if (MeshCache.TryGetValue(key, out Mesh cached))
            return cached;

        Verts.Clear();
        Colors.Clear();
        Tris.Clear();

        // Narrowed hard towards the tip. The first capture used 0.75 and read as a plank: with no
        // way to fade the edge — the material spends vertex alpha on extrusion, so a gradient there
        // would move the geometry rather than dim it — the silhouette is doing all the work, and a
        // shape that tapers reads as cast while a shape that does not reads as an object.
        //
        // Not to a point, though. A triangle reads as a cone pointing away from the pawn, which is
        // the shape of a spotlight rather than of a shadow.
        float tipHalf = half * 0.32f;

        Verts.Add(new Vector3(0f, 0f, -half));
        Verts.Add(new Vector3(0f, 0f, half));
        Verts.Add(new Vector3(length, 0f, tipHalf));
        Verts.Add(new Vector3(length, 0f, -tipHalf));

        for (int i = 0; i < 4; i++)
            Colors.Add(new Color32(0, 0, 0, 0));

        Tris.Add(0);
        Tris.Add(1);
        Tris.Add(2);
        Tris.Add(0);
        Tris.Add(2);
        Tris.Add(3);

        Mesh mesh = new Mesh { name = "CelestialLighting_PawnShadow" };
        mesh.SetVertices(Verts);
        mesh.SetColors(Colors);
        mesh.SetTriangles(Tris, 0);

        MeshCache[key] = mesh;
        return mesh;
    }
}
