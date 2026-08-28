using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §25c variant D's live half: uploads the baked cloud volume as a 3-D texture and drives
// Shaders/… CelestialCloudVolume.shader with it (DESIGN.md §25c, issue #144).
//
// This is the thin adapter over CloudRaymarchMath the house style asks for, and the split falls in
// an unusually clean place because the expensive half is not code that runs here at all — it is
// HLSL. What is left is: bake a byte array (pure, and since §25e on a background thread), hand it to
// the GPU once, and set eight uniforms per sheet per frame.
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
    // The shader's path INSIDE the bundle, minus the extension: RimWorld builds the full asset path
    // as Assets/Data/<packageId>/Materials/<this>.shader.
    //
    // THIS IS NOW THE FALLBACK, NOT THE ROUTE. CL_CloudVolume in
    // 1.6/Defs/ShaderTypeDefs/ShaderTypes.xml carries the same string and is what we normally load
    // through; this const is what ShaderLoader uses when that def is not in the database, and it is
    // still what the log messages below quote because the path is what a player can check against
    // the bundle. The duplication is deliberate and it is TESTED — ShaderTypeDefTests asserts the
    // const, the def's shaderPath and the asset name in BuildShaderBundles.cs are the same string,
    // because nothing at runtime notices if they drift, they just silently fall back to the baked atlas.
    public const string ShaderPath = "CelestialCloudVolume";

    // The name the .shader file DECLARES, which is the only thing that tells a loaded shader apart
    // from the one vanilla substitutes when loading fails.
    //
    // THE DEGRADE-TO-§25b PROMISE AT THE TOP OF THIS FILE WAS NOT KEPT, AND THIS IS THE REPAIR.
    // `ShaderDatabase.LoadShader` does not return null for a missing shader: it logs
    // "Could not load shader … Using default shader instead." and hands back the DEFAULT shader,
    // which is non-null and `isSupported`. So the old `VolumeShader != null` test passed, `Available`
    // said yes, and every sheet was drawn by a shader that knows nothing about `_Volume` — a flat
    // opaque quad the size of the sheet. That is the white slab, and it is worse than the fallback
    // this was supposed to have: a player with a missing or corrupt bundle got rectangles instead of
    // §25b's clouds, and no probe could tell because `cloud_volume_shader` read 1 throughout.
    //
    // Checking the DECLARED name rather than comparing against ShaderDatabase's default on purpose:
    // it asserts the shader we actually wanted, so it also catches a bundle that loaded some OTHER
    // shader, and it does not depend on which fallback vanilla happens to choose this version.
    public const string ShaderName = "CelestialLighting/CloudVolume";

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

    private static readonly Shader VolumeShader = ShaderLoader.Load(
        CelestialShaderDefOf.CL_CloudVolume, ShaderPath, "the volumetric cloud march");

    // Whether the shader that came back is OURS. See ShaderName: a failed load is not a null here,
    // it is vanilla's default shader wearing the same slot, so this is the only honest test.
    public static bool ShaderLoaded =>
        VolumeShader != null && VolumeShader.isSupported && VolumeShader.name == ShaderName;

    // THE BAKE RUNS ON A BACKGROUND THREAD AND THE MAIN THREAD NEVER WAITS FOR IT (§25e).
    //
    // What made that possible is a split that was already here: `FillBlobVolume` produces a plain
    // `byte[]` and touches no Unity type at all, and only three lines of what used to sit beside it
    // — the `new Texture3D`, `SetPixelData` and `Apply` now in `Upload` — have to be on Unity's
    // thread. That is the house rule about pure cores and thin adapters paying out rather than luck.
    // Measured in game, 328 ms of the bake moved and 9 ms of upload stayed.
    //
    // Started from the field initialiser, which runs inside [StaticConstructorOnStartup] on the main
    // thread during load. `ShaderDatabase.LoadShader` above it must stay there — it is a Unity call
    // — and it is also what decides whether to bake at all, which is why the ordering warning on the
    // property ids applies to this line too.
    private static readonly Task<Bake> BakeTask = StartBake();

    // Assigned once, on the main thread, by Upload(). Null until the bake has finished AND something
    // on the main thread has asked for it — see Available.
    private static Texture3D volume;

    private static Material[] sheetMats;

    // Set to true once the bake has been collected, successfully or not, so a failed bake is not
    // retried every frame for the rest of the session.
    private static bool uploadAttempted;

    // How long the bake took, in milliseconds, for the probe that reports it.
    //
    // STILL WORTH REPORTING NOW THAT NOBODY WAITS FOR IT, for two reasons. It is wall-clock across
    // however many cores CloudBake gave it, so it is the number that says whether the parallel split
    // is working on this machine — a value near the old serial one means it is not. And it bounds
    // how long after load the volumetric path stays unavailable, which is the window in which a live
    // A/B would capture §25b while believing it captured §25c.
    public static double BakeMilliseconds { get; private set; }

    // How long the main thread spent uploading the finished bake, in milliseconds.
    //
    // This is the only part of §25c's load cost the player can still feel, and it is a single frame
    // rather than a progress bar, so it is measured separately rather than folded into the bake: the
    // whole claim of §25e is that this number is what is left of the other one.
    public static double UploadMilliseconds { get; private set; }

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
    //
    // A PROPERTY WITH A SIDE EFFECT, DELIBERATELY, AND MAIN-THREAD ONLY. Since §25e the volume is
    // baked on a background thread, and the resulting bytes have to be handed to Unity from Unity's
    // own thread — so somebody on the main thread has to notice the bake finished. This is that
    // somebody, because it is already the one question every caller asks first, and the alternative
    // was a per-frame Harmony patch on a root update whose entire job would be to poll a bool.
    //
    // `Upload` is idempotent and returns immediately once it has run, so the cost of the check after
    // the first frame is a null comparison.
    public static bool Available
    {
        get
        {
            Upload();

            // BOTH the texture and the materials, not just the texture. Upload assigns `volume`
            // before `sheetMats`, so a throw in BuildMaterials between the two would leave this
            // reporting a usable path whose material array is null — and the caller's very next act
            // is to index it.
            return ShaderLoaded && volume != null && sheetMats != null;
        }
    }

    // Whether the background bake has finished, regardless of whether it has been uploaded yet.
    //
    // Separate from Available so a failure has two distinguishable shapes. A scenario that reads
    // `ready = 1, available = 0` is looking at a texture Unity refused; one that reads `0, 0` at the
    // same instant is simply early, and the fix is to wait rather than to go looking for a driver
    // bug. Without the split those are the same reading.
    public static bool BakeFinished => BakeTask != null && BakeTask.IsCompleted;

    // Sets everything that varies per sheet and returns the material to draw it with.
    //
    // `litColour` is what a fully sunlit part of this cloud looks like — the same colour §25b writes
    // into `material.color` — and `shadowRatio` the per-channel fraction of it that reaches a part
    // the beam never gets to. The shader interpolates between them, so the two of them together are
    // the entire colour model and neither is a modulation of a texture.
    //
    // §28: WRITES ONLY WHAT CHANGED. This used to make seven native `Material.Set*` calls per sheet
    // unconditionally, which at MaxSheets is 280 a frame and measured ~31 us — the largest single
    // item left in §28's sweep. Most of them rewrote a byte-for-byte identical value: a slot keeps
    // its material for the whole session, and thirteen of the sixteen numbers involved are decided
    // by the sheet's placement and deck, which change once a crossing rather than once a frame.
    //
    // So each slot now remembers what was last written to it and the write is skipped when the value
    // has not moved. The cadence split lives in CloudVolumeUniforms, and the reason it lives there
    // rather than here is that "this group does not depend on the sun or the light" is a claim about
    // arithmetic — testable offline, and false the moment somebody adds a sun term to Geometry
    // without noticing. See that file's header.
    //
    // NOT AN APPROXIMATION. The comparisons are exact float equality, and a miss writes exactly what
    // the old code wrote, so the material state handed to the GPU is identical in every frame rather
    // than merely close. A ΔE of 0.00 is the expected result here, not a weak one.
    public static Material Configure(
        int slot, in CloudSheetLayout.Placement placement, int atlasCells, int atlasSize,
        Color litColour, Color shadowColour,
        float sunAzimuthDegrees, float sunElevationDegrees)
    {
        Material material = sheetMats[slot];
        ref SheetState state = ref sheetStates[slot];

        // The deck's own vertical extent, which is what makes cirrus a flat sheet that barely
        // shadows itself and cumulus a tower that does. A2 records the same intent in its comments
        // and never wired it through; here each deck is already its own draw call, so it is free.
        float peakTexels = PeakTexels[placement.Deck];

        CloudVolumeUniforms.Geometry geometry = CloudVolumeUniforms.GeometryFor(
            CloudSheetLayout.BlobFor(placement, atlasCells), atlasCells, atlasSize,
            placement.FlipU, placement.FlipV,
            peakTexels, PeakTexels[0], CloudVolumeMath.VolumeLayers, PadSlices);

        // `!state.Written` rather than a sentinel Geometry value: there is no value of these
        // sixteen floats that cannot legitimately occur, so "nothing has been written to this slot
        // yet" has to be its own flag. It is also what makes the array safe to reuse if the
        // materials are ever rebuilt — see BuildMaterials, which clears it.
        if (!state.Written || !geometry.Equals(state.Geometry))
        {
            material.SetTextureScale(TextureId, new Vector2(geometry.ScaleU, geometry.ScaleV));
            material.SetTextureOffset(TextureId, new Vector2(geometry.OffsetU, geometry.OffsetV));

            material.SetVector(VolumeParamsId, new Vector4(
                geometry.AtlasSize, geometry.PaddedLayers, geometry.PeakTexels, geometry.LayerTexels));

            material.SetVector(CellBoundsId, new Vector4(
                geometry.MinX, geometry.MinY, geometry.MaxX, geometry.MaxY));

            // w is the LIGHT ray's coefficient, scaled by this deck's own thickness — see
            // CloudRaymarchMath.LightExtinctionFor. Without it a thin deck at a grazing sun is fully
            // self-occluded and renders at the ambient floor, which is the opposite of the sunset
            // §25b's deck windows exist to draw.
            material.SetVector(MarchParamsId, new Vector4(
                geometry.ViewExtinction, geometry.AmbientWrap,
                geometry.ShadowReach, geometry.LightExtinction));

            state.Geometry = geometry;
        }

        // Expected to change every frame — the sky moves — but compared anyway, because a paused
        // colony and a still sky are common and four float comparisons are cheaper than the
        // managed-to-native call they might avoid.
        if (!state.Written || !Same(litColour, state.Lit))
        {
            material.SetColor(ColorId, litColour);
            state.Lit = litColour;
        }

        if (!state.Written || !Same(shadowColour, state.Shadow))
        {
            material.SetColor(ShadowColorId, shadowColour);
            state.Shadow = shadowColour;
        }

        // The sun is the same for every sheet in the frame, so its trigonometry is done once and
        // reused — this used to run MaxSheets times a frame for one answer. Memoised on the exact
        // arguments rather than on FrameStamp: the pair IS the input, so an equal pair cannot
        // disagree, and it stays correct if a caller ever configures two maps in one frame.
        SunVectorFor(sunAzimuthDegrees, sunElevationDegrees, out float lu, out float lv, out float lh);

        CloudVolumeUniforms.SunDirection(
            lu, lv, lh, placement.FlipU, placement.FlipV,
            out float u, out float v, out float h);

        Vector4 sunDir = new Vector4(u, v, h, 0f);
        if (!state.Written || !Same(sunDir, state.SunDir))
        {
            material.SetVector(SunDirId, sunDir);
            state.SunDir = sunDir;
        }

        state.Written = true;
        return material;
    }

    // COMPONENTWISE, NOT `==`, and this is not a style preference. Unity's `==` on Color and Vector4
    // is APPROXIMATE: it subtracts and tests sqrMagnitude against 1e-10, so two values within about
    // 1e-5 compare equal. Used as a cache test that turns "close enough" into "do not write", which
    // is a different thing from "did not change" — a value drifting by less than the epsilon every
    // frame would be held at whatever was written when the cache was last missed. The error that
    // buys is far below anything visible, and it would still be a claim of exactness that was not
    // true, in a file whose whole argument is that the frames are identical rather than similar.
    //
    // Four float comparisons are also cheaper than the operator, which builds two Vector4s and a
    // subtraction to answer the same question.
    private static bool Same(in Color a, in Color b) =>
        a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    private static bool Same(in Vector4 a, in Vector4 b) =>
        a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

    // What was last written to one slot's material, so an unchanged value costs a compare instead of
    // a native setter. Grouped by cadence, not by shader property: see CloudVolumeUniforms.
    private struct SheetState
    {
        public bool Written;
        public CloudVolumeUniforms.Geometry Geometry;
        public Color Lit;
        public Color Shadow;
        public Vector4 SunDir;
    }

    private static SheetState[] sheetStates;

    // The sun's unit vector, computed once per distinct sun rather than once per sheet.
    private static float lastAzimuth = float.NaN;
    private static float lastElevation = float.NaN;
    private static float lastSunU;
    private static float lastSunV;
    private static float lastSunH;

    private static void SunVectorFor(
        float azimuthDegrees, float elevationDegrees, out float lu, out float lv, out float lh)
    {
        // NaN seeds, so the first call always misses: NaN != NaN, which is the one place that rule
        // is useful rather than a trap. A zeroed seed would collide with a genuine sun straight
        // overhead at due north.
        if (azimuthDegrees != lastAzimuth || elevationDegrees != lastElevation)
        {
            CloudRaymarchMath.SunVector(azimuthDegrees, elevationDegrees,
                out lastSunU, out lastSunV, out lastSunH);
            lastAzimuth = azimuthDegrees;
            lastElevation = elevationDegrees;
        }

        lu = lastSunU;
        lv = lastSunV;
        lh = lastSunH;
    }

    // The bake itself, and everything it returns. A plain array and a duration: no Unity type
    // crosses the thread boundary, which is what makes the boundary safe to draw here at all.
    private sealed class Bake
    {
        public byte[] Pixels;
        public double Milliseconds;
    }

    private static Task<Bake> StartBake()
    {
        if (!ShaderLoaded)
            return null;

        // Captured on the main thread and passed in, rather than read inside the task. Both are
        // compile-time constants today, but reading a `static readonly` of another
        // [StaticConstructorOnStartup] class from a worker thread is how a mod ends up running
        // somebody's static constructor off the main thread, and that fails in ways that do not name
        // this file.
        int size = CloudSheetOverlay.AtlasSize;
        int cells = CloudSheetOverlay.AtlasCells;
        int seed = CloudSheetOverlay.AtlasSeed;
        int layers = CloudVolumeMath.VolumeLayers;
        int octaves = CloudField.SheetOctaves;

        float[] rowCut = CloudDeckMath.PresentShapeCuts();
        float[] rowGain = CloudDeckMath.ShapeGains();
        float[] frequencyU = CloudDeckMath.FrequenciesU();
        float[] frequencyV = CloudDeckMath.FrequenciesV();

        // Task.Run rather than a raw Thread: this is a compute job that ends, the pool is already
        // warm by the time mods load, and a Task carries its own exception rather than taking the
        // process down with it — which for a cosmetic cloud renderer is the whole difference between
        // a fallback and a crash report.
        return Task.Run(() => RunBake(
            size, cells, seed, layers, octaves, rowCut, rowGain, frequencyU, frequencyV));
    }

    private static Bake RunBake(
        int size, int cells, int seed, int layers, int octaves,
        float[] rowCut, float[] rowGain, float[] frequencyU, float[] frequencyV)
    {
        Stopwatch watch = Stopwatch.StartNew();

        byte[] density = new byte[size * size * layers];

        // Split across cores by row — see CloudBake.Rows for why the row is the unit and
        // CloudVolumeMath.FillBlobVolumeRows for why bands cannot collide.
        //
        // NESTED PARALLELISM INSIDE A Task.Run IS FINE AND IS THE POINT. The outer task is one pool
        // thread that would otherwise sit on a 300 ms loop by itself; the inner Parallel.For fans
        // that loop out across the rest, so the volume is ready sooner and the window in which
        // Available answers false is shorter.
        CloudBake.Rows(size, (yStart, yEnd) => CloudVolumeMath.FillBlobVolumeRows(
            density, size, cells, layers, seed, octaves,
            rowCut, rowGain, frequencyU, frequencyV,
            // §25d's shaping, so the marched cloud is the same cloud the baked one is. The volume is
            // baked once at load, before any flag can be read, so it takes the §25d shape
            // unconditionally: §25c is itself opt-in, and a player who has turned the raymarch on has
            // not asked to see the pre-#144 silhouette through it.
            coreFraction: CloudField.PresentBlobCoreFraction,
            rimBite: CloudField.PresentRimBite,
            densityGamma: CloudSheetMath.PresenceAlphaGamma,
            yStart, yEnd));

        // TRANSPOSED on the way in. CloudVolumeMath stores a column's layers together, because both
        // of its own marches run down a column; a Texture3D wants slice-major, x fastest. Doing it
        // here rather than changing the layout keeps the CPU variants' access pattern intact — they
        // are still the fallback, and they are the ones that have to be fast on a CPU.
        //
        // Parallel over SLICES rather than rows, because a slice is what a destination row belongs
        // to: the write target is `(layer + 1) * size * size + y * size + x`, so two threads on two
        // layers are as disjoint as two threads on two bands were above.
        int padded = layers + PadSlices;
        byte[] pixels = new byte[size * size * padded];

        CloudBake.Rows(layers, (first, last) =>
        {
            for (int layer = first; layer < last; layer++)
            {
                int slice = (layer + 1) * size * size;
                for (int y = 0; y < size; y++)
                {
                    int row = slice + y * size;
                    for (int x = 0; x < size; x++)
                        pixels[row + x] = density[CloudVolumeMath.VolumeIndex(x, y, layer, size, layers)];
                }
            }
        });

        watch.Stop();
        return new Bake { Pixels = pixels, Milliseconds = watch.Elapsed.TotalMilliseconds };
    }

    // Hands the finished bake to Unity. MAIN THREAD ONLY, idempotent, and cheap after the first
    // successful call — see Available, which is the only caller and calls it every frame.
    private static void Upload()
    {
        if (uploadAttempted || BakeTask == null || !BakeTask.IsCompleted)
            return;

        uploadAttempted = true;

        // A bake that threw takes the whole path down to §25b rather than the game with it. There is
        // nothing here that should throw, which is exactly why it is worth naming the file in the
        // message if it ever does: the symptom otherwise is a sky that quietly renders one subsystem
        // older than the settings screen claims.
        if (BakeTask.IsFaulted)
        {
            Log.Error("[CelestialLighting] Cloud volume bake failed; falling back to the baked "
                + $"atlas (§25b). {BakeTask.Exception?.GetBaseException()}");
            return;
        }

        Stopwatch watch = Stopwatch.StartNew();

        Bake bake = BakeTask.Result;
        BakeMilliseconds = bake.Milliseconds;

        int size = CloudSheetOverlay.AtlasSize;
        int padded = CloudVolumeMath.VolumeLayers + PadSlices;

        Texture3D texture = new Texture3D(size, size, padded, TextureFormat.Alpha8, mipChain: false)
        {
            name = "CelestialLighting_CloudVolume",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 0,
        };

        texture.SetPixelData(bake.Pixels, 0);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

        volume = texture;
        sheetMats = BuildMaterials(texture);

        watch.Stop();
        UploadMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    private static Material[] BuildMaterials(Texture3D texture)
    {
        Material[] materials = new Material[CloudSheetLayout.MaxSheets];
        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = new Material(VolumeShader);
            materials[i].SetTexture(TextureId, texture);
        }

        // Cleared alongside the materials it describes, and assigned BEFORE `sheetMats` reaches
        // `Available` — a cache that outlived the materials it was a record of would have Configure
        // skip every write against a brand-new material, and a brand-new material has none of them.
        // Nothing rebuilds these today; this is here so that the day something does, it is not a
        // silent white slab.
        sheetStates = new SheetState[materials.Length];

        return materials;
    }
}
