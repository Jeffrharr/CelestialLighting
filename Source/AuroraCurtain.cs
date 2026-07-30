using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types, same rule and reason as AuroraMath.cs / AuroraNoise
// .cs: compiled into both Source (net481) and Tests (net8.0) via a linked <Compile Include>, so the
// shipped generator is the tested generator. AuroraCurtainOverlay is the thin adapter that owns the
// Texture2D/Material and hands this file a byte buffer to fill.
//
// This is §11a (DESIGN.md): the structured, moving aurora that §11's flat map-wide tint could never
// be. §11 lerps SkyTarget.colors toward one global hue, so at any instant the entire world is exactly
// one colour — turn that up and it reads as a green colour grade, not as an aurora. What makes a real
// aurora legible is structure, not saturation: several colours at once, in bands, drifting, with
// brightness varying across the sky. None of that is reachable from a single colour at any strength.
//
// ------------------------------------------------------------------------------------------------
// HOW THE RIBBONS ARE MADE, AND WHERE THE TECHNIQUE COMES FROM
//
// The band-forming trick is ported from the "Aurora Borealis" Godot shader by Klaufir
// (https://godotshaders.com/shader/aurora-borealis/), published under CC0 — public domain, so there
// is nothing to license and nothing to attribute beyond the courtesy of saying where it came from.
// Note this is a technique lifted from an unrelated CC0 Godot shader, NOT from any RimWorld mod:
// the clean-room constraint this whole mod is built under (see the repo CLAUDE.md) concerns Sjaandi's
// "Tilt Planet!", whose code was never available to anyone, and is untouched by this.
//
// The insight worth stealing is that you do not draw noise to get an aurora. You draw the CONTOUR
// WHERE TWO NOISE FIELDS ARE EQUAL. Take two fBm fields, subtract them, and push the difference
// through a smoothstep: the result is ~0 where field A wins, ~1 where B wins, and sweeps through 0.5
// along the boundary curve between them. Keying brightness on "how close to 0.5 is this" therefore
// lights up a thin, closed, wandering curve — a ribbon — rather than a cloud of blobs. Then a steep
// power chain (Amplify below) crushes everything that is not right on the boundary to black, which is
// what turns a soft gradient into a curtain with a defined edge.
//
// What we did NOT port is the raymarching. The Godot shader marches a ray through a unit cube to give
// the curtain volume in a 3D scene. RimWorld's camera is a fixed orthographic top-down, so there is
// no volume to march through and no parallax to sell — the march would cost ~100 samples per pixel to
// produce, from directly overhead, very nearly what one sample produces. Dropped.
//
// The other departure is the animation. The original scrolls both noise fields together, so the
// pattern translates rigidly and all its apparent writhing comes from the static domain warp the
// fields slide underneath. We scroll the two fields in DIFFERENT directions (see DriftX/DriftY
// below), which moves the boundary curve non-rigidly: the ribbons stretch, fold and pinch instead of
// merely sliding. That is the "undulate" half of what issue #42 asks for, and it is free — it is the
// same number of samples either way.
//
// ------------------------------------------------------------------------------------------------
// WHY THIS IS A CPU-GENERATED TEXTURE AND NOT A SHADER
//
// It would obviously be nicer as a fragment shader. RimWorld 1.6 is Unity 2022.3.35f1 and mods can
// load custom shaders out of an AssetBundle, so it is possible — but a bundle has to be built in the
// matching Unity Editor and shipped as a per-platform binary blob, and this repo ships no binary
// assets at all today. Against that, the effect is genuinely cheap on the CPU as long as it is not
// regenerated per frame at screen resolution, and it is not: the field is baked into a small tileable
// texture (see AuroraCurtainOverlay for the size and the incremental-refresh schedule), a few rows
// per frame, while the GPU does the smooth per-frame panning for free through a texture offset. The
// expensive axis is resolution, and an aurora is the one effect that loses nothing by being soft.
public static class AuroraCurtain
{
    // ---- Ribbon geometry ---------------------------------------------------------------------

