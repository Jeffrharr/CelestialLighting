using System.Diagnostics;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §25c variant D's live half: uploads the baked cloud volume as a 3-D texture and drives
// Shaders/… CelestialCloudVolume.shader with it (DESIGN.md §25c, issue #144).
//
// This is the thin adapter over CloudRaymarchMath the house style asks for, and the split falls in
// an unusually clean place because the expensive half is not code that runs here at all — it is
// HLSL. What is left is: bake a byte array (pure), hand it to the GPU once, and set eight uniforms
// per sheet per frame.
//
// THE FIRST CUSTOM SHADER THIS MOD HAS SHIPPED, and DESIGN.md §11a is on record rejecting one. That
// rejection was a cost-and-precedent call — "this repo ships no binary assets" — not a technical
// one, and it was made for the aurora, where the thing wanted was a curtain a mesh could already
// draw. What is wanted here cannot be reached from a texture at all: a bake is multiplied into
// `material.color` by ShaderDatabase.Transparent, an 8-bit texture has no value above 1, and a
// silver lining is by definition brighter than the cloud it edges. See DESIGN.md §25c.
//
// EVERY PATH HERE DEGRADES TO §25b. A missing bundle, an unsupported shader, a graphics API that
// will not take a 3-D texture — all of them land on `Available == false`, and CloudSheetOverlay then
// draws exactly what it drew before this file existed. That is not defensive habit: only Linux
// bundles are built today, so it is the path most subscribers are on right now.
[StaticConstructorOnStartup]
public static class CloudVolumeShader
{
    // Named without a folder prefix, unlike vanilla's "Map/Transparent". ContentFinder searches mod
    // bundles for exactly `Materials/<this>.shader`, and the bundle is built with the asset at
    // Assets/Data/joof.celestiallighting/Materials/CelestialCloudVolume.shader to match.
    public const string ShaderPath = "CelestialCloudVolume";

    // A zero slice above and below the deck, so the hardware's clamped sampling FADES out of the
    // volume the way CloudRaymarchMath.Sample does instead of smearing the outermost slice outward
    // forever. Two slices of 384x384 is 288 KB to avoid a branch in the hottest loop in the mod.
    private const int PadSlices = 2;

    // Property ids, resolved once. Material.SetX(string) hashes the name on every call, and this is
    // eight setters per sheet per frame.
    //
    // THESE MUST BE DECLARED ABOVE THE MATERIALS, and the reason is a C# rule with a very quiet
    // failure mode. Static field initialisers run in DECLARATION order, so with this block at the
    // bottom of the class — where a reader would naturally file it — `BuildMaterials` ran while
    // `TextureId` was still 0 and bound the volume to property id 0 instead of to `_Volume`. Nothing
    // logs that. The sampler simply stays unbound, Unity substitutes its default WHITE texture, the
    // march reads a density of 1 at every voxel it looks at, and every sheet comes out as a solid
    // opaque rectangle — a symptom that looks exactly like a broken shader and is not one.
    private static readonly int TextureId = Shader.PropertyToID("_Volume");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int ShadowColorId = Shader.PropertyToID("_ShadowColor");
    private static readonly int VolumeParamsId = Shader.PropertyToID("_VolumeParams");
    private static readonly int CellBoundsId = Shader.PropertyToID("_CellBounds");
    private static readonly int SunDirId = Shader.PropertyToID("_SunDir");
    private static readonly int MarchParamsId = Shader.PropertyToID("_MarchParams");

    // Peak height in texels per deck: depends only on the atlas geometry and the deck table, and it
    // is read once per sheet per frame.
    private static readonly float[] PeakTexels =
        CloudRaymarchMath.RowPeakTexels(CloudSheetOverlay.AtlasSize / CloudSheetOverlay.AtlasCells,
            CloudDeckMath.DeckCount);

    private static readonly Shader VolumeShader = ShaderDatabase.LoadShader(ShaderPath);

    private static readonly Texture3D Volume = BuildVolume();

    private static readonly Material[] SheetMats = BuildMaterials();

    // How long the bake took, in milliseconds, for the probe that reports it. Load-time cost, paid
    // once, on a loading screen — but it is the number somebody will want when deciding whether to
    // move it to a background Task, so it is measured rather than estimated.
    public static double BakeMilliseconds { get; private set; }

    // Whether this machine takes the one-byte volume format, reported by a probe.
    //
    // Here because a texture format that the driver declines is the OTHER way a march reads a
    // density of 1 everywhere and draws solid rectangles, and the two causes are indistinguishable
    // on screen. Turning it into a number a scenario can read costs nothing and saves a whole live
    // run of guessing next time this goes wrong.
    public static bool VolumeFormatSupported =>
        SystemInfo.SupportsTextureFormat(TextureFormat.Alpha8);

    // Whether this path can be used at all. Checked by the overlay ahead of the feature flag, so a
    // player who turns the feature on with no usable shader gets the baked cloud rather than nothing.
    public static bool Available => VolumeShader != null && VolumeShader.isSupported && Volume != null;

