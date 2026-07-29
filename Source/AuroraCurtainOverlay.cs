using UnityEngine;
using Verse;

namespace CelestialLighting;

// The thin, impure half of §11a (DESIGN.md §11a): owns the Texture2D, the materials and the refresh
// schedule, and does nothing else. Every decision about what the aurora LOOKS like lives in the pure,
// offline-tested AuroraCurtain / AuroraNoise; this file is the boundary where that field meets Unity.
//
// A Verse.SkyOverlay subclass because that is vanilla's own abstraction for an animated sky visual and
// it costs nothing to conform to — but note that we drive it ourselves rather than handing it to
// vanilla. See Patch_AuroraCurtainDraw for why the obvious hook (GameCondition.SkyOverlays) cannot
// work for a mod that does not own the condition.
//
// [StaticConstructorOnStartup] is mandatory, same as EaveShadeOverlay and NightDesaturationOverlay:
// `new Material(...)` must happen on Unity's main thread, and the attribute is what guarantees the
// static initialiser runs there (at startup, after ShaderDatabase has loaded) instead of on whichever
// thread first happens to touch the type. Without it this is a latent map-generation crash.
[StaticConstructorOnStartup]
public sealed class AuroraCurtainOverlay : SkyOverlay
{
    // Texture edge length, in pixels, of the field baked per map.
    //
    // 128² stretched over a whole map is very soft, and that is the point: an aurora is the one effect
    // that loses nothing to blur, so resolution is the cheapest possible thing to spend. Cost scales
    // with the square, so this is also the lever of last resort if the aurora_curtain_cost probe says
    // the budget is blown — 96² is a 1.8x saving and 64² a 4x one, both for no structural change.
    //
    // Public so AuroraCurtainCostProbe times the size we actually ship rather than a copy of the number
    // that can drift out of step with it — the same discipline AuroraConditions applies to the strength
    // the tint patches and their probe share.
    public const int Resolution = 128;

    // Rows regenerated per frame while an aurora is running. 6 of 128 means a full refresh every ~22
    // frames, roughly a third of a second at 60 fps.
    //
    // That is far slower than it sounds, because regeneration is NOT what makes the aurora move. The
    // GPU pans the texture every single frame (see the pan rates below), which supplies all the smooth
    // motion; regeneration only supplies change of SHAPE, and a real curtain's form evolves over
    // seconds to minutes. Rows baked ~22 frames apart therefore differ by an invisible amount, and
    // AuroraNoise's determinism guarantees the rows that overlap in the lattice agree exactly, so there
    // is no seam at the slice boundary.
    //
    // 6 rather than fewer, because measurement says smaller slices stop paying: benchmarked under the
    // .NET 8 JIT, 6 rows costs ~0.24 ms and 4 rows ~0.25 ms — indistinguishable, because at that size
    // the per-call setup dominates the per-pixel work. Going the other way is what costs: 16 rows is
    // ~0.60 ms and a whole field in one frame ~4.9 ms. So 6 sits at the knee of the curve. (Those are
    // JIT figures and RimWorld's Mono is slower; the aurora_curtain_cost probe carries the real one.)
    //
    // Public for the same reason as Resolution above.
    public const int RowsPerUpdate = 6;

    // UV offset per game tick for the two layers, and the second layer's tiling.
    //
    // TWO LAYERS, one texture. A single panning plane translates rigidly, and rigid translation of the
    // whole sky is the one motion that reads as "the camera is moving" rather than "the aurora is
    // moving". Drawing the same field twice — at different tilings, panning at different speeds in
    // different directions — makes the sum move non-uniformly across the frame, which is what sells
    // drift. It costs one extra DrawMesh and zero extra CPU field work, because it is the same texture.
    //
    // The second layer tiles 2x, so its ribbons are half the size, reading as a further-off curtain.
    // Integer tiling only: the texture is seamless, but only if it wraps on a whole number of tiles.
    //
    // Rates are per TICK, not per frame, so the drift obeys game speed and pause exactly as the field's
    // own evolution does. ~6e-5 UV/tick is about one map-width every 4.6 in-game hours — gentle enough
    // to read as weather rather than as a scrolling background.
    private const float Layer1PanU = 6e-5f;
    private const float Layer1PanV = 1.4e-5f;
    private const float Layer2PanU = -2.2e-4f;
    private const float Layer2PanV = -3e-5f;
    private const float Layer2Tiling = 2f;

    // Second layer's share of the alpha. Below the first so the near curtain stays dominant and the
    // far one reads as depth rather than as a competing aurora.
    private const float Layer2Alpha = 0.55f;

