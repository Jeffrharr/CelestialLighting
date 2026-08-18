using System.Collections.Generic;
using UnityEngine;
using Verse;

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

    // The same gradients again, bound to §27 phase 2b's custom shader instead of MoteGlow. Two
    // caches rather than one keyed on the mode, because the mode is a per-frame answer — the shader
    // can be unavailable, and the flag can be flipped by the harness mid-run — and a light must not
    // have to throw away its material to change composition.
    private static readonly Dictionary<int, Material> MaxMaterialsByRadius = new Dictionary<int, Material>();

    // Gradients are keyed on radius alone and shared between the two material caches. A 256x32
    // texture per distinct radius is the expensive half of MaterialFor, and it is identical either
    // way: the composition changes what the fragment program does with the curve, never the curve.
    private static readonly Dictionary<int, Texture2D> GradientsByRadius = new Dictionary<int, Texture2D>();

    private static readonly List<Vector3> Verts = new List<Vector3>();
    private static readonly List<Vector2> Uvs = new List<Vector2>();
    private static readonly List<int> Tris = new List<int>();

    // Vanilla's delivered glow per vertex, in UV1. Vector4 rather than Vector3 because Unity's mesh
    // API takes UV channels as float4 and a float3 overload would silently pad anyway.
    private static readonly List<Vector4> VanillaUvs = new List<Vector4>();

    public static void Draw(Map map)
    {
        if (!CelestialLightingFeatures.VectorLights || map == null)
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
        // TWO ANSWERS, NOT ONE, and keeping them apart is what makes the control arm possible.
        // `maxDrawing` is which material the pass goes through — asked for AND possible, since a
        // machine that cannot run the shader has to compose as the crossfade instead. `maxComposing`
        // is whether that shader will actually subtract anything, and it is what the level and the
        // suppression key off: a shader subtracting nothing must be drawn over a suppressed vanilla,
        // or the control arm measures the sum of two models rather than phase 1's render.
        bool maxDrawing = VectorLightShader.MaxActive;
        bool maxComposing = maxDrawing && CelestialLightingFeatures.VectorLightMaxSubtract;

        float strength = StrengthFor(map, entry, skyGlow, maxComposing);

        if (strength <= 0f || !Overlaps(view, entry))
            return;

        if (entry.GeometryDirty || entry.Mesh == null)
            Rebuild(map, entry, altitude);

        if (entry.Mesh == null)
            return;

        // Only under the max, and only when something moved. Off-screen lights never get here at all
        // — the cull above is what keeps this proportional to what is being looked at — so a colony
        // switching a lamp pays for resampling the handful of lights currently in view.
        if (maxDrawing && entry.SampleDirty)
            UploadVanillaSamples(map, entry);

        // Per-draw colour goes through a MaterialPropertyBlock and not Material.color. Graphics.DrawMesh
        // is DEFERRED — the draws are queued and resolved later — so writing the material's colour
        // between calls gives every light in the frame whichever colour was written last. §17's branch
        // paid for learning this.
        Color color = entry.Color;
        entry.Props.SetColor(ShaderPropertyIDs.Color, new Color(color.r, color.g, color.b, strength));

        // Set on the same property block and for the same deferred-draw reason as the colour. Zero
        // is the control arm rather than a disabled state: the shader still runs, and still has to
        // produce MoteGlow's output when it subtracts nothing.
        if (maxDrawing)
            VectorLightShader.SetVanillaWeight(entry.Props, maxComposing ? 1f : 0f);

        Graphics.DrawMesh(
            entry.Mesh, Vector3.zero, Quaternion.identity, MaterialFor(entry.Radius, maxDrawing),
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
        Map map, VectorLightField.LightEntry entry, float skyGlow, bool maxComposing)
    {
        bool sheltered = map.roofGrid.Roofed(entry.Cell);
        float daylight = VectorLightMath.DaylightScale(sheltered ? 0f : skyGlow);

        // Under the crossfade, whatever share of the light vanilla was left holding we do not also
        // deliver — otherwise the two models sum and the room lands 6 L* bright, which is the
        // measured failure of drawing over an unsuppressed flood. Under the max we deliver the whole
        // of it, because the compensation happens per fragment against vanilla's local value instead
        // of globally against a constant. Both cases live in the pure core so the offline tests can
        // pin the endpoints, and so this and Patch_VectorLightSuppress cannot drift apart.
        return VectorLightMath.StrengthFor(
            VectorLightMath.DefaultStrength, maxComposing, CelestialLightingFeatures.VectorLightBlend)
            * daylight;
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

        VectorLightMath.Segment[] segments =
            VectorLightBlockers.SegmentsAround(map, entry.Cell, entry.Radius);

        // The light sits at the CENTRE of its cell, which is where its sprite is drawn and where
        // vanilla's flood seeds. Using the cell's corner instead offsets every shadow by half a cell
        // — small enough to look like imprecision rather than an error.
        float lightX = entry.Cell.x + 0.5f;
        float lightZ = entry.Cell.z + 0.5f;

        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
            lightX, lightZ, entry.Radius, segments, VectorLightMath.DefaultBaseRayCount);

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
        // than resampled here so an off-screen or crossfaded light never pays for it: DrawLight is
        // the only place that knows whether the max is actually composing this frame.
        entry.SampleDirty = true;
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

    // Vanilla's delivered glow at every vertex of the mesh, written into UV1 for the fragment
    // program to subtract. Rewrites one UV channel and touches neither the vertices nor the
    // triangles, which is the whole reason SampleDirty is separate from GeometryDirty.
    //
    // WHAT IS BEING SAMPLED, and why it is the accumulated glow rather than this light's own. The
    // shader is composing against the frame vanilla actually draws, and that frame carries the sum
    // of every light reaching the cell. Where two lamps overlap that means we subtract more than
    // this one light put there and add slightly less than a true per-light max would — dimmer, never
    // brighter, which is the direction §27 can afford to be wrong in.
    //
    // GAMEPLAY LIGHT IS READ, NEVER WRITTEN. VisualGlowAt is a lookup into the finished grid; nothing
    // here dirties it, invalidates it or schedules a recompute, so GroundGlowAt and everything built
    // on it return exactly what they always did.
    private static void UploadVanillaSamples(Map map, VectorLightField.LightEntry entry)
    {
        entry.SampleDirty = false;

        VectorLightMath.LightMesh built = entry.Built;

        if (entry.Mesh == null || built.VertexCount == 0)
            return;

        float lightX = entry.Cell.x + 0.5f;
        float lightZ = entry.Cell.z + 0.5f;

        VanillaUvs.Clear();

        for (int i = 0; i < built.VertexCount; i++)
        {
            VectorLightMath.SampleTowardLight(
                built.X[i], built.Z[i], lightX, lightZ, VectorLightMath.VanillaSamplePull,
                out float sampleX, out float sampleZ);

            Color32 glow = GlowAt(map, sampleX, sampleZ);

            VanillaUvs.Add(new Vector4(
                VectorLightMath.GlowUnit(glow.r),
                VectorLightMath.GlowUnit(glow.g),
                VectorLightMath.GlowUnit(glow.b),
                0f));
        }

        entry.Mesh.SetUVs(1, VanillaUvs);
    }

    // Vanilla's glow at a point, clamped into the map rather than defaulted to zero outside it. Zero
    // would mean "vanilla delivers nothing here", so we would subtract nothing and hand back the full
    // unsubtracted render in a band along every map edge — a visible bright rim on exactly the cells
    // a player is least likely to look at closely and most likely to screenshot.
    private static Color32 GlowAt(Map map, float x, float z)
    {
        int cellX = Mathf.Clamp(Mathf.FloorToInt(x), 0, map.Size.x - 1);
        int cellZ = Mathf.Clamp(Mathf.FloorToInt(z), 0, map.Size.z - 1);

        return map.glowGrid.VisualGlowAt(new IntVec3(cellX, 0, cellZ));
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

    private static Material MaterialFor(float radius, bool maxDrawing)
    {
        int key = Mathf.RoundToInt(radius * 4f);

        Dictionary<int, Material> cache = maxDrawing ? MaxMaterialsByRadius : MaterialsByRadius;

        if (!cache.TryGetValue(key, out Material material))
        {
            Texture2D gradient = GradientFor(key);

            material = maxDrawing
                ? VectorLightShader.NewMaterial(gradient)
                : new Material(ShaderDatabase.MoteGlow) { mainTexture = gradient };

            cache[key] = material;
        }

        return material;
    }

    private static Texture2D GradientFor(int key)
    {
        if (!GradientsByRadius.TryGetValue(key, out Texture2D gradient))
        {
            gradient = BuildGradient(key / 4f);
            GradientsByRadius[key] = gradient;
        }

        return gradient;
    }

    // The falloff curve and the penumbra ramp as one 2-D texture: white throughout, with the product
    // of the two in ALPHA. That split is copied from AuroraCurtain, which writes colour into RGB and
    // intensity into alpha and is the one thing here already proven to modulate correctly through
    // MoteGlow. Putting the curve in both channels would square it if the shader premultiplies, which
    // is the sort of mistake that reads as "the falloff is too aggressive" rather than as a bug.
    //
    // WHY A TEXTURE AND NOT A SHADER — STILL TRUE, EVEN THOUGH A SHADER NOW SHIPS. Soft edges were
    // carried on the epic as blocked on a custom shader, and they were not: falloff(u) * ramp(v) is
    // separable, so one bilinear sample of a 2-D texture reproduces it EXACTLY, with nothing left for
    // a fragment program to compute. §27 phase 2b did eventually bring a compiled AssetBundle into
    // the repo, and it changed nothing here: what it buys is a per-vertex channel MoteGlow will not
    // read, not fidelity in the curve. The curve is still baked, and it is still what both materials
    // sample.
    private static Texture2D BuildGradient(float radius)
    {
        byte[] curve = VectorLightMath.PenumbraGradient(
            radius, VectorLightMath.GradientSize, VectorLightMath.PenumbraGradientSize);
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
