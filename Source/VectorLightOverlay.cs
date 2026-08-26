using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace CelestialLighting;

// §27's draw: one additive fan per emitter, over the visible map, once per frame.
//
// ADDITIVE, WHICH IS THE WHOLE POINT. SkyColorSet.sky is a MULTIPLY into MatBases.LightOverlay.color
// whose brightest palette is already (1, 1, 1), so a multiplicative lane has no value above "do not
// darken" and cannot put light back where §27 has just taken vanilla's away. MoteGlow adds. Same
// shader, same altitude and same argument as §11a's aurora, §23b's cloud underlight and §24's snow
// glare — see epic #103, of which this is a bounded instance.
//
// BRIGHTNESS TRAVELS AS A TEXTURE, NOT A VERTEX COLOUR. CloudUnderlightOverlay's header records the
// finding: nothing here has ever asked MoteGlow to honour a vertex colour, while the aurora puts real
// structure through it as a texture and works. So the falloff curve is baked into a 1-D gradient in
// the ALPHA channel — which is where AuroraCurtain writes its own intensity — and the mesh carries
// only a radial texture coordinate.
//
// [StaticConstructorOnStartup] is mandatory rather than tidy, same as AuroraCurtainOverlay and
// SnowGlareOverlay: `new Material(...)` and `new Texture2D(...)` have to happen on Unity's main
// thread, and the attribute is what guarantees the static initialiser runs there at startup, after
// ShaderDatabase has loaded, rather than on whichever thread first touches the type.
[StaticConstructorOnStartup]
public static class VectorLightOverlay
{
    // One material per distinct radius, because the gradient is per radius — the falloff's
    // inverse-square term is 1/(u*radius)^2, so a campfire and a sun lamp genuinely cannot share one.
    // Keyed on quarter-cells so a mod handing out radius 11.5 gets its own entry without letting
    // float noise fill the cache.
    private static readonly Dictionary<int, Material> MaterialsByRadius = new Dictionary<int, Material>();

    // The same gradients again, bound to §27 phase 6's custom shader instead of MoteGlow. Two caches
    // rather than one keyed on the mode, because the mode is a per-frame answer — the shader can be
    // unavailable, and the flag can be flipped by the harness mid-run — and a light must not have to
    // throw away its material to change composition.
    private static readonly Dictionary<int, Material> MaxMaterialsByRadius = new Dictionary<int, Material>();

    // Gradients are keyed on radius alone and shared between the two material caches. A 256x32
    // texture per distinct radius is the expensive half of MaterialFor, and it is identical either
    // way: the composition changes what the fragment program does with the curve, never the curve.
    private static readonly Dictionary<int, Texture2D> GradientsByRadius = new Dictionary<int, Texture2D>();

    // The surface lift's materials. Separate from MaxMaterialsByRadius because the surface lift IS a
    // material property — blend state cannot be set per draw — so the two compositions cannot share
    // one material however identical everything else about them is. Same gradient, same program,
    // render queue; one blend factor apart.
    private static readonly Dictionary<int, Material> LiftMaterialsByRadius = new Dictionary<int, Material>();

    // Vanilla's delivered glow per vertex, in UV1. Vector4 rather than Vector3 because Unity's mesh
    // API takes UV channels as float4 and a float3 overload would silently pad anyway.
    private static readonly List<Vector4> VanillaUvs = new List<Vector4>();

    private static readonly List<Vector3> Verts = new List<Vector3>();
    private static readonly List<Vector2> Uvs = new List<Vector2>();
    private static readonly List<int> Tris = new List<int>();