    // Additive, not alpha-blended. An aurora emits light; it does not replace the sky behind it. Under
    // alpha blending a bright ribbon over a near-black night has to be almost opaque before it reads at
    // all, which pushes straight back toward the flat wash §11a exists to escape.
    //
    // MoteGlow specifically, from ShaderDatabase — the additive shader vanilla already uses for glowing
    // motes. This composites rendered pixels only: SkyTarget.glow, GlowGrid, plant growth, solar output
    // and pawn vision are all untouched, exactly as in §11's colour-only lane.
    private static readonly Material Layer1 = new Material(ShaderDatabase.MoteGlow);
    private static readonly Material Layer2 = new Material(ShaderDatabase.MoteGlow);

    // One shared instance. The field is map-independent (it is a sky, not terrain) and the texture is
    // the single largest thing this subsystem allocates, so there is no reason for a second colony to
    // bake its own copy of the same sky.
    public static readonly AuroraCurtainOverlay Instance = new AuroraCurtainOverlay();

    // Both allocated on first actual use, never at startup. This is the single biggest performance
    // decision in §11a: a solar flare or Aurora event is rare, short and night-only, so for almost all
    // of a playthrough this subsystem is one null check per frame and these two stay null.
    //
    // They are NOT released when the event ends, which the plan originally called for and which is the
    // wrong trade. Together they are ~128 KB — genuinely nothing — while releasing them buys a
    // destroyed-texture-still-referenced bug and a realloc storm if an aurora sits flickering at the
    // night-visibility threshold. What actually costs is the per-frame regeneration, and that stops
    // dead the moment strength hits zero (see Advance).
    private Texture2D _texture;
    private byte[] _pixels;

    // Next row to regenerate; wraps at Resolution, so the refresh rolls continuously up the texture.
    private int _rowCursor;

    private AuroraCurtainOverlay()
    {
    }

    // Advances the curtain for this frame and returns whether there is anything to draw.
    //
    // `strength` is AuroraConditions.CurrentCurtainStrength — already folded through night visibility
    // and the condition's fade ramp — and `tint` is §11's driver colour, so the ribbons and the flat
    // wash underneath them agree about what colour tonight's aurora is.
    public bool Advance(int ticksGame, float strength, Color tint, float tintWeight)
    {
        // The zero-cost path. No allocation, no field work, no draw: for the overwhelming majority of
        // frames in a playthrough this is where the subsystem ends.
        if (strength <= 0f)
            return false;

        EnsureAllocated();

        // Wrap in INTEGER arithmetic before the cast. A float cannot represent every integer past
        // ~16.7M, i.e. ~278 in-game days, so casting TicksGame raw would make an old colony's aurora
        // advance in jerks and eventually freeze. See AuroraCurtain.DriftWrapTicks.
        float time = ticksGame % AuroraCurtain.DriftWrapTicks;

        Regenerate(time, tint, tintWeight);
        SetPan(ticksGame);
        SetOverlayColor(new Color(1f, 1f, 1f, strength));
        return true;
    }

    // Bakes field rows into the pixel buffer and uploads it.
    //
    // LoadRawTextureData rather than SetPixels32: the pure core already writes RGBA bytes in exactly
    // the layout TextureFormat.RGBA32 wants, so this is a straight memcpy of the buffer with no
    // per-pixel Color32 marshalling in between. Apply(false) skips mip regeneration, which the texture
    // does not have.
    //
    // The whole buffer uploads even though only a slice changed. That is deliberate: the upload is
    // 64 KB of contiguous memory, which is far cheaper than the bookkeeping for a partial-rect update,
    // and it keeps the buffer as the single source of truth for what the texture contains.
    // NO PRIMING PASS, deliberately, and this was a measured decision rather than a stylistic one.
    //
    // The obvious design bakes the whole field on the aurora's first frame so nothing unbaked is ever
    // displayed. Benchmarked, a full 128² bake is ~4.9 ms under the .NET 8 JIT and Mono is materially
    // slower — i.e. a dropped frame every single time an aurora begins, which is precisely the moment
    // the player is most likely to be looking.
    //
    // It is also unnecessary. `new byte[]` is zero-filled, so an unbaked row is RGBA(0,0,0,0), and under
    // ADDITIVE blending zero contributes exactly nothing — an unbaked row is invisible, not garbage.
    // Combine that with the condition's own hour-long fade-in, over which alpha climbs from zero, and
    // the field quietly fills itself in over the first ~22 frames while the aurora is still too faint to
    // see. The hitch buys nothing, so it is not paid.
    //
    // The corollary is that the texture is NOT re-zeroed between auroras: the second aurora of a
    // playthrough inherits the first one's field and is therefore fully formed immediately, which is
    // strictly better than starting blank again.
    private void Regenerate(float time, Color tint, float tintWeight)
    {
        AuroraCurtain.FillRows(
            _pixels, Resolution, Resolution, _rowCursor, RowsPerUpdate, time,
            tint.r, tint.g, tint.b, tintWeight);

        _rowCursor += RowsPerUpdate;
        if (_rowCursor >= Resolution)
            _rowCursor = 0;

        _texture.LoadRawTextureData(_pixels);
        _texture.Apply(false);
    }

