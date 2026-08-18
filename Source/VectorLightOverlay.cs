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

    private static readonly List<Vector3> Verts = new List<Vector3>();
    private static readonly List<Vector2> Uvs = new List<Vector2>();
    private static readonly List<int> Tris = new List<int>();

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
        float strength = StrengthFor(map, entry, skyGlow);

        if (strength <= 0f || !Overlaps(view, entry))
            return;

        if (entry.GeometryDirty || entry.Mesh == null)
            Rebuild(map, entry, altitude);

        if (entry.Mesh == null)
            return;

        // Per-draw colour goes through a MaterialPropertyBlock and not Material.color. Graphics.DrawMesh
        // is DEFERRED — the draws are queued and resolved later — so writing the material's colour
        // between calls gives every light in the frame whichever colour was written last. §17's branch
        // paid for learning this.
        Color color = entry.Color;
        entry.Props.SetColor(ShaderPropertyIDs.Color, new Color(color.r, color.g, color.b, strength));

        Graphics.DrawMesh(
            entry.Mesh, Vector3.zero, Quaternion.identity, MaterialFor(entry.Radius),
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
    private static float StrengthFor(Map map, VectorLightField.LightEntry entry, float skyGlow)
    {
        bool sheltered = map.roofGrid.Roofed(entry.Cell);
        float daylight = VectorLightMath.DaylightScale(sheltered ? 0f : skyGlow);

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
        UploadMesh(entry, built, altitude);
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

    private static Material MaterialFor(float radius)
    {
        int key = Mathf.RoundToInt(radius * 4f);

        if (!MaterialsByRadius.TryGetValue(key, out Material material))
        {
            material = new Material(ShaderDatabase.MoteGlow) { mainTexture = BuildGradient(key / 4f) };
            MaterialsByRadius[key] = material;
        }

        return material;
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