    public static void Draw(Map map)
    {
        if (!CelestialLightingFeatures.VectorLights || map == null)
            return;

        // §27 phase 3 expresses the shadow by subtracting from vanilla's own lighting rather than by
        // drawing over it, so by default this pass stands down — together at full strength they would
        // carve the shadow once and then light it again from above. The beam flag is the deliberate
        // exception: it keeps this pass running at a reduced strength so the lit region gains the
        // contrast the mask alone cannot produce, over a vanilla that has already had the bent light
        // removed. See CelestialLightingFeatures.VectorLightMaskBeam.
        // Phase 5's per-cell lift and this pass are two deliveries of the SAME quantity — the excess
        // of our model over what vanilla delivered — at two different resolutions. Running both
        // would light the region twice. Phase 6 wins when it can actually draw, because polygon
        // resolution is the whole reason it exists; phase 5 wins otherwise, so a machine whose
        // shader failed to compile still gets the right level at the coarser resolution rather than
        // nothing. See CelestialLightingFeatures.VectorLightShaderMax.
        if (VectorLightMask.Lifting && !VectorLightShader.MaxActive)
            return;

        // The beam flag governs the FLAT lift only. Phase 6 is this same pass carrying a different
        // fragment program, so gating it on the beam flag stands the shader down whenever the flat
        // beam happens to be off — which is exactly how a phase 6 arm is configured, and it
        // photographs as the feature having no effect rather than as a misconfiguration.
        if (VectorLightMask.Active && !CelestialLightingFeatures.VectorLightMaskBeam
            && !VectorLightShader.MaxActive)
            return;

        Dictionary<object, VectorLightField.LightEntry>.ValueCollection lights =
            VectorLightField.LightsFor(map);

        if (lights.Count == 0)
            return;

        CellRect view = Find.CameraDriver.CurrentViewRect;
        float skyGlow = map.skyManager.CurSkyGlow;
        float altitude = AltitudeLayer.VisEffects.AltitudeFor();

        foreach (VectorLightField.LightEntry entry in lights)
            DrawLight(map, entry, view, skyGlow, altitude);
    }

    private static void DrawLight(
        Map map, VectorLightField.LightEntry entry, CellRect view, float skyGlow, float altitude)
    {
        // THE CAMERA CULL COMES FIRST, which is what Overlaps' own header has always claimed and
        // what this method did not do. Everything below is proportional to what is on screen; the
        // cull is the thing that makes that true, so anything asked before it is asked about every
        // emitter on the map, every frame, including the overwhelming majority that are nowhere near
        // the camera. StrengthFor is a roof-grid lookup and a daylight curve — small, and small
        // multiplied by a built-up colony's lamp count is the shape of cost this subsystem is
        // supposed to avoid. Both tests are pure, so the order between them changes nothing except
        // who pays.
        if (!Overlaps(view, entry))
            return;

        // TWO ANSWERS, NOT ONE, and keeping them apart is what makes the control arm possible.
        // `maxDrawing` is which material the pass goes through — asked for AND possible, since a
        // machine that cannot run the shader has to compose the old way instead. `maxComposing` is
        // whether that shader will actually subtract anything, and a shader subtracting nothing must
        // be treated as the plain additive pass or the control arm measures a different thing.
        bool maxDrawing = VectorLightShader.MaxActive;
        bool maxComposing = maxDrawing && CelestialLightingFeatures.VectorLightShaderMaxSubtract;

        // The surface lift. Only meaningful under the max — the fallback path draws through MoteGlow, whose
        // blend we do not own — and it changes both which material this draw goes through and how
        // the strength scalar is calibrated, so the two have to be decided from one answer.
        bool surfaceLift = maxComposing && VectorLightShader.SurfaceLiftActive;

        float strength = StrengthFor(map, entry, skyGlow, maxComposing, surfaceLift);

        if (strength <= 0f)
            return;

        if (entry.GeometryDirty || entry.Mesh == null)
            Rebuild(map, entry, altitude);

        if (entry.Mesh == null)
            return;

        // Only under the max, and only when something moved. Off-screen lights never get here at
        // all — the cull above is what keeps this proportional to what is being looked at — so a
        // colony switching a lamp pays for resampling the handful of lights currently in view.
        if (maxDrawing && entry.SampleDirty)
            UploadVanillaField(map, entry);

        // Per-draw colour goes through a MaterialPropertyBlock and not Material.color. Graphics.DrawMesh
        // is DEFERRED — the draws are queued and resolved later — so writing the material's colour
        // between calls gives every light in the frame whichever colour was written last. §17's branch
        // paid for learning this.
        Color color = entry.Color;
        entry.Props.SetColor(ShaderPropertyIDs.Color, new Color(color.r, color.g, color.b, strength));

        // Set on the same property block and for the same deferred-draw reason as the colour. Zero
        // is the control arm rather than a disabled state: the shader still runs, and still has to
        // produce MoteGlow's output when it subtracts nothing.
        // A light whose field could not be built has nothing to compose against, so it draws the
        // stock additive pass rather than a max against black — which would be our whole model over
        // an unsuppressed vanilla, i.e. two lighting models summed.
        bool composed = maxDrawing && entry.VanillaField != null;

        if (maxDrawing)
        {
            // ZERO UNDER THE APERTURE BEAM, and that is the whole of its draw-side half. The mask
            // has just taken this emitter's vanilla light off the frame entirely, so there is
            // nothing left to subtract and the fan has to deliver the model rather than the
            // difference. Subtracting a field the mask has already removed would darken the beam by
            // exactly the light it is replacing.
            bool replacing = VectorLightMask.Replacing;

            VectorLightShader.SetVanillaWeight(
                entry.Props, maxComposing && composed && !replacing ? 1f : 0f);
            VectorLightShader.SetVanillaTexture(entry.Props, entry.VanillaField);

            // Set on every max draw and not only on the lift ones, because the property block is
            // per emitter and reused frame to frame: an emitter that drew under the lift and then
            // under the additive pass would otherwise keep dividing by a stale ambient. Zero is the
            // additive pass, so writing it unconditionally is what makes the two arms exclusive.
            VectorLightShader.SetSkyAmbient(
                entry.Props,
                surfaceLift ? VectorLightMath.SurfaceAmbient(skyGlow, map.roofGrid.Roofed(entry.Cell)) : 0f);
        }

        Graphics.DrawMesh(
            entry.Mesh, Vector3.zero, Quaternion.identity,
            MaterialFor(entry.Radius, maxDrawing, surfaceLift),
            0, null, 0, entry.Props);
    }

