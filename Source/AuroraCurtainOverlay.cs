using UnityEngine;
using Verse;

namespace CelestialLighting;

// The thin, impure half of §11a (DESIGN.md §11a): owns the Texture2D, the materials, the mesh and the
// refresh schedule, and does nothing else. Every decision about what the aurora LOOKS like lives in the
// pure, offline-tested AuroraCurtainHemRays / AuroraNoise / AuroraFieldSpec / AuroraSheetLayout; this
// file is the boundary where those meet Unity.
//
// A Verse.SkyOverlay subclass because that is vanilla's own abstraction for an animated sky visual and
// it costs nothing to conform to — but note that we drive it ourselves rather than handing it to
// vanilla. See Patch_AuroraCurtainDraw for why the obvious hook (GameCondition.SkyOverlays) cannot
// work for a mod that does not own the condition.
//
// ================================================================================================
// WHY THIS NO LONGER USES MeshPool.wholeMapPlane
//
// It used to, through SkyOverlay's own DrawWorldOverlay helper. That plane is 2000 world units across
// with its UVs pre-multiplied by 200 (MeshMakerPlanes.NewWholeMapPlane), i.e. one texture repeat per
// ten cells, and the adapter divided that out to reach whatever feature size the field wanted.
//
// The trouble is that it tiles in BOTH axes, and the shipping field's v axis is not map-north — it is
// ALTITUDE UP THE CURTAIN. Repeating v does not tile a texture, it stamps the same three arcs, hems
// and all, over and over as the camera pans north: ~1.6 copies on one screen, 3.3 up a 250-cell map.
//
// So we draw our own quads instead, one per sheet, each showing exactly one vertical repeat (see
// AuroraSheetLayout). wholeMapPlane is also a SHARED STATIC MESH used by every vanilla weather overlay,
// so adjusting its UVs was never an option — it would have altered rain and snow for every mod in the
// load order.
//
// [StaticConstructorOnStartup] is mandatory, same as EaveShadeOverlay and NightDesaturationOverlay:
// `new Material(...)` and `new Mesh()` must happen on Unity's main thread, and the attribute is what
// guarantees the static initialiser runs there (at startup, after ShaderDatabase has loaded) instead of
// on whichever thread first happens to touch the type. Without it this is a latent crash.
[StaticConstructorOnStartup]
public sealed class AuroraCurtainOverlay : SkyOverlay
{
    // The field being drawn. Read once per frame rather than cached in a static, so a future settings
    // toggle takes effect without a reload; the property is a field lookup, not work.
    private static AuroraFieldSpec Spec => AuroraFieldRegistry.Active;

    // Re-exposed for AuroraCurtainCostProbe, so it times the size and slice the game actually bakes
    // rather than a copy of those numbers that can drift out of step.
    public static int ResolutionX => Spec.ResolutionX;

    public static int ResolutionY => Spec.ResolutionY;

    public static int RowsPerUpdate => Spec.RefreshRows;

    // Additive, not alpha-blended. An aurora emits light; it does not replace the sky behind it. Under
    // alpha blending a bright ribbon over a near-black night has to be almost opaque before it reads at
    // all, which pushes straight back toward the flat wash §11a exists to escape.
    //
    // MoteGlow specifically, from ShaderDatabase — the additive shader vanilla already uses for glowing
    // motes. This composites rendered pixels only: SkyTarget.glow, GlowGrid, plant growth, solar output
    // and pawn vision are all untouched, exactly as in §11's colour-only lane.
    //
    // MaxSheets of them, allocated up front because `new Material` must be on the main thread and the
    // sheet count is not known until a map is drawn. Unused ones cost a few hundred bytes and never
    // reach a draw call.
    private static readonly Material[] Sheets = BuildSheetMaterials();

    // One unit quad in the XZ plane, centred on the origin, scaled per sheet by the draw matrix.
    //
    // Vertex order, UVs and triangle winding are copied exactly from the decompiled
    // Verse.MeshMakerPlanes.NewPlaneMesh rather than reasoned out, because a quad wound the wrong way
    // renders as nothing at all with no error, no warning and no visual clue as to why.
    private static readonly Mesh SheetQuad = BuildSheetQuad();

    // One shared instance. The field is map-independent (it is a sky, not terrain) and the texture is
    // the single largest thing this subsystem allocates, so there is no reason for a second colony to
    // bake its own copy of the same sky. Material state that DOES depend on the map — the UV scales,
    // which are derived from Map.Size — is therefore set every frame in Advance rather than once at
    // allocation, so two colonies of different sizes cannot poison each other's sky.
    public static readonly AuroraCurtainOverlay Instance = new AuroraCurtainOverlay();