    // Lattice periods of the two ribbon-forming fields, in cells across one tile of the texture.
    // Deliberately anisotropic: auroral arcs are long east-west bands, so the field must be
    // low-frequency across the map (few, wide features) and higher-frequency along it (the
    // undulations that give an arc its shape). Equal periods give round blobs, which read as clouds.
    public const int WaveXPeriod = 3;
    public const int WaveYPeriod = 7;

    // Two octaves, not three. Three was the initial guess and the first live run showed why it is
    // wrong: the ribbon is a CONTOUR of the difference field, so every extra octave only makes the
    // curve wander more, and rendered that read as thin wiry filaments — glowing worms — rather than as
    // curtains. Auroral arcs are sweeping, not scribbled. Dropping the octave also removes 2 of the 11
    // noise samples per pixel, so the look and the cost improved together.
    public const int WaveOctaves = 2;

    // Half-width of the smoothstep the field difference is pushed through, and therefore how thick the
    // ribbons are. This was 0.16, which the first live run rendered as hairlines: a contour is
    // infinitely thin by nature, and it is entirely this constant that gives it width. 0.30 is wide
    // enough to read as a curtain with soft edges. Push it much further and the bands merge into a wash,
    // which is the failure §11a exists to avoid.
    public const float Smoothness = 0.42f;

    // Amplitude of the static domain warp, in lattice units. This is the original shader's `distort`:
    // a third noise field displaces the sample coordinate of both ribbon fields identically, so the
    // ribbons acquire folds and hooks that no amount of octaves would produce (fBm is isotropic; a
    // warp is not). Static in map space on purpose — the fields drift underneath it, which is what
    // makes a fold stay put while the ribbon flows through it, exactly as a real curtain's rays do.
    public const float DistortAmplitude = 1.1f;

    // How fast the two fields slide past each other, in lattice units per GAME TICK. The pair is
    // deliberately not parallel and not equal: same-direction, same-speed motion is a rigid
    // translation of the boundary curve, which the material's own UV pan already provides and does
    // better. What this rate controls is how fast the ribbons change SHAPE. Slow — a real aurora's
    // large-scale form evolves over minutes, and anything fast enough to notice reads as a screensaver.
    //
    // Per tick rather than per second, so the curtain runs on game time exactly as vanilla's
    // WeatherOverlayDualPanner does: frozen while paused, faster at 2x/3x. 0.0006 ≈ 0.036 lattice
    // units per real second at normal speed (60 ticks/s).
    //
    // Ticks, specifically, rather than an accumulated delta-time: the live harness advances time by
    // jumping TicksGame (SetTime / Timelapse steps), and an accumulator would sit frozen through a
    // scripted jump, so a timelapse would record a static texture and prove nothing. Reading the tick
    // count directly means a jump moves the aurora, and it also makes any given tick reproducible,
    // which is what lets a scenario pin this at all.
    public const float DriftRate = 0.0006f;

    // Drift is wrapped to this many lattice units before it reaches the noise, for two reasons.
    //
    // PRECISION, which is the real one: drift grows without bound with TicksGame, and a float's
    // resolution degrades as it does. A century-old colony is ~2.2e9 ticks, i.e. a drift near 1.3e6,
    // where consecutive floats are ~0.06 lattice units apart — the field would visibly quantize into
    // steps. Wrapping keeps the value small and the increments smooth forever.
    //
    // WHY 840 EXACTLY, and why the wrap is seamless rather than a pop. Every drift term below is a
    // multiple of 1/20 of the base rate (0.35, 0.6, 0.2 for the ribbons, 0.25 for the envelope, 0.4
    // for the hue), so wrapping at 840 shifts each field by a multiple of 840/20 = 42 lattice units.
    // 42 = 2·3·7 is divisible by every base period in this file (3, 7, 2, 2, 2, 3), and the octave
    // doubling preserves the ratio — octave 2 shifts by twice as much against twice the period. So
    // every field lands on an exact multiple of its own period and the wrap is bit-identical, not a
    // discontinuity nobody would notice. If any period or coefficient here changes, this changes too.
    public const float DriftWrapCycle = 840f;