    // How brightly this light competes with the sky above it.
    //
    // Vanilla gets this for free and we have to pay for it: its glow is a vertex colour composited
    // under the sky's own multiply, so at noon a lamp outdoors contributes nothing visible because
    // everything around it is already at full brightness. Ours ADDS, above that multiply, so with no
    // attenuation a torch would glow harder at midday than at midnight.
    //
    // The sky a light competes with is the sky that REACHES IT, which is why this asks the roof grid
    // rather than reading CurSkyGlow flat. Keying on the global value would put every indoor lamp out
    // at noon — the one case where vanilla's lamp is most clearly visible, since a roofed cell renders
    // at a fraction of the sky and the lamp is what lifts it back. §7c's NativeSkyFalloffGrid already
    // answers "how much sky reaches this cell" properly and is the principled upgrade here; the binary
    // roof test is the prototype's version of it.
    private static float StrengthFor(
        Map map, VectorLightField.LightEntry entry, float skyGlow, bool maxComposing,
        bool surfaceLift)
    {
        bool sheltered = map.roofGrid.Roofed(entry.Cell);

        // Passed as the roofed FLAG rather than by feeding a fake zero glow, which is how this used
        // to say it. Same result, but the question is now part of DaylightScale's signature, so the
        // pawn-shadow lane cannot call it without answering the same one — which it did for a long
        // time, and spent that whole time drawing nothing indoors at noon.
        float daylight = VectorLightMath.DaylightScale(skyGlow, sheltered);

        // THE SURFACE LIFT CHANGES WHAT THIS SCALAR MEANS, which is why it returns before every other
        // branch rather than scaling one of them. Under the additive blend the number is an amount of
        // light to add and needs a level; under DstColor/One it is a factor to brighten the frame by,
        // and the factor itself is computed per fragment against the sky ambient plus vanilla's own
        // local glow — the only place vanilla's value is known at better than cell resolution. So the
        // only thing left for a per-draw scalar to say is whether this lamp competes with the sky at
        // all, and that is exactly the daylight curve. No DefaultStrength here, deliberately: see
        // SurfaceAmbient's header for the three-times error that putting it here produced.
        if (surfaceLift)
            return daylight;

        // Under the per-fragment max we deliver the WHOLE of our model, because the compensation
        // happens per fragment against vanilla's local value rather than globally against a
        // constant. Where vanilla already delivered our model's own value the fragment program
        // subtracts all of it and this scalar multiplies zero; where vanilla delivered nothing —
        // the far side of an open door — it multiplies the whole beam. That is the self-limiting
        // property, and cutting the level here would take it back.
        if (maxComposing)
            return VectorLightMath.DefaultStrength * daylight;

        // Riding on top of phase 3's mask rather than over a suppressed vanilla. What is underneath
        // is not a whole second lighting model — it is vanilla with the shadowed light already taken
        // out — so the level is a lift on the lit region rather than a sum of two models.
        if (VectorLightMask.Active)
            return VectorLightMath.MaskBeamStrengthFor(VectorLightSettings.BeamStrength) * daylight;

        // Whatever share of the light the crossfade left vanilla holding, we do not also deliver —
        // otherwise the two models sum and the room lands 6 L* bright, which is the measured failure
        // of drawing over an unsuppressed flood.
        float floor = CelestialLightingFeatures.VectorLightBlend
            ? VectorLightMath.DefaultVanillaFloor
            : 0f;

        return VectorLightMath.BlendedStrength(VectorLightMath.DefaultStrength, floor) * daylight;
    }