    // Computed from the tick count rather than accumulated per frame. Stateless, so it cannot drift out
    // of step with the field's own clock, and reproducible — the same tick always yields the same pan,
    // which is what lets a harness scenario screenshot this at all. The modulo keeps the offset inside
    // one texture repeat; wrapping there is exactly seamless because the field is tileable.
    private void SetPan(int ticksGame)
    {
        int wrapped = ticksGame % AuroraCurtain.DriftWrapTicks;

        // Material.mainTextureOffset, not SetTextureOffset with a Verse property ID: RimWorld's
        // ShaderPropertyIDs exposes its own custom `_Main_TexOffset` / `_Main_TexScale` properties, not
        // Unity's standard `_MainTex`, and MoteGlow is an ordinary `_MainTex` shader. This pair of Unity
        // properties addresses `_MainTex` by definition, so there is no name to get wrong.
        Layer1.mainTextureOffset =
            new Vector2(wrapped * Layer1PanU % 1f, wrapped * Layer1PanV % 1f);

        Layer2.mainTextureOffset =
            new Vector2(wrapped * Layer2PanU % 1f, wrapped * Layer2PanV % 1f);
    }

    private void EnsureAllocated()
    {
        if (_texture != null)
            return;

        _pixels = new byte[Resolution * Resolution * 4];

        // No mip chain: the plane is viewed at roughly one scale and mips would only cost memory and a
        // generation pass per upload. Repeat wrap is what makes the pan seamless — Clamp would smear
        // the edge row across the map as soon as the offset moved off zero. Bilinear because softness
        // is the intent here, not a compromise.
        _texture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, mipChain: false)
        {
            name = "CelestialLighting_AuroraCurtain",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };

        Layer1.mainTexture = _texture;
        Layer2.mainTexture = _texture;
        Layer2.mainTextureScale = new Vector2(Layer2Tiling, Layer2Tiling);

        _rowCursor = 0;
    }

    // --- SkyOverlay contract ------------------------------------------------------------------

    // Vanilla's own tick entry point. Left empty on purpose: everything it would do needs the aurora's
    // current strength and driver colour, which vanilla has no way to hand us, so Patch_AuroraCurtainDraw
    // calls Advance instead. Implemented because SkyOverlay declares it abstract, not because it runs —
    // if a future RimWorld ever routes our overlay through SkyManager.UpdateOverlays, an empty tick is
    // the safe behaviour rather than a second, uncoordinated animation clock.
    public override void TickOverlay(Map map, float lerpFactor)
    {
    }

    public override void DrawOverlay(Map map)
    {
        if (_texture == null)
            return;

        // AltitudeLayer.VisEffects, not Weather, and that is load-bearing. Weather sits directly BELOW
        // LightingOverlay, so a weather-altitude aurora gets multiplied by the night sky colour — and
        // with §7a pitch-black nights driving that overlay toward opaque black, the aurora would be
        // multiplied out of existence in exactly the conditions it exists for. VisEffects is above the
        // lighting overlay and still below FogOfWar, so the ribbons glow through the dark while
        // unexplored map stays properly fogged.
        //
        // Two slightly different altitudes so the pair has a deterministic draw order. Additive
        // blending is commutative, so this changes nothing visually; it just means the ordering is a
        // stated fact rather than whatever the sort happens to do.
        float altitude = AltitudeLayer.VisEffects.AltitudeFor();
        DrawWorldOverlay(map, Layer1, altitude);
        DrawWorldOverlay(map, Layer2, altitude + 0.0015f);
    }

    public override void SetOverlayColor(Color color)
    {
        Layer1.color = color;

        Color far = color;
        far.a *= Layer2Alpha;
        Layer2.color = far;
    }

    // Called by vanilla when the sky state is being torn down or reset. Park both layers transparent so
    // nothing lingers on screen, and send the refresh cursor back to the bottom.
    //
    // The baked field itself is left alone on purpose — it is a sky, not per-map state, so there is
    // nothing about it that a map change invalidates, and keeping it means the next aurora starts fully
    // formed instead of filling in from blank.
    public override void Reset()
    {
        _rowCursor = 0;
        SetOverlayColor(Color.clear);
    }

    public override string ToString() => "AuroraCurtainOverlay";
}