    // DriftWrapCycle expressed back in ticks: 840 / 0.0006. The ADAPTER must wrap the tick count with
    // this, in integer arithmetic, before it ever converts to float — which is a second, separate
    // precision problem from the one DriftWrapCycle solves.
    //
    // A float carries a 24-bit mantissa, so it stops being able to represent every integer past
    // 16,777,216 — about 278 in-game days at 60,000 ticks/day. Cast TicksGame directly on a colony
    // older than that and consecutive ticks start collapsing onto the same float, so the aurora would
    // advance in visible jerks and then, later still, stop advancing at all. Wrapping in ints first
    // keeps the value comfortably inside the exactly-representable range forever.
    //
    // AuroraCurtainTests pins DriftWrapTicks * DriftRate against DriftWrapCycle, and pins the field
    // itself as unchanged across the wrap, so this pair cannot drift out of agreement silently.
    public const int DriftWrapTicks = 1400000;

    // ---- Brightness envelope -----------------------------------------------------------------

    // A separate very-low-frequency field gates the ribbons in and out across the map, so the aurora
    // occupies part of the sky rather than all of it. Without this the ribbon field covers the whole
    // tile uniformly and the effect drifts back toward the "one thing happening everywhere" failure
    // §11 already has. Floor/range are chosen against fBm's distribution (which clusters near 0.5) to
    // leave roughly half the map lit and the rest genuinely empty.
    public const int EnvelopeXPeriod = 2;
    public const int EnvelopeYPeriod = 2;
    public const int EnvelopeOctaves = 2;
    public const float EnvelopeFloor = 0.22f;
    public const float EnvelopeRange = 0.34f;

    // ---- Palette -----------------------------------------------------------------------------

    // The third auroral emission line, alongside AuroraMath's two oxygen lines: molecular nitrogen
    // (N2+ first negative band, ~427.8 nm), the violet-blue fringe seen along the lower edge of an
    // active curtain. Same caveat as AuroraMath.OxygenGreen/OxygenRed — a single spectral line has no
    // exact sRGB triple, so this is an in-gamut approximation chosen to read as the right hue.
    public static readonly AuroraMath.Rgb NitrogenViolet = new AuroraMath.Rgb(0.35f, 0.30f, 1f);

    // The hue-band edges and the Amplify response curve moved to AuroraMath — see the "Shared curtain
    // primitives" section there for why. This field's palette still reads violet-green-red across them;
    // only their address changed.

    // The hue field's own scale. Coarser than the ribbons — the point is that DIFFERENT PARTS OF ONE
    // RIBBON are different colours, which needs hue to vary slower than the ribbon does. But not nearly
    // as coarse as first written: at 2x3 one hue lobe spanned ~100 map cells, which is WIDER THAN THE
    // VISIBLE WINDOW, so the whole screen sat inside a single lobe and rendered one flat green. That is
    // #42's own complaint reappearing one level down — several colours have to be visible *at once*, and
    // "at once" means within one screen, not within one map. 5x7 puts a lobe at ~40x28 cells, so a few
    // are always in frame. Offline preview (Tools/AuroraPreview) is what made this visible.
    public const int HueXPeriod = 5;
    public const int HueYPeriod = 7;
    public const int HueOctaves = 2;