    // Cull against the camera before doing anything else. A colony's lamps are overwhelmingly
    // off-screen, and this is what keeps the cost proportional to what is being looked at rather than
    // to how built-up the base is.
    private static bool Overlaps(CellRect view, VectorLightField.LightEntry entry)
    {
        int reach = (int)entry.Radius + 1;
        return entry.Cell.x + reach >= view.minX
            && entry.Cell.x - reach <= view.maxX
            && entry.Cell.z + reach >= view.minZ
            && entry.Cell.z - reach <= view.maxZ;
    }

    private static void Rebuild(Map map, VectorLightField.LightEntry entry, float altitude)
    {
        entry.GeometryDirty = false;

        // The light sits at the CENTRE of its cell, which is where its sprite is drawn and where
        // vanilla's flood seeds. Using the cell's corner instead offsets every shadow by half a cell
        // — small enough to look like imprecision rather than an error.
        float lightX = entry.Cell.x + 0.5f;
        float lightZ = entry.Cell.z + 0.5f;

        VectorLightMath.LightPolygon polygon = PolygonFor(map, entry, lightX, lightZ);

        // The feature flag is a source radius of zero and nothing else. A point source has no
        // penumbra by definition, so off emits no wedge geometry and leaves every V at 0, which is
        // phase 1's mesh exactly rather than a preserved copy of the code that used to build it.
        float sourceRadius = CelestialLightingFeatures.VectorLightPenumbra
            ? VectorLightMath.DefaultSourceRadius
            : 0f;

        VectorLightMath.LightMesh built =
            VectorLightMath.BuildMesh(lightX, lightZ, entry.Radius, polygon, sourceRadius);

        entry.LitArea = PolygonArea(polygon);
        entry.Built = built;
        UploadMesh(entry, built, altitude);

        // New geometry means every sample belongs to a vertex that no longer exists. Marked rather
        // than resampled here so an off-screen or non-max light never pays for it: DrawLight is the
        // only place that knows whether the shader is actually composing this frame.
        entry.SampleDirty = true;
    }

    // The visibility polygon this mesh is extruded from — the one the field already holds whenever
    // it holds one, and a fresh build only when it does not.
    //
    // IT WAS BEING BUILT TWICE PER FRAME, and nothing in the repo could see it. With the mask on —
    // which is the shipped configuration — Patch_VectorLightDraw runs VectorLightField.EnsurePolygons
    // immediately before this pass, so by the time an emitter reaches Rebuild its polygon has already
    // been baked from the same cell, the same radius and the same segment window, in the same frame,
    // with no tick in between. Rebuilding it here scanned the emitter's whole square a second time
    // and re-cast every ray against every wall to arrive at an answer that was sitting on the entry.
    // Tools/VectorLightBench puts that build at 83-94% of a bake in any cluttered scene, so the
    // duplicate was most of the cost of every geometry change a colony makes.
    //
    // The counter could not see it either, which is why it survived: `vector_light_bakes` is
    // incremented inside EnsurePolygon and this path never touched it, so the probe reported one
    // bake per emitter while two were happening. It is a Circinus arm on VectorLightMath.Build,
    // read against that counter, that shows the pair — see Tests/Scenarios/vector_light_frame_cost.
    //
    // WHY A FALLBACK RATHER THAN JUST CALLING EnsurePolygon. With the mask off nothing builds
    // polygons at all, and EnsurePolygon would also bake the coverage grid and the unobstructed
    // flag, neither of which this pass has any use for — so the mask-off arm would come out slower
    // than it is today. Building locally in that case is exactly what this method did before, which
    // is what makes the change bit-identical in every configuration rather than only in the shipped
    // one.
    //
    // THE CULLS ARE NESTED, so the reuse is not a coincidence to be checked frame by frame. The
    // build window is MapDrawer's view rect (camera + 1 cell) tested against ceil(radius) +
    // VectorLightMask.ReachMargin; DrawLight's own cull is the raw camera rect tested against
    // (int)radius + 1. The first admits everything the second does, so an emitter that gets this far
    // was built this frame — and if some later edit breaks that nesting, the fallback below is a
    // slower frame rather than a wrong one.
    private static VectorLightMath.LightPolygon PolygonFor(
        Map map, VectorLightField.LightEntry entry, float lightX, float lightZ)
    {
        if (!entry.PolygonDirty && entry.Polygon.Count > 0)
            return entry.Polygon;

        VectorLightMath.Segment[] segments =
            VectorLightBlockers.SegmentsAround(map, entry.Cell, entry.Radius);

        return VectorLightMath.Build(
            lightX, lightZ, entry.Radius, segments, VectorLightMath.DefaultBaseRayCount);
    }

