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

    // Per-column work for the sweep in progress, held across its slices. Rebuilt only when the cursor
    // returns to the bottom, which is what turns "19 noise samples per column, thirty-two times over"
    // into "19 samples per column".
    private AuroraCurtainHemRays.ColumnTable _columnTable;

    // How many quads to draw this frame, and which slot each one belongs to. Decided in Advance where
    // the map is in hand. Slots are not consecutive — slot 2 can be lit while 0, 1 and 3 are dark — so
    // the draw loop walks this list rather than counting up to a total.
    private int _liveCount;

    private readonly int[] _liveSlots = new int[AuroraSheetLayout.MaxSheets];

    // Scratch for AuroraDisplays.Resolve, allocated once and refilled every frame. The schedule fills a
    // caller-owned buffer precisely so this per-frame path allocates nothing.
    private readonly AuroraDisplay[] _live = new AuroraDisplay[AuroraDisplays.MaxLive];

    // The drift phase PlaceSheets resolved this frame, so DrawOverlay puts each quad where its material
    // state was prepared for. Recomputing it there would work but would silently desynchronise the
    // moment either call site changed.
    private float _driftPhase;

    // The tick this aurora began. Seeds the whole SEQUENCE of displays (AuroraDisplays salts it with
    // the slot and the generation), and doubles as the clock that sequence is measured from, so slot 0
    // spawns the moment the aurora lights rather than wherever a global tick count happens to fall.
    //
    // Captured on the 0 -> lit transition rather than read from the condition, because the overlay is
    // driven through Advance and has no handle on the driver. Reset when the aurora ends, so the NEXT
    // one runs a different sequence — which is the point.
    private int _eventSeed;

    private bool _eventLit;

    // This frame's placement per slot, indexed BY SLOT rather than packed, so DrawOverlay can look one
    // up straight from _liveSlots.
    private readonly AuroraSheetPlacement[] _placements =
        new AuroraSheetPlacement[AuroraSheetLayout.MaxSheets];

    // Where each slot's CURRENT display sits, in MAP coordinates, chosen once when that display spawned
    // and held for its whole life. Only the drift moves after that.
    //
    // The camera is never consulted. Reading it per frame made the patch a fraction OF THE VIEW, so it
    // slid along with the camera and read as glued to the screen rather than hanging over a piece of
    // ground.
    private readonly AuroraSheetPlacement[] _homes = new AuroraSheetPlacement[AuroraSheetLayout.MaxSheets];

    // The seed each cached home was placed for. A slot's display is replaced every few in-game hours,
    // and comparing seeds is how the overlay notices: same seed, reuse the home; different seed, the
    // slot has relit as a new display and needs a new spot. Less state than having the schedule
    // announce transitions, and it cannot drift out of step with the schedule because it IS the
    // schedule's own output.
    private readonly int[] _homeSeeds = new int[AuroraSheetLayout.MaxSheets];

    private readonly bool[] _homePlaced = new bool[AuroraSheetLayout.MaxSheets];

    // Map size the cached homes were placed against. Homes are in map cells, so a colony on a
    // differently sized map must not inherit them — the same reason the UV scales are set per frame
    // rather than once at allocation.
    private int _homeMapX;

    private int _homeMapZ;

    // The tick the field was last baked at. Sentinel is int.MinValue rather than 0, because tick 0 is a
    // real tick a scenario can genuinely sit on.
    private int _lastBakedTick = int.MinValue;

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
        {
            _eventLit = false;
            _liveCount = 0;
            ForgetHomes();
            return false;
        }

        if (!_eventLit)
        {
            _eventLit = true;
            _eventSeed = ticksGame;
        }

        AuroraFieldSpec spec = Spec;
        EnsureAllocated(spec);

        // Wrap in INTEGER arithmetic before the cast. A float cannot represent every integer past
        // ~16.7M, i.e. ~278 in-game days, so casting TicksGame raw would make an old colony's aurora
        // advance in jerks and eventually freeze. See AuroraCurtainHemRays.DriftWrapTicks.
        int wrapped = ticksGame % spec.DriftWrapTicks;

        // BAKE ON TICKS, DRAW ON FRAMES.
        //
        // This postfix runs from Map.MapUpdate, i.e. once per rendered FRAME, but everything the field
        // depends on is a function of TicksGame. So at any framerate above the tick rate the extra
        // frames were re-baking pixels identical to the ones already in the buffer — at ~350 fps that
        // is five of every six bakes thrown away — and while the game is PAUSED it regenerated forever
        // against a frozen clock, which is the worst case because a paused game is exactly when someone
        // is reading a tooltip and not expecting the mod to be busy.
        //
        // Profiling is what found this: the per-call cost was fine and the call COUNT was the problem.
        // Gating on the tick changing costs one comparison and removes the redundancy entirely, with no
        // visual difference of any kind — the field cannot change faster than game time does.
        //
        // Placement still updates every frame. It is a handful of float operations, it keeps alpha
        // responsive as the condition fades, and it is not what the profiler was pointing at.
        if (ticksGame != _lastBakedTick)
        {
            _lastBakedTick = ticksGame;
            Regenerate(spec, wrapped, tint, tintWeight);
        }

        PlaceSheets(spec, map, ticksGame, wrapped, strength);
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
        if (spec.CachesColumnTable)
            RegenerateFromTable(spec, time, tint, tintWeight);
        else
            spec.Fill(
                _pixels, spec.ResolutionX, spec.ResolutionY, _rowCursor, spec.RefreshRows, time,
                tint.r, tint.g, tint.b, tintWeight);

        _rowCursor += spec.RefreshRows;

        if (_rowCursor < spec.ResolutionY)
            return;

        // DOUBLE BUFFERED, with the CPU array as the back buffer and the GPU texture as the front. The
        // upload happens only when a sweep COMPLETES, never mid-sweep.
        //
        // Two reasons, and the first only became true when the sweep started pinning its time. The
        // buffer is baked a slice at a time, so mid-sweep it holds rows from the sweep in progress below
        // the cursor and rows from the PREVIOUS sweep above it. While every row carried its own instant
        // those two halves differed imperceptibly; now that a sweep is baked at one instant, the two
        // halves are a whole sweep apart in time and meet at a horizontal line. That line is faint at
        // normal speed and grows with game speed, which is the worst kind of artifact — invisible while
        // you are looking for it and obvious to a player at 3x.
        //
        // The second is plain bandwidth: this pushed ~147 KB to the GPU every frame to reveal six new
        // rows. Now it pushes the same 147 KB once every ResolutionY / RefreshRows frames.
        //
        // The cost is that the displayed field lags by up to one sweep — about half a second — which for
        // a field whose whole shape evolves over minutes is not a cost at all.
        _rowCursor = 0;
        Upload();
    }

    // Pushes the back buffer to the GPU.
    //
    // Also called once at allocation, and that call is not optional: a freshly constructed Texture2D's
    // contents are UNDEFINED, not zeroed. While the upload happened every frame that never mattered
    // because the first frame overwrote it; uploading only on sweep completion would otherwise leave
    // whatever the driver handed us on screen for the first thirty-odd frames of an aurora.
    private void Upload()
    {
        _texture.LoadRawTextureData(_pixels);
        _texture.Apply(false);
    }

    // Rebuilds the per-column table only at the start of a sweep, then reuses it for every slice of
    // that sweep.
    //
    // The `time` a sweep is baked at is therefore PINNED to the instant the sweep began, rather than
    // advancing row by row. That is a fix rather than a compromise: the rolling refresh already relies
    // on rows baked frames apart differing imperceptibly, and pinning makes the tile self-consistent
    // instead of shearing slightly in time between its bottom and its top — which it quietly did
    // before and which nobody had looked for.
    private void RegenerateFromTable(AuroraFieldSpec spec, float time, Color tint, float tintWeight)
    {
        if (_rowCursor == 0 || _columnTable == null)
            _columnTable = AuroraCurtainHemRays.BuildColumnTable(_columnTable, spec.ResolutionX, time);

        AuroraCurtainHemRays.FillRows(
            _pixels, _columnTable, spec.ResolutionX, spec.ResolutionY, _rowCursor, spec.RefreshRows,
            tint.r, tint.g, tint.b, tintWeight);
    }

    // Sets every drawn sheet's UV scale, pan offset and colour for this frame.
    //
    // The pan is computed from the tick count rather than accumulated per frame. Stateless, so it cannot
    // drift out of step with the field's own clock, and reproducible — the same tick always yields the
    // same pan, which is what lets a harness scenario screenshot this at all. The modulo keeps the
    // offset inside one texture repeat; wrapping there is exactly seamless because the field tiles.
    private void PlaceSheets(AuroraFieldSpec spec, Map map, int ticksGame, int wrapped, float strength)
    {
        _driftPhase = AuroraCurtainHemRays.Oscillate(wrapped * AuroraCurtainHemRays.DriftRate);

        if (spec.Sheets[0].SpansMapVertically)
        {
            PlaceSpanningSheets(spec, map, wrapped, strength);
            return;
        }

        ForgetHomesIfMapChanged(map);

        // The sequence, not a single display. Each lit slot carries its own seed and its own fade
        // envelope, so one patch dims out over here while another gathers over there — see
        // AuroraDisplays for why an aurora is a sequence rather than one patch lit for a day.
        _liveCount = AuroraDisplays.Resolve(_eventSeed, ticksGame - _eventSeed, _live);

        for (int i = 0; i < _liveCount; i++)
        {
            PlaceDisplay(_live[i], map, strength);
            _liveSlots[i] = _live[i].Slot;
        }
    }

    private void PlaceDisplay(AuroraDisplay display, Map map, float strength)
    {
        int slot = display.Slot;
        AuroraSheetPlacement home = HomeFor(display, map);

        // Mirrored displays drift the other way, so two patches on screen never move as one rigid
        // picture. UScale is already ±1 straight from the display's seed, so reusing it as the drift
        // sign costs nothing and cannot disagree with the mirroring it is derived from.
        AuroraSheetPlacement p = AuroraSheetLayout.WithDrift(home, _driftPhase * home.UScale);
        _placements[slot] = p;

        Material mat = Sheets[slot];
        mat.mainTextureScale = new Vector2(p.UScale, p.VScale);
        mat.mainTextureOffset = Vector2.zero;

        // Three factors, and all three have to be here rather than folded into one. `strength` is the
        // whole effect (night visibility and the condition's own hour-long ramp), `p.Alpha` is this
        // display's peak brightness, and `display.Alpha` is where it is in its own few-hour life.
        mat.color = new Color(1f, 1f, 1f, strength * p.Alpha * display.Alpha);
    }

    // The spot a slot's current display stands on, placed once when the display spawned and reused for
    // its whole life. A new seed means the slot has relit as a different display, which is the one
    // event that earns a fresh RandomPlacement.
    private AuroraSheetPlacement HomeFor(AuroraDisplay display, Map map)
    {
        int slot = display.Slot;

        if (_homePlaced[slot] && _homeSeeds[slot] == display.Seed)
            return _homes[slot];

        // Each slot draws inside its own horizontal band, which is what keeps concurrent displays from
        // landing on top of each other. Everything else about the placement is still free.
        _homes[slot] = AuroraSheetLayout.RandomPlacement(
            display.Seed, map.Size.x, map.Size.z, slot, AuroraDisplays.MaxLive);
        _homeSeeds[slot] = display.Seed;
        _homePlaced[slot] = true;
        return _homes[slot];
    }

    private void ForgetHomesIfMapChanged(Map map)
    {
        if (_homeMapX == map.Size.x && _homeMapZ == map.Size.z)
            return;

        _homeMapX = map.Size.x;
        _homeMapZ = map.Size.z;
        ForgetHomes();
    }

    private void ForgetHomes()
    {
        for (int i = 0; i < _homePlaced.Length; i++)
            _homePlaced[i] = false;
    }

    // The contour field's path, unchanged: whole-map planes that tile and pan their UVs. It has no
    // per-display lifecycle because its sheets are the sky rather than patches in it — every sheet is
    // always live, and slot i is placement i.
    private void PlaceSpanningSheets(AuroraFieldSpec spec, Map map, int wrapped, float strength)
    {
        _liveCount = AuroraSheetLayout.PlacementCount(spec, map.Size.z);

        for (int i = 0; i < _liveCount; i++)
        {
            AuroraSheetPlacement p = AuroraSheetLayout.Placement(spec, i, map.Size.x, map.Size.z);
            AuroraSheetSpec sheet = spec.Sheets[i];
            Material mat = Sheets[i];

            mat.mainTextureScale = new Vector2(p.UScale, p.VScale);
            mat.mainTextureOffset = new Vector2(
                (wrapped * sheet.PanU + p.UPhase) % 1f, wrapped * sheet.PanV % 1f);
            mat.color = new Color(1f, 1f, 1f, strength * p.Alpha);

            _placements[i] = p;
            _liveSlots[i] = i;
        }
    }

    private void EnsureAllocated(AuroraFieldSpec spec)
    {
        if (_texture != null && _pixels.Length == spec.PixelCount)
            return;

        _pixels = new byte[spec.PixelCount];

        // No mip chain: the plane is viewed at roughly one scale and mips would only cost memory and a
        // generation pass per upload. Repeat wrap is what makes a tiling sheet seamless; a bounded
        // patch never samples outside [0,1] anyway. Bilinear because softness is the intent here.
        _texture = new Texture2D(spec.ResolutionX, spec.ResolutionY, TextureFormat.RGBA32, mipChain: false)
        {
            name = "CelestialLighting_AuroraCurtain",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };

        for (int i = 0; i < Sheets.Length; i++)
            Sheets[i].mainTexture = _texture;

        _rowCursor = 0;

        // See Upload: a new Texture2D's contents are undefined, so the zeroed back buffer has to be
        // pushed once before anything is drawn. Zero alpha is invisible under additive blending, which
        // is what lets the field fill itself in over the first sweep without a priming pass.
        Upload();
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
    //
    // Vertex order, UVs and winding copied verbatim from decompiled Verse.MeshMakerPlanes.NewPlaneMesh
    // rather than reasoned out, because a quad wound the wrong way renders as nothing at all — no
    // error, no warning, no clue.
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
    // calls Advance instead. Implemented because SkyOverlay declares it abstract, not because it runs.
    public override void TickOverlay(Map map, float lerpFactor)
    {
    }

    public override void DrawOverlay(Map map)
    {
        if (_texture == null)
            return;

        // ABOVE FogOfWar, and every part of that is deliberate.
        //
        // Not Weather (31): it sits directly below LightingOverlay, so a weather-altitude aurora gets
        // multiplied by the night sky colour — and with §7a's pitch-black nights driving that overlay
        // toward opaque black, the aurora would be multiplied out of existence in exactly the conditions
        // it exists for.
        //
        // Not VisEffects (33) either, which is where this started. SectionLayer_IndoorMask draws between
        // Weather and VisEffects — measured, not assumed: with a block of RoofRockThick laid over the
        // map, vanilla rain's streaks vanish underneath it while our aurora carried straight across the
        // boundary at full strength. Sitting above that mask is what we WANT (an aurora is sky, and the
        // sky does not stop existing over a mountain), so VisEffects was right about roofs.
        //
        // It was wrong about fog. FogOfWar (34) would have covered the aurora over unexplored ground,
        // and an aurora is no more hidden by a player's ignorance of the terrain than by a roof. One
        // AltInc above FogOfWar puts us over the fog while staying inside its band — comfortably below
        // WorldClipper (36), which must keep drawing over us so the patch cannot spill past the map
        // edge. The whole change is a Y coordinate in the draw matrix; it costs nothing.
        float altitude = AltitudeLayer.FogOfWar.AltitudeFor() + Altitudes.AltInc;

        // Straight from what PlaceSheets resolved this frame. It used to re-derive the placement here
        // for every sheet but the first, which was both wasted work in a per-frame path and a second
        // copy of the layout call that could disagree with the material state already set against it.
        for (int i = 0; i < _liveCount; i++)
        {
            int slot = _liveSlots[i];
            AuroraSheetPlacement p = _placements[slot];

            // Separated by slot rather than by draw order, so two displays keep a stable front-to-back
            // relationship as others light and go dark around them. Under additive blending the order
            // does not change the result; it only has to be deterministic so nothing z-fights.
            Graphics.DrawMesh(
                SheetQuad,
                Matrix4x4.TRS(
                    new Vector3(p.CenterX, altitude + slot * 0.0015f, p.CenterZ),
                    Quaternion.identity,
                    new Vector3(p.Width, 1f, p.Height)),
                Sheets[slot],
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
        _liveCount = 0;

        // Homes are map coordinates, so they mean nothing once the map they were chosen on is gone.
        // Dropping them here is what stops a display reappearing at the same spot on a different
        // colony's map that happens to be the same size.
        ForgetHomes();
        SetOverlayColor(Color.clear);
    }

    public override string ToString() => "AuroraCurtainOverlay";
}