    // How far the driver condition's own colour is allowed to pull the palette (see FillRows' tint
    // arguments). Deliberately partial.
    //
    // At 1 the curtain would be a single hue again, which is the failure §11a exists to fix — vanilla's
    // Aurora event cycles through one colour at a time, and adopting it wholesale would trade our bands
    // of several colours for a monochrome ribbon. At 0 the curtain ignores what §11 is doing underneath,
    // and during a vanilla Aurora event the wash and the ribbons visibly disagree about the hue. 0.3
    // keeps the palette's structure dominant while tying the two layers to the same night.
    public const float DriverTintWeight = 0.3f;

    // ---- Layer geometry ------------------------------------------------------------------------
    //
    // How the baked field is laid over the map, and how it moves. These live in the pure core rather
    // than in AuroraCurtainOverlay — where they are actually applied to a Material — for one reason:
    // Tools/AuroraPreview renders this field offline, and it can only be trusted to show what the game
    // shows if it reads the SAME numbers. Duplicating them into the previewer would produce a tool that
    // silently drifts from the thing it is previewing, which is worse than no tool.
    //
    // MeshPool.wholeMapPlane is 2000 world units across with its UVs multiplied by 200
    // (MeshMakerPlanes.NewWholeMapPlane), i.e. one texture repeat per ten cells at material scale 1.
    // Vanilla's weather textures are authored for that density; ours is not, so the adapter divides it
    // out. Left alone, the field repeats ~25 times across a 250-cell map and the ribbons read as rows of
    // tiny cursive squiggles — which is exactly what the first live run rendered.
    public const float PlaneRepeatsPerCell = 0.1f;

    // Cells spanned by one repeat of each layer, i.e. the on-screen feature size. 200/110 rather than
    // the 160/80 first tried: with the ribbons widened (see Smoothness) the field wants to be larger, so
    // one band spans a good fraction of the visible window instead of several crossing it at once.
    public const float Layer1CellsPerRepeat = 200f;
    public const float Layer2CellsPerRepeat = 110f;

    // Second layer's share of the alpha. Below the first so the near curtain dominates and the far one
    // reads as depth rather than as a competing aurora. Trimmed from 0.55 after the first live run,
    // where two near-equal layers crossing each other produced a double-line "outline" artifact.
    public const float Layer2Alpha = 0.4f;

    // Pan offsets per game tick, in texture-repeat units. Deliberately not parallel and not equal: two
    // layers translating together is a rigid shift of the whole sky, which reads as the camera moving
    // rather than the aurora moving. 6e-5/tick advances one repeat (200 cells) every ~16,700 ticks —
    // roughly 24 cells an hour, gentle enough to read as weather rather than a scrolling background.
    public const float Layer1PanU = 6e-5f;
    public const float Layer1PanV = 1.4e-5f;
    public const float Layer2PanU = -2.2e-4f;
    public const float Layer2PanV = -3e-5f;

    // Edge length of the baked texture. 96², down from the 128² first shipped to the live harness, on
    // the strength of that run's aurora_curtain_cost: 1423 microseconds per frame against ~240 predicted
    // from the offline .NET 8 benchmark. Mono is ~6x slower here than the JIT, which is the gap that
    // probe exists to close. 1.4 ms of a 16.6 ms frame is ~9%, more than an atmospheric background should
    // ask for even during a rare event; 96² is a 1.8x saving and costs nothing visible now that the
    // ribbons are deliberately soft and large. 64² remains in reserve at 4x.
    public const int Resolution = 96;

    // ---- Seeds ---------------------------------------------------------------------------------
    // Fixed rather than randomised per game. The aurora is a map-wide backdrop, not a generated
    // artifact a player inspects, and a fixed lattice means a scenario screenshot is reproducible —
    // which is the only way the live harness can pin this effect at all.
    private const int FieldASeed = 10007;
    private const int FieldBSeed = 20011;
    private const int WarpSeed = 30013;
    private const int HueSeed = 40009;
    private const int EnvelopeSeed = 50021;