    private static void UploadMesh(
        VectorLightField.LightEntry entry, VectorLightMath.LightMesh built, float altitude)
    {
        if (built.VertexCount == 0)
        {
            entry.Mesh = null;
            return;
        }

        entry.Mesh = entry.Mesh ?? new Mesh { name = "CelestialLighting_VectorLight" };
        entry.Mesh.Clear();

        Verts.Clear();
        Uvs.Clear();
        Tris.Clear();

        for (int i = 0; i < built.VertexCount; i++)
        {
            Verts.Add(new Vector3(built.X[i], altitude, built.Z[i]));

            // Both axes carry meaning now the gradient is 2-D: U is distance from the light, V is
            // how far across a soft shadow edge the vertex sits. Every vertex of the fan itself
            // carries V = 0, which is the gradient's first row — the falloff curve unmodified.
            Uvs.Add(new Vector2(built.U[i], built.V[i]));
        }

        Tris.AddRange(built.Triangles);

        entry.Mesh.SetVertices(Verts);
        entry.Mesh.SetUVs(0, Uvs);
        entry.Mesh.SetTriangles(Tris, 0);
    }

    private static float PolygonArea(VectorLightMath.LightPolygon polygon)
    {
        float total = 0f;

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;

            float ax = Mathf.Cos(polygon.Angles[i]) * polygon.Distances[i];
            float az = Mathf.Sin(polygon.Angles[i]) * polygon.Distances[i];
            float bx = Mathf.Cos(polygon.Angles[next]) * polygon.Distances[next];
            float bz = Mathf.Sin(polygon.Angles[next]) * polygon.Distances[next];

            total += 0.5f * Mathf.Abs(ax * bz - bx * az);
        }