    // Sets everything that varies per sheet and returns the material to draw it with.
    //
    // `litColour` is what a fully sunlit part of this cloud looks like — the same colour §25b writes
    // into `material.color` — and `shadowRatio` the per-channel fraction of it that reaches a part
    // the beam never gets to. The shader interpolates between them, so the two of them together are
    // the entire colour model and neither is a modulation of a texture.
    public static Material Configure(
        int slot, in CloudSheetLayout.Placement placement, int atlasCells, int atlasSize,
        Color litColour, Color shadowColour,
        float sunAzimuthDegrees, float sunElevationDegrees)
    {
        Material material = SheetMats[slot];

        int blob = CloudSheetLayout.BlobFor(placement, atlasCells);
        int blobSize = atlasSize / atlasCells;
        int blobX = blob % atlasCells;
        int blobY = blob / atlasCells;

        float cell = 1f / atlasCells;
        float scaleU = placement.FlipU ? -cell : cell;
        float scaleV = placement.FlipV ? -cell : cell;

        material.SetTextureScale(TextureId, new Vector2(scaleU, scaleV));
        material.SetTextureOffset(TextureId, new Vector2(
            (blobX + (placement.FlipU ? 1 : 0)) * cell,
            (blobY + (placement.FlipV ? 1 : 0)) * cell));

        material.SetColor(ColorId, litColour);
        material.SetColor(ShadowColorId, shadowColour);

        // The deck's own vertical extent, which is what makes cirrus a flat sheet that barely
        // shadows itself and cumulus a tower that does. A2 records the same intent in its comments
        // and never wired it through; here each deck is already its own draw call, so it is free.
        float peakTexels = PeakTexels[placement.Deck];
        float layerTexels = peakTexels / CloudVolumeMath.VolumeLayers;

        material.SetVector(VolumeParamsId, new Vector4(
            atlasSize, CloudVolumeMath.VolumeLayers + PadSlices, peakTexels, layerTexels));

        material.SetVector(CellBoundsId, new Vector4(
            blobX * blobSize, blobY * blobSize,
            blobX * blobSize + blobSize - 1, blobY * blobSize + blobSize - 1));

        CloudRaymarchMath.SunVector(sunAzimuthDegrees, sunElevationDegrees,
            out float lu, out float lv, out float lh);

        // MIRRORED WITH THE TEXTURE, and this is the one correctness win that comes free with a
        // shader. A sheet drawn with a negative texture scale reads the atlas backwards, so a BAKED
        // lit side arrives on the wrong flank — half the sky lit from the east — and the only fix
        // available to a bake is to stop mirroring, which costs the sheets their variety. Flipping
        // the light direction alongside the texture keeps both.
        material.SetVector(SunDirId, new Vector4(
            placement.FlipU ? -lu : lu,
            placement.FlipV ? -lv : lv,
            lh, 0f));

        // w is the LIGHT ray's coefficient, scaled by this deck's own thickness — see
        // CloudRaymarchMath.LightExtinctionFor. Without it a thin deck at a grazing sun is fully
        // self-occluded and renders at the ambient floor, which is the opposite of the sunset §25b's
        // deck windows exist to draw.
        material.SetVector(MarchParamsId, new Vector4(
            CloudRaymarchMath.ViewExtinctionFor(peakTexels, PeakTexels[0]),
            CloudRaymarchMath.AmbientWrap,
            blobSize * 0.667f,
            CloudRaymarchMath.LightExtinctionFor(peakTexels, PeakTexels[0])));

        return material;
    }

    private static Texture3D BuildVolume()
    {
        if (VolumeShader == null || !VolumeShader.isSupported)
            return null;

        int size = CloudSheetOverlay.AtlasSize;
        int layers = CloudVolumeMath.VolumeLayers;

        Stopwatch watch = Stopwatch.StartNew();

        byte[] volume = new byte[size * size * layers];
        CloudVolumeMath.FillBlobVolume(
            volume, size, CloudSheetOverlay.AtlasCells, layers,
            seed: CloudSheetOverlay.AtlasSeed, octaves: CloudField.SheetOctaves,
            // §25d's shaping, so the marched cloud is the same cloud the baked one is. The volume is
            // baked once at load, before any flag can be read, so it takes the §25d shape
            // unconditionally: §25c is itself opt-in, and a player who has turned the raymarch on has
            // not asked to see the pre-#144 silhouette through it.
            rowCut: CloudDeckMath.PresentShapeCuts(),
            rowGain: CloudDeckMath.ShapeGains(),
            rowFrequencyU: CloudDeckMath.FrequenciesU(),
            rowFrequencyV: CloudDeckMath.FrequenciesV(),
            coreFraction: CloudField.PresentBlobCoreFraction,
            rimBite: CloudField.PresentRimBite,
            densityGamma: CloudSheetMath.PresenceAlphaGamma);

        watch.Stop();
        BakeMilliseconds = watch.Elapsed.TotalMilliseconds;

        // TRANSPOSED on the way in. CloudVolumeMath stores a column's layers together, because both
        // of its own marches run down a column; a Texture3D wants slice-major, x fastest. Doing it
        // here rather than changing the layout keeps the CPU variants' access pattern intact — they
        // are still the fallback, and they are the ones that have to be fast on a CPU.
        int padded = layers + PadSlices;
        byte[] pixels = new byte[size * size * padded];

        for (int layer = 0; layer < layers; layer++)
        {
            int slice = (layer + 1) * size * size;
            for (int y = 0; y < size; y++)
            {
                int row = slice + y * size;
                for (int x = 0; x < size; x++)
                    pixels[row + x] = volume[CloudVolumeMath.VolumeIndex(x, y, layer, size, layers)];
            }
        }

        Texture3D texture = new Texture3D(size, size, padded, TextureFormat.Alpha8, mipChain: false)
        {
            name = "CelestialLighting_CloudVolume",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 0,
        };

        texture.SetPixelData(pixels, 0);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static Material[] BuildMaterials()
    {
        if (VolumeShader == null || !VolumeShader.isSupported || Volume == null)
            return null;

        Material[] materials = new Material[CloudSheetLayout.MaxSheets];
        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = new Material(VolumeShader);
            materials[i].SetTexture(TextureId, Volume);
        }

        return materials;
    }
}