    // Peak value Amplify can return for the intensity range Wave feeds it (see Intensity below,
    // whose maximum is 0.95). Dividing by it normalises the ribbon to [0, 1] so callers get a
    // meaningful alpha rather than "whatever the power chain happened to produce".
    private static readonly float PeakAmplified = AuroraMath.Amplify(0.95f);

    // Ribbon intensity at texture coordinate (u, v) ∈ [0,1)² at the given time, in [0, 1].
    //
    // u/v are texture coordinates rather than lattice coordinates so every field can pick its own
    // period (and therefore its own feature size) off the same tile, and so the whole function stays
    // seamless: each field wraps at its own period, and a sum of tile-periodic functions is
    // tile-periodic.
    public static float Wave(float u, float v, float time)
    {
        // The warp is sampled at the UNDRIFTED coordinate, which is what pins it to the map while the
        // fields slide through it. Both axes take the same displacement (the original samples the
        // noise texture's red channel into both components of a vec2); using two independent warps
        // instead decorrelates the axes and the folds turn into shear, which looks like a bad JPEG.
        float warp = (AuroraNoise.Value(u * WaveXPeriod, v * WaveYPeriod, WaveXPeriod, WaveYPeriod, WarpSeed)
            - 0.5f) * DistortAmplitude;

        float drift = Drift(time);

        float a = AuroraNoise.Fbm(
            u * WaveXPeriod + warp + drift,
            v * WaveYPeriod + warp - drift * 0.35f,
            WaveXPeriod, WaveYPeriod, FieldASeed, WaveOctaves);

        float b = AuroraNoise.Fbm(
            u * WaveXPeriod + warp - drift * 0.6f,
            v * WaveYPeriod + warp + drift * 0.2f,
            WaveXPeriod, WaveYPeriod, FieldBSeed, WaveOctaves);

        float boundary = Smoothstep(-Smoothness, Smoothness, a - b);
        return AuroraMath.Amplify(Intensity(boundary)) / PeakAmplified;
    }

    // Distance from the boundary, remapped so that "right on the contour" (boundary == 0.5) is
    // brightest. The 0.2 pedestal and 1.5 gain are the shader's; the pedestal is what survives
    // Amplify as the faint sky-wide glow mentioned above, and the gain saturates the curve slightly
    // before the contour so the ribbon has a flat-topped core instead of a single bright pixel.
    // Maximum is 0.2 + 0.75 = 0.95, which is what PeakAmplified normalises against.
    public static float Intensity(float boundary)
    {
        float closeness = (0.5f - Math.Abs(boundary - 0.5f)) * 1.5f;
        return 0.2f + Clamp01(closeness);
    }

    // Large-scale on/off gate, in [0, 1]: which parts of the sky have an aurora in them at all.
    public static float Envelope(float u, float v, float time)
    {
        // Drifts much slower than the ribbons do. Where the aurora IS should change over many
        // minutes; what it looks like changes over seconds.
        float drift = Drift(time) * 0.25f;

        float e = AuroraNoise.Fbm(
            u * EnvelopeXPeriod + drift,
            v * EnvelopeYPeriod,
            EnvelopeXPeriod, EnvelopeYPeriod, EnvelopeSeed, EnvelopeOctaves);

        return Clamp01((e - EnvelopeFloor) / EnvelopeRange);
    }

    // Hue selector in [0, 1] — see PaletteColor for what the number means.
    public static float HueField(float u, float v, float time)
    {
        float drift = Drift(time) * 0.4f;

        return AuroraNoise.Fbm(
            u * HueXPeriod,
            v * HueYPeriod + drift,
            HueXPeriod, HueYPeriod, HueSeed, HueOctaves);
    }