        return total;
    }

    private static Material MaterialFor(float radius, bool max, bool surfaceLift)
    {
        int key = Mathf.RoundToInt(radius * 4f);
        Dictionary<int, Material> cache = CacheFor(max, surfaceLift);

        if (!cache.TryGetValue(key, out Material material))
        {
            // THE MAX GETS A DIFFERENT CURVE, not just a different program. Its whole arithmetic is
            // ours minus vanilla's, and vanilla's flood is evaluated at octile + 1 because
            // PrepareFill seeds the light's own cell at one cell rather than zero. Handing the
            // fragment program our unseeded curve compares ours at d against vanilla's at d + 1, so
            // the subtraction never reaches zero and every lamp keeps a halo the composition is
            // supposed to have removed — measured at +2.04 L* across a room vanilla had already lit
            // correctly. The stock additive pass is not subtracting vanilla from anything and keeps
            // the unseeded curve, which is why the two caches hold different textures for one radius.
            Texture2D gradient = GradientFor(key, max);
            material = max
                ? VectorLightShader.NewMaterial(gradient, surfaceLift)
                : new Material(ShaderDatabase.MoteGlow) { mainTexture = gradient };
            cache[key] = material;
        }

        return material;
    }

    // Which of the three material caches this draw belongs in. Named rather than nested in
    // MaterialFor's lookup because "which program, and which blend" is two questions and reading
    // them as one conditional expression is what makes a third composition easy to mis-key.
    private static Dictionary<int, Material> CacheFor(bool max, bool surfaceLift)
    {
        if (!max)
            return MaterialsByRadius;

        return surfaceLift ? LiftMaterialsByRadius : MaxMaterialsByRadius;
    }

    // One gradient per radius, shared by both material caches — the curve does not depend on which
    // program consumes it, and a 256x32 texture per radius is the expensive half of MaterialFor.
    private static Texture2D GradientFor(int key, bool matchSeed)
    {
        // Keyed on the seed as well as the radius, because the two curves genuinely differ and a
        // cache that ignored the flag would hand whichever program asked first its own texture to
        // every later one. Negative keys for the seeded half rather than a second dictionary: the
        // key is a rounded radius in quarter-cells and is never negative, so the two spaces cannot
        // collide.
        int cacheKey = matchSeed ? -key - 1 : key;

        if (!GradientsByRadius.TryGetValue(cacheKey, out Texture2D gradient))
        {
            gradient = BuildGradient(key / 4f, matchSeed);
            GradientsByRadius[cacheKey] = gradient;
        }

        return gradient;
    }

    // Vanilla's delivered glow over this emitter's square, uploaded as a TEXTURE, plus the UV1
    // coordinates that let each fragment look itself up in it.
    //
    // WHY A TEXTURE AND NOT A PER-VERTEX VALUE, which is the whole correction over #151. #151 wrote
    // vanilla's glow into UV1 as a value and let the hardware interpolate it across the triangle.
    // The fan's triangles are long radial slivers that all share ONE apex — at the lamp, where
    // vanilla's glow is near its maximum — and reach out to the polygon rim, where it is near zero.
    // Linear interpolation between those two therefore tells a fragment halfway along a doorway beam
    // that vanilla is roughly half-maximum where the true value is almost nothing, and
    // max(0, ours - vanilla) clamps to zero across the beam. That is why #151 measured its
    // composition as a no-op: not because the two models agree, but because one of its two inputs
    // was sampled at a rate the input does not survive.
    //
    // The signature was specific enough to name: on the door scene the ONLY geometry that lit was
    // the penumbra wedges, which are short triangles out at the rim whose three vertices all sample
    // low vanilla, while the fan's interior stayed dark. A wrong texture, a wrong weight or a wrong
    // sample position would all fail uniformly; that failed BY TRIANGLE LENGTH.
    //
    // So UV1 carries a POSITION instead. Position across a triangle genuinely is linear, so it
    // interpolates exactly, and the value it looks up does not have to. One texel per cell is
    // vanilla's own resolution — this is not an approximation of its field, it IS its field,
    // bilinearly filtered the same way vanilla's lighting overlay filters the same numbers.
    //
    // THIS EMITTER'S OWN GLOW, NOT THE ACCUMULATED TOTAL. #151 sampled GlowGrid.VisualGlowAt because
    // it composed against the whole frame vanilla draws; the per-light array is the same quantity
    // phase 3's mask works on, so using it keeps the two halves composing the same thing. It is also
    // a straight memcpy of a buffer that already exists in exactly this layout, where the total
    // would be a per-cell lookup.
    //
    // GAMEPLAY LIGHT IS READ, NEVER WRITTEN. Nothing here dirties the grid, invalidates it or
    // schedules a recompute, so GroundGlowAt and everything built on it return what they always did.
    private static void UploadVanillaField(Map map, VectorLightField.LightEntry entry)
    {
        entry.SampleDirty = false;

        VectorLightMath.LightMesh built = entry.Built;

        if (entry.Mesh == null || built.VertexCount == 0)
            return;

        GlowGridPerLight.Reader reader = GlowGridPerLight.For(map);

        if (reader == null
            || !reader.TryResolveEmitter(entry.VanillaKey, out GlowLight light, out UnsafeList<Color32> colors))
        {
            // No per-light arrays means nothing to compose against. Leaving the field black would
            // make the shader subtract nothing and draw our whole model over an unsuppressed
            // vanilla, which is the summing failure epic #145 rejected — so the pass stands down for
            // this emitter instead, and VectorLightShader.Available is what a scenario pins.
            entry.VanillaField = null;
            return;
        }

        int diameter = light.diameter;

        if (diameter <= 0 || colors.Length < diameter * diameter)
            return;

        EnsureField(entry, diameter);
        // light.position rather than entry.Cell, because the mask's own loop measures its offsets
        // from light.position and the two sides of the per-cell rule have to agree cell for cell.
        CopyField(
            entry, entry.VanillaField, colors, diameter, light.localGlowGridStartPos, light.position);
        UploadFieldUvs(entry, built, light.localGlowGridStartPos, diameter);
    }

    private static void EnsureField(VectorLightField.LightEntry entry, int diameter)
    {
        if (entry.VanillaField != null && entry.VanillaField.width == diameter)
            return;

        if (entry.VanillaField != null)
            Object.Destroy(entry.VanillaField);

        entry.VanillaField = new Texture2D(diameter, diameter, TextureFormat.RGBA32, mipChain: false)
        {
            // Clamp for the same reason the gradient clamps: the penumbra wedges lie just outside
            // the polygon and can reach a hair past the emitter's square, where wrapping would fetch
            // the glow from the opposite side of the lamp and put a bright seam on a shadow edge.
            wrapMode = TextureWrapMode.Clamp,

            // Bilinear, matching what vanilla's own lighting overlay does with these same numbers —
            // it interpolates them across its mesh. Point filtering would give the composition a
            // cell-shaped staircase, which is precisely the resolution this pass exists to escape.
            filterMode = FilterMode.Bilinear,
        };
    }

    // WRITTEN INTO THE TEXTURE'S OWN BUFFER, NOT THROUGH SetPixels32. The array overload demands an
    // array of EXACTLY width*height, which a scratch buffer shared between emitters cannot promise:
    // this used to grow a static Color32[] to the largest field seen and hand the whole thing over, so
    // the first smaller emitter to upload — a radius-3 torch drawn after a radius-14 sun lamp — threw
    // "the size of data to be written would result in writing outside the target buffer bounds". From
    // a Postfix on GameConditionManagerDraw that throw is not local: it aborts the rest of the draw
    // chain (§11a's aurora, §23b's cloud underlight, §24's snow glare) for the frame, every frame.
    //
    // Sizing the scratch exactly would fix the throw and give back the allocation the buffer existed
    // to avoid, once per emitter per frame whenever two radii alternate on screen. GetRawTextureData
    // gives up nothing instead: it is a NativeArray view of the texture's own RGBA32 storage, already
    // exactly diameter*diameter Color32s in the row order SetPixels32 was writing, so the copy stays a
    // straight per-texel write with no second buffer to keep in step. Same argument as
    // AuroraCurtainOverlay's LoadRawTextureData, one step further: no intermediate array at all.
    // WEIGHTED BY COVERAGE, WHICH IS WHAT MAKES A GAP BEHAVE LIKE A DOOR. What the fragment program
    // needs to subtract is what vanilla still contributes ON SCREEN, and that is not what the glow
    // grid holds. The mask has already scaled each cell's vanilla light by this emitter's coverage
    // there — how much of the cell our polygon can actually see — so subtracting the raw grid value
    // takes off light the mask has removed already, twice.
    //
    // PAST A DOOR IT NEVER MATTERED AND THAT IS WHY IT SURVIVED. RimWorld's glow grid never learns a
    // door opened, so beyond one the raw value is exactly zero, the double-subtraction is zero, and
    // every doorway scenario in this repo measures the composition working perfectly. Past a one-cell
    // GAP the grid floods straight through: coverage there is well under 1 because our polygon
    // correctly says a narrow aperture leaves those cells only partly able to see the lamp, while
    // vanilla's flood bends around and fills them. Measured on vector_light_gap_vs_door.json, the
    // ground one cell outside a gap sat at 6.22 L* against vanilla's 7.67 — our own beam subtracted
    // itself out of existence and took some of vanilla's light with it.
    private static void CopyField(
        VectorLightField.LightEntry entry, Texture2D field, UnsafeList<Color32> colors, int diameter,
        IntVec3 start, IntVec3 lightCell)
    {
        int count = diameter * diameter;

        // Both read once and threaded down, for the reason the mask's own locals are: this runs
        // once per texel of every emitter's square whenever a light resamples.
        bool gapParity = CelestialLightingFeatures.VectorLightGapParity;
        bool bentPath = VectorLightMask.BentPath;

        NativeArray<Color32> texels = field.GetRawTextureData<Color32>();

        for (int i = 0; i < count; i++)
        {
            Color32 glow = colors[i];

            // Texel i covers cell i of the emitter's square, in the same row order UploadFieldUvs
            // maps vertices into — so the cell this texel speaks for is start + (i % d, i / d), and
            // the coverage grid is indexed in world cells around the emitter rather than in this
            // square's local ones.
            int cellX = start.x + i % diameter;
            int cellZ = start.z + i / diameter;

            int share = SurvivingShare(entry, glow, cellX, cellZ, lightCell, gapParity, bentPath);

            // Integer, and rounded the way a byte scale should be: (v * c + 127) / 255 rather than a
            // float multiply, so a fully covered cell (255) returns its own value unchanged instead
            // of drifting a level on the way through.
            glow.r = (byte)((glow.r * share + 127) / 255);
            glow.g = (byte)((glow.g * share + 127) / 255);
            glow.b = (byte)((glow.b * share + 127) / 255);

            // ALPHA IS NOT OPACITY IN THIS BUFFER. ComputeGlowGridsJob writes the accumulated
            // DISTANCE into it (`colorInt.a = (int)num2`), so copying it through would hand the
            // sampler a channel that means nothing and looks like a mask. The fragment program reads
            // rgb only, but leaving distance in alpha is the kind of thing that becomes a bug the
            // first time somebody adds an alpha term.
            glow.a = 255;
            texels[i] = glow;
        }

        field.Apply(updateMipmaps: false);
    }

    // How much of this emitter's vanilla light the MASK LEFT STANDING at one cell, as the byte scale
    // the copy above applies. This is the whole of what the fragment program is entitled to subtract:
    // subtracting more takes off light nobody is drawing, and subtracting less sums two models.
    //
    // THE ORDER OF THE TWO RULES IS NOT ARBITRARY. The per-cell replacement is all-or-nothing and
    // wins outright, because where it fires the mask has already removed the cell's whole
    // contribution and there is no remaining share for the coverage weighting to describe. Asking
    // coverage first and then zeroing would give the same answer and read as though a partly-covered
    // claimed cell kept part of its vanilla light, which it does not.
    private static int SurvivingShare(
        VectorLightField.LightEntry entry, Color32 glow, int cellX, int cellZ, IntVec3 lightCell,
        bool gapParity, bool bentPath)
    {
        bool delivered = glow.r != 0 || glow.g != 0 || glow.b != 0;

        // The identical predicate the mask ran, on the identical inputs — vanilla's own accumulated
        // distance out of the alpha channel, against the straight run to the same offset. The two
        // sides cannot drift apart without the pure core changing under both of them.
        if (bentPath && VectorLightLiftMath.VanillaBentToArrive(
                cellX - lightCell.x, cellZ - lightCell.z, glow.a, delivered))
        {
            return 0;
        }

        if (!gapParity)
            return 255;

        return VectorLightMath.CoverageAt(
            entry.Coverage, entry.Cell.x, entry.Cell.z, entry.CoverageRadius, cellX, cellZ);
    }

    // Where each vertex sits in the emitter's square, in [0, 1] on both axes.
    //
    // The square spans `diameter` cells from localGlowGridStartPos, and texel i covers cell i, so a
    // cell centre at start + i + 0.5 maps to (i + 0.5) / diameter — the texel's own centre, which is
    // what bilinear filtering needs to return that cell's value unmixed. Dividing world position by
    // the diameter does that without a special case.
    private static void UploadFieldUvs(
        VectorLightField.LightEntry entry, VectorLightMath.LightMesh built, IntVec3 start, int diameter)
    {
        VanillaUvs.Clear();

        float scale = 1f / diameter;

        for (int i = 0; i < built.VertexCount; i++)
        {
            VanillaUvs.Add(new Vector4(
                (built.X[i] - start.x) * scale,
                (built.Z[i] - start.z) * scale,
                0f,
                0f));
        }

        entry.Mesh.SetUVs(1, VanillaUvs);
    }


    // The falloff curve and the penumbra ramp as one 2-D texture: white throughout, with the product
    // of the two in ALPHA. That split is copied from AuroraCurtain, which writes colour into RGB and
    // intensity into alpha and is the one thing here already proven to modulate correctly through
    // MoteGlow. Putting the curve in both channels would square it if the shader premultiplies, which
    // is the sort of mistake that reads as "the falloff is too aggressive" rather than as a bug.
    //
    // WHY A TEXTURE AND NOT A SHADER. Soft edges were carried on the epic as blocked on a custom
    // shader, and they are not: falloff(u) * ramp(v) is separable, so one bilinear sample of a 2-D
    // texture reproduces it EXACTLY, with nothing left for a fragment program to compute. A shader
    // would mean shipping a compiled AssetBundle per platform — the toolchain for which does now
    // work here — but it would buy no fidelity, and it would put the feature behind an asset that
    // has to be rebuilt for three platforms and checked with shader.isSupported at runtime.
    private static Texture2D BuildGradient(float radius, bool matchSeed)
    {
        byte[] curve = VectorLightMath.PenumbraGradient(
            radius, VectorLightMath.GradientSize, VectorLightMath.PenumbraGradientSize, matchSeed);
        byte[] rgba = new byte[curve.Length * 4];

        for (int i = 0; i < curve.Length; i++)
        {
            rgba[i * 4] = 255;
            rgba[i * 4 + 1] = 255;
            rgba[i * 4 + 2] = 255;
            rgba[i * 4 + 3] = curve[i];
        }

        Texture2D texture = new Texture2D(
            VectorLightMath.GradientSize, VectorLightMath.PenumbraGradientSize,
            TextureFormat.RGBA32, mipChain: false)
        {
            // Clamp, not wrap, on BOTH axes now. On U it stops a value of exactly 1 at the rim from
            // sampling the bright end of the falloff and ringing the outer edge of every light; on V
            // it stops the fully-occluded edge of a wedge from wrapping back round to fully lit,
            // which would draw a bright seam along the far side of every soft shadow.
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "CelestialLighting_VectorLightGradient"
        };

        texture.LoadRawTextureData(rgba);
        texture.Apply(false);
        return texture;
    }
}
