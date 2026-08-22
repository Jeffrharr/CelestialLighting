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
    private static readonly List<Vector2> Uvs = new List<Vector2>();
    private static readonly List<int> Tris = new List<int>();

    // One material per opacity step. Quantised to 16 levels because these are faint, overlapping
    // shadows: the eye cannot tell 0.104 from 0.110, and an unquantised key would mint a material
    // per pawn per lamp per frame — SolidColorMaterials caches for the life of the process.
    private const int OpacitySteps = 16;

    private static readonly Dictionary<int, Material> MaterialCache = new Dictionary<int, Material>();

    // The feathered path's own bucket cache, kept SEPARATE from the flat one rather than keyed by a
    // flag: these are different shaders, and one dictionary holding both would hand an arm the other
    // arm's material the first time a bucket was reused — an A/B that measures nothing while every
    // flag reads as set, which is the same trap the mesh cache's taper key exists to avoid.
    private static readonly Dictionary<int, Material> FeatheredMaterialCache =
        new Dictionary<int, Material>();

    private const int RampTexels = 64;

    private static Texture2D RampTextureCache;

    // What tip opacity RampTextureCache was built for, so the harness's flat-ramp control arm gets a
    // rebuilt row instead of the shipped one.
    private static float RampTip = float.NaN;

    // One lamp's contribution to the pawn currently being drawn, carried from the first pass to the
    // second so the second does not recompute a distance and a coverage lookup it already paid for.
    private struct Contribution
    {
        public VectorLightField.LightEntry Entry;
        public float Illuminance;
        public float Distance;
        public float LightX;
        public float LightZ;
        public float UnitX;
        public float UnitZ;

        // The shadow's bearing FROM THE LAMP, in radians, in the visibility polygon's own
        // convention. Kept beside the unit vector rather than derived from it at the point of use
        // because the two have opposite sign conventions — the transform wants Unity's clockwise
        // degrees, the polygon wants atan2's anticlockwise radians — and deriving one from the other
        // at each site is how a shadow ends up clipped against the wall behind the lamp.
        public float Bearing;
    }

    // Reused rather than allocated per pawn: this runs for every on-screen pawn every frame, and a
    // fresh list each time is the kind of per-frame garbage §27's profiling budget has no room for.
    // Safe to share because DrawFor neither recurses nor outlives its own call.
    private static readonly List<Contribution> Contributions = new List<Contribution>();

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

    // Everything about one shadow a pawn casts from one lamp, resolved once.
    //
    // A struct carried between two consumers rather than a draw that computes as it goes, because
    // the PROBE has to see these numbers. The quantity issue #166 is about is a LENGTH, and a length
    // is no more visible to a screenshot than an alpha was: a shadow that stops at a wall and one
    // that crosses it differ only in pixels on the far side of the wall.
    public struct DrawnShadow
    {
        // How wide the tip is as a fraction of the base: 1 for the blocky shape vanilla's own
        // shadows use, and phase 4b's 0.32 when the shape flag is down.
        public float Taper;

        public float Opacity;
        public float Length;
        public float Half;
        public float TrailingEdge;
        public float AngleDegrees;
        public float UnitX;
        public float UnitZ;
    }

    private static readonly List<DrawnShadow> Shadows = new List<DrawnShadow>();

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

        Build(pawn, lights, skyGlow, Shadows);

        for (int i = 0; i < Shadows.Count; i++)
        {
            DrawnShadow drawn = Shadows[i];
            Mesh mesh = MeshFor(drawn.Half, drawn.Length, drawn.Taper);

            if (mesh == null)
                continue;

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
                new Vector3(
                    anchor.x + drawn.UnitX * drawn.TrailingEdge, altitude,
                    anchor.z + drawn.UnitZ * drawn.TrailingEdge),
                Quaternion.Euler(0f, drawn.AngleDegrees, 0f), MaterialForShadow(drawn.Opacity), 0);
        }
    }

    // Every shadow this pawn casts, geometry and opacity both. The one place either is decided.
    //
    // The draw and the probe call this same builder rather than each deriving its own answer, which
    // is the repo's probe convention and has earned its keep twice in this file already: phase 4b's
    // share and #166's clip are both quantities a screenshot reports only indirectly.
    private static void Build(
        Pawn pawn, Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights,
        float skyGlow, List<DrawnShadow> into)
    {
        into.Clear();

        Vector3 centre = pawn.DrawPos;
        ShadowData shadow = ShadowDataOf(pawn);

        float halfX = shadow == null
            ? VectorLightMath.DefaultPawnShadowHalfExtent : shadow.BaseX * 0.5f;
        float halfZ = shadow == null
            ? VectorLightMath.DefaultPawnShadowHalfExtent : shadow.BaseZ * 0.5f;

        // How TALL the caster is, taken from the same struct the two half-extents above come from.
        // `ShadowData.BaseY` is vanilla's own tallness for this def — the number its own shader
        // multiplies the sun-shadow extrusion by — and §27 was inventing 1.2 while reading BaseX and
        // BaseZ out of the very same object. A human declares 0.8, so this alone takes a third off a
        // colonist's lamp shadow, and an animal that declares a squatter shadow now gets a squatter
        // one instead of a human's.
        // With the shape flag down, the heights phase 4b shipped — an invented 1.2-cell caster and
        // a 2.4-cell lamp, whose ratio is exactly 1. Not a separate code path, just the other pair
        // of numbers through the same similar-triangles function, so the off arm reproduces the old
        // frame rather than approximating it.
        bool shaped = CelestialLightingFeatures.VectorLightShadowShape;

        float casterHeight = !shaped
            ? VectorLightMath.LegacyPawnHeight
            : shadow == null ? VectorLightMath.DefaultPawnHeight : shadow.BaseY;

        float lampHeight = shaped
            ? VectorLightMath.DefaultLampHeight : VectorLightMath.LegacyLampHeight;

        // The first pass: which lamps light this pawn, and how much in total — see Gather.
        float totalForShare = Gather(pawn, lights);

        for (int i = 0; i < Contributions.Count; i++)
        {
            Contribution light = Contributions[i];

            float opacity = VectorLightMath.PawnShadowOpacity(
                light.Illuminance, totalForShare, skyGlow);

            // Below a level of 255 the shadow is a rounding artefact rather than a shadow, and
            // drawing it costs the same as drawing a visible one. It rejects considerably more now
            // than it used to: dilution is exactly what pushes the fifth and sixth lamp's arms under
            // the threshold, so the busiest scenes are the ones that get cheaper.
            if (opacity * 255f < 1f)
                continue;

            float length = VectorLightMath.PawnShadowLength(
                light.Distance, casterHeight, lampHeight,
                shaped ? VectorLightMath.MaxPawnShadowLength : VectorLightMath.LegacyMaxShadowLength);

            // The caster's silhouette as this lamp sees it: how wide across, and how far out its
            // trailing edge is. Vanilla's shadow is the footprint rectangle PLUS a skirt extruded
            // from the edge facing away from the light, so both numbers come from the same rectangle
            // and both are direction-dependent — a human presents 0.15 half-cells to a lamp due east
            // and 0.20 to one due south.
            float half = Mathf.Max(
                VectorLightMath.FootprintExtent(halfX, halfZ, -light.UnitZ, light.UnitX),
                VectorLightMath.MinPawnShadowHalfWidth);

            float trailingEdge = VectorLightMath.FootprintExtent(
                halfX, halfZ, light.UnitX, light.UnitZ);

            // Stopped at the first thing that blocks the lamp (issue #166). The shadow runs directly
            // away from the lamp, so it lies along a radial of that lamp's visibility polygon and one
            // boundary query answers it — see VectorLightMath.ClipShadowLength. Asked in the
            // polygon's own angle convention, atan2(dz, dx) from the light, which is what the ray
            // builder fills the array with and what IsLit queries it with.
            if (CelestialLightingFeatures.VectorLightShadowClip)
                length = VectorLightMath.ClipShadowLength(
                    length, BoundaryFor(light), light.Distance, trailingEdge);

            // A shadow with no room left to fall into is not drawn at all: the pawn is standing flat
            // against the thing the lamp's light dies on.
            if (length <= 0f)
                continue;

            into.Add(new DrawnShadow
            {
                Taper = shaped ? 1f : VectorLightMath.LegacyTipTaper,
                Opacity = opacity,
                Length = length,
                Half = half,
                TrailingEdge = trailingEdge,
                UnitX = light.UnitX,
                UnitZ = light.UnitZ,
                AngleDegrees = VectorLightMath.PawnShadowAngleDegrees(
                    light.LightX, light.LightZ, centre.x, centre.z),
            });
        }
    }

    // Which lamps light this pawn and how much each contributes, left in Contributions, with the
    // denominator their shares are taken against returned.
    //
    // Split out of DrawFor so the PROBE can call it. That is the repo's probe convention and it
    // matters more than usual here: the quantity under test is an alpha, which no screenshot can
    // report directly, and a probe that recomputed the share from its own copy of the arithmetic
    // could agree with the intended physics while the renderer drew something else.
    private static float Gather(
        Pawn pawn, Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights)
    {
        Vector3 centre = pawn.DrawPos;

        // FIRST PASS: which lamps light this pawn, and how much light is on its cell in total.
        //
        // The total has to be in hand before ANY shadow is drawn, because each lamp's shadow is that
        // lamp's SHARE of it — see VectorLightMath.PawnShadowShare for why that is what a shadow is.
        // The requirement is the whole reason this is two passes rather than one: a single pass
        // cannot know how much light the lamps it has not reached yet are putting back into the
        // ground it is busy darkening, so it can only assume none, which is what left a pawn under
        // eight lamps standing in an opaque asterisk.
        Contributions.Clear();

        float totalIlluminance = 0f;

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

            float illuminance = VectorLightMath.PawnIlluminance(distance, entry.Radius, coverage);

            // A lamp delivering nothing here casts nothing here, and it must not reach the total
            // either — a lamp the pawn cannot see diluting the shadows of the lamps it can would be
            // the mirror of the bug being fixed.
            if (illuminance <= 0f)
                continue;

            totalIlluminance += illuminance;

            Contributions.Add(new Contribution
            {
                Entry = entry,
                Bearing = (float)System.Math.Atan2(dz, dx),
                Illuminance = illuminance,
                Distance = distance,
                LightX = lightX,
                LightZ = lightZ,

                // The shadow's bearing as a unit vector, resolved once so the two footprint
                // questions below and the push-out that follows all agree about which way "away from
                // the lamp" points. A pawn standing on the lamp's own cell has no bearing at all,
                // and +X is what PawnShadowAngleDegrees resolves that to — agreeing with it matters
                // more than the choice does.
                UnitX = distance > 0f ? dx / distance : 1f,
                UnitZ = distance > 0f ? dz / distance : 0f,
            });
        }

        // With the share model switched off the denominator stays at one — which is not a second
        // code path but literally the arithmetic that shipped: PawnShadowShare floors its divisor at
        // FullIlluminance and an illuminance never exceeds it, so the share collapses back to
        // falloff × coverage exactly. That is what makes the off arm a true pre-feature baseline
        // rather than a picture of the shadows being absent.
        float totalForShare = CelestialLightingFeatures.VectorLightShadowShares
            ? totalIlluminance
            : VectorLightMath.FullIlluminance;

        return totalForShare;
    }

    // How far this lamp's light reaches along the shadow's own bearing, in cells from the lamp.
    //
    // AN UNBUILT POLYGON MEANS "NO WALL KNOWN", NOT "A WALL AT ZERO", and the difference is the
    // whole feature working versus every shadow in the colony vanishing. `BoundaryDistanceAt`
    // answers 0 for an empty polygon, which through ClipShadowLength is a boundary closer than the
    // pawn and therefore no shadow at all — so a light whose polygon has not been rebaked yet (a
    // frame after a wall changed, or any path that reaches the draw before the mask has run) would
    // silently delete its shadows rather than draw them unclipped. Falling back to the light's own
    // radius degrades to phase 4b's behaviour for that one frame, which is the safe direction.
    //
    // Capped at the radius in the normal case too: the ray distances are stored unclamped, and a
    // boundary beyond the lamp's reach would let a shadow run out past the light that casts it.
    private static float BoundaryFor(Contribution light)
    {
        if (light.Entry.Polygon.Count == 0)
            return light.Entry.Radius;

        return Mathf.Min(
            VectorLightMath.BoundaryDistanceAt(light.Entry.Polygon, light.Bearing),
            light.Entry.Radius);
    }

    // Every shadow this pawn is about to have drawn, for the probe alone.
    //
    // Returns exactly what the renderer will draw because it runs the same builder — so the peak
    // arm, the composited rosette and the reach a scenario pins are facts about the frame rather
    // than about a model of it.
    public static void ShadowsFor(Map map, Pawn pawn, List<DrawnShadow> into)
    {
        into.Clear();

        if (map == null || pawn == null || !pawn.Spawned || !CastsShadow(pawn))
            return;

        Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights =
            VectorLightField.LightsFor(map);

        if (lights.Count == 0)
            return;

        Build(pawn, lights, map.skyManager.CurSkyGlow, into);
    }

    // Which of the two draw paths this shadow takes. The flag is read here rather than at the call
    // site so the probe and the draw cannot end up on different ones.
    private static Material MaterialForShadow(float opacity) =>
        CelestialLightingFeatures.VectorLightShadowFeather
            ? FeatheredMaterialFor(opacity)
            : MaterialFor(opacity);

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
            //
            // (The feathered path below DOES go through Map/Transparent, and gets away with it for
            // exactly the reason this one could not: it supplies a texture.)
            material = SolidColorMaterials.SimpleSolidColorMaterial(
                new Color(0f, 0f, 0f, (float)step / OpacitySteps));
            MaterialCache[step] = material;
        }

        return material;
    }

    // The same shadow, faded along its length by a ramp texture rather than held flat.
    //
    // WHY A TEXTURE AND NOT A PER-VERTEX ALPHA. The obvious cheap route is to grade the mesh's own
    // vertex colours, and it is closed twice over: `Map/SolidColor` ignores vertex colour outright,
    // and the material that reads it (`Custom/Sun shadow fade`) spends its alpha channel on the
    // extrusion. It is closed a third time on principle — a per-vertex *value* interpolates linearly
    // across a triangle, so a curve sampled that way is wrong by triangle length, which is precisely
    // how the shader-max attempt failed before it was fixed with a texture. A UV is a *position*, and
    // position across a triangle really is linear, so the curve stays exact wherever it is sampled.
    //
    // One texture for the whole map, not one per shadow: the ramp is a pure function of the fraction
    // along the shadow, so every shadow at every length and every opacity samples the same row. The
    // per-shadow opacity stays in the material colour, where it already was, and `Map/Transparent`
    // multiplies the two.
    private static Material FeatheredMaterialFor(float opacity)
    {
        // ASKED FIRST, ON EVERY DRAW, and deliberately not only when the material lookup misses.
        // The two caches invalidate each other — rebuilding the ramp clears the materials — so
        // reaching the ramp only through a material-cache miss makes the pair unreachable the moment
        // one material exists: the row is fixed for the rest of the session and nothing can ever
        // change it again.
        //
        // That is not hypothetical. The harness runs one step per frame, so a scenario arm that sets
        // two flags renders a frame between them; the control arm built its material in that
        // one-frame window, with the first flag applied and the second not, and then held the wrong
        // ramp for the whole arm. Every derived number said the arm had changed and the pixels never
        // did. When the ramp has not moved this costs one float comparison, which is not worth
        // trading a whole class of invisible staleness for.
        Texture2D ramp = RampTexture();

        int step = Mathf.Clamp(Mathf.RoundToInt(opacity * OpacitySteps), 1, OpacitySteps);

        if (!FeatheredMaterialCache.TryGetValue(step, out Material material))
        {
            // THE RENDER QUEUE IS COPIED FROM THE FLAT MATERIAL, DELIBERATELY. Turning this feature
            // on swaps the shader, and a shader at a different queue composites against the lighting
            // overlay at a different moment — which shows up as the whole shadow changing darkness
            // rather than as a gradient appearing, and reads as a wrong formula rather than an
            // ordering bug. Pinning the queue to the one the flat path already used makes the swap
            // compositionally neutral, so the A/B measures the curve and nothing else. The scenario
            // keeps a flat-ramp arm through THIS material anyway, because a comment is not a control.
            material = MaterialPool.MatFrom(new MaterialRequest
            {
                shader = ShaderDatabase.Transparent,
                mainTex = ramp,
                color = new Color(0f, 0f, 0f, (float)step / OpacitySteps),
                colorTwo = Color.white,
                renderQueue = MaterialFor(opacity).renderQueue,
                needsMainTex = true,
            });

            FeatheredMaterialCache[step] = material;
        }

        return material;
    }

    // One texel row, alpha falling from full at the caster to nothing at the tip.
    //
    // 64 texels because the ramp is sampled bilinearly across a shadow that is rarely more than a
    // cell or two on screen — at the zoom these are looked at, a cell is about 50 px, so 64 texels
    // over the whole length is already finer than the pixels it lands on. Rebuilt when the curve
    // changes, which in a shipped game is never: the harness's flat-ramp control arm is the only
    // thing that moves it.
    private static Texture2D RampTexture()
    {
        float frontLoad = VectorLightMath.PawnShadowFadeFrontLoad;

        // KEYED ON THE CURVE'S OWN MIDPOINT, NOT ON THE CONSTANT BEHIND IT. Reading the constant
        // would make this cache blind to anything that changes the curve without changing the
        // constant — which is exactly what the harness's flat-ramp control arm does, by postfixing
        // PawnShadowFade. Keyed this way, flipping that arm invalidates the row on the next draw
        // instead of leaving the previous arm's gradient on screen, which is the failure an in-run
        // A/B would otherwise photograph as "the flag did nothing".
        //
        // The MIDPOINT rather than the endpoint, which the endpoint version of this got wrong: the
        // curve now ends at exactly 0 by construction, so its tip is 0 for every front-load value
        // and would key every distinct curve to the same cache entry. Halfway along, it separates
        // them — and it still separates the flat control arm, which reads 1 there.
        float key = VectorLightMath.PawnShadowFade(0.5f, frontLoad);

        if (RampTextureCache != null && RampTip == key)
            return RampTextureCache;

        Texture2D ramp = new Texture2D(RampTexels, 1, TextureFormat.ARGB32, false)
        {
            name = "CelestialLighting_PawnShadowRamp",

            // Clamp, not repeat: a bilinear sample at the very tip would otherwise blend the far end
            // of the ramp with its own full-opacity start and put a bright seam on the last texel.
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        for (int i = 0; i < RampTexels; i++)
        {
            // Sampled at the texel CENTRE, which is what a bilinear fetch of u = i/(n-1) actually
            // returns, so the row's two ends really are the ramp's two endpoints.
            float along = RampTexels == 1 ? 0f : (float)i / (RampTexels - 1);

            // RGB stays black: the shadow's colour is the material's, and Map/Transparent multiplies
            // texel by _Color, so anything else here would tint the shadow.
            ramp.SetPixel(
                i, 0, new Color(0f, 0f, 0f, VectorLightMath.PawnShadowFade(along, frontLoad)));
        }

        ramp.Apply();

        RampTextureCache = ramp;
        RampTip = key;

        // EVERY CACHED MATERIAL IS NOW STALE, because a material binds a texture OBJECT and this has
        // just made a new one. Without this line the first arm to build a material pins its ramp for
        // the rest of the session: a later arm rebuilds the row, every recomputed number agrees the
        // ramp changed, and the screen goes on sampling the old texture. That failure is close to
        // invisible — the probes move, the frames do not — and it cost a live run here before the
        // probe was changed to read the bound texture rather than a freshly derived value.
        //
        // Clearing rather than re-binding in place because the bucket set is a handful of entries
        // rebuilt on the next draw, and a rebuild is cheaper to reason about than a mutation.
        FeatheredMaterialCache.Clear();

        return ramp;
    }

    // The alpha of the ramp row THE DRAW IS ACTUALLY BOUND TO, at `along` of the way to the tip.
    //
    // For the probe, and deliberately read off a live material's own texture rather than recomputed
    // from the formula — or even off the current cached row. This is the number the screen samples,
    // so a scenario pinning it pins a fact about the frame. A probe that re-derived it agreed with
    // the formula while the screen sampled a stale row, which is exactly the disagreement that hid
    // the material-cache bug above: the arm's numbers all moved and its pixels did not.
    //
    // TAKES A POSITION because the tip alone stopped being enough to identify the curve once the
    // curve started ending at zero. A scenario pinning only the endpoint would pass against any
    // shape that happens to vanish, including a broken one; pinning the endpoint AND the midpoint
    // says the row reaching the GPU has both the right ends and the right bend.
    //
    // Falls back to building the row when nothing has drawn yet, so a probe read before the first
    // frame reports the ramp that is about to be used rather than a sentinel.
    public static float BoundRampAlphaAt(float along)
    {
        int texel = Mathf.Clamp(
            Mathf.RoundToInt(along * (RampTexels - 1)), 0, RampTexels - 1);

        foreach (Material material in FeatheredMaterialCache.Values)
        {
            if (material != null && material.mainTexture is Texture2D bound)
                return bound.GetPixel(texel, 0).a;
        }

        Texture2D ramp = RampTexture();

        return ramp == null ? 1f : ramp.GetPixel(texel, 0).a;
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
    private static Mesh MeshFor(float half, float length, float taper)
    {
        // The taper joins the key because the shape flag can change it mid-session, and a cache that
        // ignored it would hand the new arm the old arm's mesh — an A/B that measures nothing while
        // every flag reads as set.
        long key = ((long)Mathf.RoundToInt(half * 32f) << 32)
            | ((uint)Mathf.RoundToInt(length * 4f) << 1)
            | (uint)(taper >= 1f ? 1 : 0);

        if (MeshCache.TryGetValue(key, out Mesh cached))
            return cached;

        Verts.Clear();
        Colors.Clear();
        Tris.Clear();

        // CONSTANT WIDTH, which reverses an earlier decision in this same file rather than merely
        // differing from it. The taper (a tip at 32% of the base) was added because the first
        // capture, at full width, "read as a plank" — and it did, because the shadows were up to six
        // cells long and nothing that long is shaped like a pawn.
        //
        // The premise went away when the geometry got shorter. Vanilla's own sun shadow is the
        // footprint rectangle plus a skirt of CONSTANT width extruded from the trailing edge
        // (`MeshMakerShadows.NewShadowMesh` duplicates each edge's two vertices and lets the shader
        // push them along _CastVect) — there is no taper anywhere in it. So the blocky shape is not
        // a compromise here, it is the shape the game's other shadows already are, and matching it
        // is what makes a lamp shadow and a sun shadow on the same pawn look like two shadows rather
        // than two effects.
        float tipHalf = half * taper;

        Verts.Add(new Vector3(0f, 0f, -half));
        Verts.Add(new Vector3(0f, 0f, half));
        Verts.Add(new Vector3(length, 0f, tipHalf));
        Verts.Add(new Vector3(length, 0f, -tipHalf));

        // U runs 0 at the trailing edge to 1 at the tip, which is the fraction-along the fade ramp
        // is a function of. V is constant because the ramp has no cross-shadow variation — vanilla's
        // skirt is uniform across its width and only dissolves along its length.
        //
        // Baked unconditionally, including for the flat path: SolidColor ignores UVs, so they cost
        // four Vector2s in a cache that saturates at a couple of dozen meshes and save the mesh cache
        // from needing the feather flag in its key. Length is already in the key, so a shadow that
        // changes length gets a fresh mesh and the ramp restretches with it — which is what makes the
        // fade a fraction of each shadow's own length rather than a fixed distance in cells.
        Uvs.Clear();
        Uvs.Add(new Vector2(0f, 0f));
        Uvs.Add(new Vector2(0f, 1f));
        Uvs.Add(new Vector2(1f, 1f));
        Uvs.Add(new Vector2(1f, 0f));

        // WHITE, WHICH REVERSES THIS LINE'S ORIGINAL VALUE AND IS THE REASON THE FEATHERED PATH
        // DREW NOTHING THE FIRST TIME IT RAN. These were (0,0,0,0), chosen back when the plan was to
        // draw through the game's own shadow material, where vertex alpha IS the extrusion distance
        // and zero means "leave this vertex where it is". Nothing has drawn through that material for
        // a long time; `Map/SolidColor` ignores vertex colour entirely, so the zeros were inert and
        // stayed put looking deliberate.
        //
        // `Map/Transparent` does NOT ignore them — it multiplies the texel by the vertex colour, the
        // way every map-layer mesh in the game relies on (Printer_Plane's DefaultColors are white for
        // exactly this reason). Against a black, alpha-zero vertex colour that product is zero
        // everywhere, so the shadow rendered as a perfect nothing: no error, no warning, every probe
        // green, and a frame identical to the one with the feature switched off.
        //
        // White is inert for the flat path (SolidColor still ignores it) and correct for the
        // feathered one, so the two can go on sharing one cached mesh.
        for (int i = 0; i < 4; i++)
            Colors.Add(new Color32(255, 255, 255, 255));

        Tris.Add(0);
        Tris.Add(1);
        Tris.Add(2);
        Tris.Add(0);
        Tris.Add(2);
        Tris.Add(3);

        Mesh mesh = new Mesh { name = "CelestialLighting_PawnShadow" };
        mesh.SetVertices(Verts);
        mesh.SetColors(Colors);
        mesh.SetUVs(0, Uvs);
        mesh.SetTriangles(Tris, 0);

        MeshCache[key] = mesh;
        return mesh;
    }
}