    // Both allocated on first actual use, never at startup. This is the single biggest performance
    // decision in §11a: a solar flare or Aurora event is rare, short and night-only, so for almost all
    // of a playthrough this subsystem is one null check per frame and these two stay null.
    //
    // They are NOT released when the event ends, which the plan originally called for and which is the
    // wrong trade. Together they are ~150 KB — genuinely nothing — while releasing them buys a
    // destroyed-texture-still-referenced bug and a realloc storm if an aurora sits flickering at the
    // night-visibility threshold. What actually costs is the per-frame regeneration, and that stops
    // dead the moment strength hits zero (see Advance).
    private Texture2D _texture;
    private byte[] _pixels;

    // Next row to regenerate; wraps at ResolutionY, so the refresh rolls continuously up the texture.
    private int _rowCursor;

    // How many quads to draw this frame, decided in Advance where the map is in hand.
    private int _liveSheets;

    private AuroraCurtainOverlay()
    {
    }

    // Advances the curtain for this frame and returns whether there is anything to draw.
    //
    // `strength` is AuroraConditions.CurrentCurtainStrength — already folded through night visibility
    // and the condition's fade ramp — and `tint` is §11's driver colour, so the ribbons and the flat
    // wash underneath them agree about what colour tonight's aurora is.
    //
    // Takes the map because sheet geometry depends on Map.Size, and this is the frame's one chance to
    // set material state. DrawOverlay is left to do nothing but issue draws.
    public bool Advance(Map map, int ticksGame, float strength, Color tint, float tintWeight)
    {
        // The zero-cost path. No allocation, no field work, no draw: for the overwhelming majority of
        // frames in a playthrough this is where the subsystem ends.
        if (strength <= 0f || map == null)
            return false;

        AuroraFieldSpec spec = Spec;
        EnsureAllocated(spec);

        // Wrap in INTEGER arithmetic before the cast. A float cannot represent every integer past
        // ~16.7M, i.e. ~278 in-game days, so casting TicksGame raw would make an old colony's aurora
        // advance in jerks and eventually freeze. See AuroraCurtainHemRays.DriftWrapTicks.
        int wrapped = ticksGame % spec.DriftWrapTicks;

        Regenerate(spec, wrapped, tint, tintWeight);
        PlaceSheets(spec, map, wrapped, strength);
        return true;
    }

    // Bakes one slice of field rows into the pixel buffer and uploads the buffer.
    //
    // NO PRIMING PASS, and that was a measured decision rather than a stylistic one. The obvious design
    // bakes the whole field on the aurora's first frame so nothing unbaked is ever displayed.
    // Benchmarked, a full-field bake is several milliseconds under the .NET 8 JIT and Mono is materially
    // slower — i.e. a dropped frame every single time an aurora begins, which is precisely the moment
    // the player is most likely to be looking up.
    //
    // It is also unnecessary, which is the part worth writing down. `new byte[]` is zero-filled, so an
    // unbaked row is RGBA(0,0,0,0), and under ADDITIVE blending zero contributes exactly nothing — an
    // unbaked row is invisible, not garbage. Combine that with the condition's own hour-long fade-in,
    // over which alpha climbs from zero anyway, and the field quietly fills itself in over the first few
    // dozen frames while the aurora is still far too faint to see. The hitch buys nothing, so it is not
    // paid.
    //
    // The corollary is that the texture is NOT re-zeroed between auroras: the second aurora of a
    // playthrough inherits the first one's field and is therefore fully formed immediately, which is
    // strictly better than starting blank again.
    //
    // LoadRawTextureData rather than SetPixels32: the pure core already writes RGBA bytes in exactly the
    // layout TextureFormat.RGBA32 wants, so this is a straight memcpy with no per-pixel Color32
    // marshalling in between. Apply(false) skips mip regeneration, which this texture does not have.
    private void Regenerate(AuroraFieldSpec spec, float time, Color tint, float tintWeight)
    {
        spec.Fill(
            _pixels, spec.ResolutionX, spec.ResolutionY, _rowCursor, spec.RefreshRows, time,
            tint.r, tint.g, tint.b, tintWeight);

        _rowCursor += spec.RefreshRows;
        if (_rowCursor >= spec.ResolutionY)
            _rowCursor = 0;

        _texture.LoadRawTextureData(_pixels);
        _texture.Apply(false);
    }

