using System.Collections.Generic;
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

            DrawFor(map, pawn, lights, skyGlow, altitude);
        }
    }

    private static void DrawFor(
        Map map, Pawn pawn, Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights,
        float skyGlow, float altitude)
    {
        Vector3 centre = pawn.DrawPos;
        float footprint = FootprintOf(pawn);

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

            Mesh mesh = MeshFor(footprint, length);

            if (mesh == null)
                continue;

            float angle = VectorLightMath.PawnShadowAngleDegrees(lightX, lightZ, centre.x, centre.z);

            // A material per opacity step rather than a property block. Graphics.DrawMesh is
            // deferred, so writing one shared material's colour between calls gives every shadow in
            // the frame whichever opacity was written last — the trap VectorLightOverlay's header
            // records §17 paying for. Distinct materials sidestep it without a property block.
            Graphics.DrawMesh(
                mesh, new Vector3(centre.x, altitude, centre.z), Quaternion.Euler(0f, angle, 0f),
                MaterialFor(opacity), 0);
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

    // The caster's footprint, from its own shadow data where it has some. A pawn without shadow data
    // still casts one here — vanilla's absence of a blob is a decision about SUNlight, and a torch a
    // cell away should still throw something.
    private static float FootprintOf(Pawn pawn)
    {
        ShadowData shadow = pawn.def?.graphicData?.shadowData;

        return shadow == null ? 0.6f : Mathf.Max(shadow.BaseX, 0.35f);
    }

    // The footprint extruded along +X, at alpha zero throughout so the shader leaves it where it is
    // put. Direction comes from the transform; only the LENGTH is baked, which is what makes a
    // couple of dozen cached meshes cover a whole colony.
    private static Mesh MeshFor(float footprint, float length)
    {
        long key = ((long)Mathf.RoundToInt(footprint * 4f) << 32) | (uint)Mathf.RoundToInt(length * 4f);

        if (MeshCache.TryGetValue(key, out Mesh cached))
            return cached;

        float half = footprint * 0.5f;

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