    // Violet → green → red across [0, 1], with green holding the whole middle. Piecewise-linear
    // rather than a smooth three-stop blend on purpose: blending violet and red through the middle
    // would desaturate the green core toward grey, and the green core is the one colour every aurora
    // has.
    public static AuroraMath.Rgb PaletteColor(float hue01)
    {
        float h = Clamp01(hue01);

        if (h < AuroraMath.HueGreenLow)
            return LerpRgb(NitrogenViolet, AuroraMath.OxygenGreen, h / AuroraMath.HueGreenLow);

        if (h > AuroraMath.HueGreenHigh)
            return LerpRgb(AuroraMath.OxygenGreen, AuroraMath.OxygenRed, (h - AuroraMath.HueGreenHigh) / (1f - AuroraMath.HueGreenHigh));

        return AuroraMath.OxygenGreen;
    }

    // Fills `rowCount` rows of an RGBA32 byte buffer (R,G,B,A per pixel, row-major, 4 bytes per
    // pixel) starting at `firstRow`.
    //
    // Filling a SLICE rather than the whole texture is the performance contract with the adapter:
    // regenerating the whole field every frame on Mono is not free, but regenerating a small band of
    // it is, and because the field is continuous in time the rows generated a few frames apart differ
    // by an invisible amount — there is no seam to see. Determinism in AuroraNoise is what makes that
    // safe: two rows baked in different frames must agree about the lattice they share. The adapter
    // owns the actual band size and cadence (AuroraCurtainOverlay), because that is a frame-budget
    // question, not a maths one.
    //
    // The tint arguments let §11's driver colour bleed into the palette: during vanilla's Aurora event
    // that is the colour the event is already cycling through, so the curtain and §11's underlying
    // flat tint agree about what colour tonight's aurora is instead of arguing. At tintWeight 0 the
    // palette stands alone (the solar-flare case, where the driver has no colour of its own worth
    // borrowing beyond AuroraMath's shimmer).
    public static void FillRows(
        byte[] rgba, int width, int height, int firstRow, int rowCount, float time,
        float tintR, float tintG, float tintB, float tintWeight)
    {
        if (rgba == null || width < 1 || height < 1)
            return;

        int lastRow = Math.Min(firstRow + rowCount, height);
        float invWidth = 1f / width;
        float invHeight = 1f / height;
        float tint = Clamp01(tintWeight);

        for (int y = Math.Max(firstRow, 0); y < lastRow; y++)
        {
            float v = y * invHeight;
            int rowBase = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                float u = x * invWidth;

                float alpha = Wave(u, v, time) * Envelope(u, v, time);
                AuroraMath.Rgb colour = PaletteColor(HueField(u, v, time));

                int i = rowBase + x * 4;
                rgba[i] = ToByte(Lerp(colour.R, tintR, tint));
                rgba[i + 1] = ToByte(Lerp(colour.G, tintG, tint));
                rgba[i + 2] = ToByte(Lerp(colour.B, tintB, tint));
                rgba[i + 3] = ToByte(alpha);
            }
        }
    }

    // Base drift distance for a tick count, wrapped into [0, DriftWrapCycle) — see DriftWrapCycle for
    // why the wrap exists and why it is invisible. Every field scales this by its own coefficient, so
    // wrapping once here is what keeps those coefficients all landing on exact period multiples.
    //
    // Negative times can't arise from TicksGame, but the modulo is made sign-safe anyway: a negative
    // drift would mirror the field rather than merely offsetting it, and a test that feeds -1 should
    // get a sane field rather than a subtly reflected one.
    public static float Drift(float time)
    {
        float drift = time * DriftRate % DriftWrapCycle;
        return drift < 0f ? drift + DriftWrapCycle : drift;
    }

    private static AuroraMath.Rgb LerpRgb(AuroraMath.Rgb a, AuroraMath.Rgb b, float t) =>
        new AuroraMath.Rgb(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    private static byte ToByte(float v) => (byte)(Clamp01(v) * 255f + 0.5f);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smoothstep(float edge0, float edge1, float v)
    {
        if (edge1 <= edge0)
            return v < edge0 ? 0f : 1f;

        float t = Clamp01((v - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }
}