    // Sets every drawn sheet's UV scale, pan offset and colour for this frame.
    //
    // The pan is computed from the tick count rather than accumulated per frame. Stateless, so it cannot
    // drift out of step with the field's own clock, and reproducible — the same tick always yields the
    // same pan, which is what lets a harness scenario screenshot this at all. The modulo keeps the
    // offset inside one texture repeat; wrapping there is exactly seamless because the field tiles.
    private void PlaceSheets(AuroraFieldSpec spec, Map map, int wrapped, float strength)
    {
        _liveSheets = AuroraSheetLayout.PlacementCount(spec, map.Size.z);

        for (int i = 0; i < _liveSheets; i++)
        {
            AuroraSheetPlacement p = AuroraSheetLayout.Placement(spec, i, map.Size.x, map.Size.z);
            Material mat = Sheets[i];

            mat.mainTextureScale = new Vector2(p.UScale, p.VScale);

            // Material.mainTextureOffset, not SetTextureOffset with a Verse property ID: RimWorld's
            // ShaderPropertyIDs exposes its own custom `_Main_TexOffset` / `_Main_TexScale` properties,
            // not Unity's standard `_MainTex`, and MoteGlow is an ordinary `_MainTex` shader. This pair
            // of Unity properties addresses `_MainTex` by definition, so there is no name to get wrong.
            AuroraSheetSpec sheet = spec.Sheets[i < spec.Sheets.Length ? i : 0];
            mat.mainTextureOffset = new Vector2(
                (wrapped * sheet.PanU + p.UPhase) % 1f,
                wrapped * sheet.PanV % 1f);

            mat.color = new Color(1f, 1f, 1f, strength * p.Alpha);
        }
    }

    private void EnsureAllocated(AuroraFieldSpec spec)
    {
        if (_texture != null && _pixels.Length == spec.PixelCount)
            return;

        _pixels = new byte[spec.PixelCount];

        // No mip chain: the plane is viewed at roughly one scale and mips would only cost memory and a
        // generation pass per upload. Repeat wrap is what makes the pan seamless — Clamp would smear
        // the edge row across the map as soon as the offset moved off zero. Bilinear because softness
        // is the intent here, not a compromise.
        _texture = new Texture2D(spec.ResolutionX, spec.ResolutionY, TextureFormat.RGBA32, mipChain: false)
        {
            name = "CelestialLighting_AuroraCurtain",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };

        for (int i = 0; i < Sheets.Length; i++)
            Sheets[i].mainTexture = _texture;

        _rowCursor = 0;
    }

    private static Material[] BuildSheetMaterials()
    {
        Material[] mats = new Material[AuroraSheetLayout.MaxSheets];

        for (int i = 0; i < mats.Length; i++)
            mats[i] = new Material(ShaderDatabase.MoteGlow);

        return mats;
    }

    // A 1x1 quad in the XZ plane centred on the origin, so the draw matrix's scale reads directly as
    // the sheet's size in map cells.
    private static Mesh BuildSheetQuad()
    {
        Mesh mesh = new Mesh { name = "CelestialLighting_AuroraSheet" };

        mesh.vertices = new[]
        {
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0f, 0.5f),
            new Vector3(0.5f, 0f, 0.5f),
            new Vector3(0.5f, 0f, -0.5f),
        };

        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f),
        };

        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.RecalculateNormals();
        return mesh;
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
        // lighting overlay and still below FogOfWar, so the curtains glow through the dark while
        // unexplored map stays properly fogged.
        //
        // Each sheet is nudged a fraction of an altitude increment above the last purely so the draw
        // order is a stated fact. Additive blending is commutative, so it changes nothing visually.
        float altitude = AltitudeLayer.VisEffects.AltitudeFor();

        for (int i = 0; i < _liveSheets; i++)
        {
            AuroraSheetPlacement p = AuroraSheetLayout.Placement(Spec, i, map.Size.x, map.Size.z);

            Graphics.DrawMesh(
                SheetQuad,
                Matrix4x4.TRS(
                    new Vector3(p.CenterX, altitude + i * 0.0015f, p.CenterZ),
                    Quaternion.identity,
                    new Vector3(p.Width, 1f, p.Height)),
                Sheets[i],
                0);
        }
    }

    // Kept because SkyOverlay declares it abstract. Per-sheet alpha is set in PlaceSheets, which knows
    // each sheet's own share; a single colour for all of them would flatten that, so this only handles
    // the one call that matters — Reset's parking of every sheet.
    public override void SetOverlayColor(Color color)
    {
        for (int i = 0; i < Sheets.Length; i++)
            Sheets[i].color = color;
    }

    // Called when the sky state is being torn down or reset. Park EVERY sheet transparent — not just
    // the ones currently live, since the live count changes with map size and a stale material would
    // otherwise linger on screen — and send the refresh cursor back to the bottom.
    //
    // The baked field itself is left alone on purpose: it is a sky, not per-map state, so nothing about
    // it is invalidated by a map change, and keeping it means the next aurora starts fully formed
    // instead of filling in from blank.
    public override void Reset()
    {
        _rowCursor = 0;
        _liveSheets = 0;
        SetOverlayColor(Color.clear);
    }

    public override string ToString() => "AuroraCurtainOverlay";
}
