using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// CloudField.cs / CloudSheetLayout.cs. Compiled into both Source (net481, inside RimWorld) and Tests
// (net8.0, standalone) via a linked <Compile Include>, so the exact code that ships is the exact code
// under test.
//
// Subsystem 25b (DESIGN.md §25b): CLOUD VARIETIES, AND THE ALTITUDES THEY SIT AT.
//
// WHAT WAS MISSING. §25 drew one cloud type — one noise character, one opacity, one speed — with
// variety coming only from shape, mirroring and size. That is a sky of twelve of the same thing. It
// also meant §25's sunset colour ran on ONE handover window (CloudSheetMath.UnderlitFraction's old
// fixed [-1.5, 6] degrees), which is a guess standing in for a quantity the codebase already computes
// exactly: CloudUnderlightMath.ShadowEntryDepressionDegrees answers "at what solar depression does a
// deck at this height stop catching direct sun". A single window is only correct if every cloud is at
// one height.
//
// WHY THE TWO ARE ONE CHANGE RATHER THAN TWO. What makes a real sunset read as a sunset is not that
// the clouds turn orange — it is that they turn orange and then go out IN ORDER, bottom deck first.
// A low deck loses the sun about 1.2 degrees below the horizon; cirrus at 9.5 km holds it to about
// 3.1. So for a couple of minutes the low cumulus is already a flat grey mass while the cirrus above
// it is still burning. That sequencing is the effect, and it is unreachable without varieties,
// because it is a statement about two decks at once. Adding varieties and fixing the window are the
// same work seen from two ends.
//
// WHAT WEATHER ACTUALLY TELLS US, AND WHAT IT DOES NOT. WeatherDimmingMath.DefaultAltitudeMetres
// classifies a WeatherDef's deck height from its precipitation rates, 4000 m dry down to 1000 m
// raining, and its own header names the hole this file fills: "Neither rain/snow/sand rate nor
// palette opacity says anything about whether a *dry* deck is a low ceiling or high thin cloud."
// That is not a defect of the classifier — it is that a single number cannot describe a LAYERED sky,
// and a real dry sky is layered. So the classified altitude is read here as the sky's centre of mass
// and decomposed into a mixture over the three decks below; the one thing precipitation genuinely
// pins down (rain comes out of the low deck, so a raining sky is all low deck) is the anchor that
// makes the decomposition well-posed at one end.
//
// WHAT THIS DELIBERATELY DOES NOT DO YET, recorded rather than left to be discovered: §23b's
// underlit-ground lane still runs on the map's single classified altitude, so a cirrus sheet's
// bounced light on the GROUND still dies when the low deck's does, even though its own drawn colour
// now outlives it. Fixing that means moving §23b's window inside its per-sheet loop; it is a
// contained change and it is not made here, so that this one's live ΔE is attributable to the sheet.
public static class CloudDeckMath
{
    // How many decks there are. Three because that is how meteorology already divides the
    // troposphere (low / mid / high), because each one is a genuinely different-looking cloud, and
    // because it is the smallest number that can show the sequenced sunset above — two decks going
    // out one after the other reads as a glitch, three reads as depth.
    public const int DeckCount = 3;

    public const int LowDeck = 0;
    public const int MidDeck = 1;
    public const int HighDeck = 2;

    // One deck: where it sits and what it looks like. Plain floats and a plain struct so this file
    // stays pure and the table stays readable as a table.
    public readonly struct Deck
    {
        // Cloud base height in metres, the only field with a physical consequence rather than a
        // cosmetic one — it is what ShadowEntryDegrees turns into a sunset window.
        public readonly float AltitudeMetres;

        // How opaque this deck's sheets are, relative to the low deck's. Multiplied into
        // CloudSheetLayout.Placement.Alpha, which is what makes it apply to ALL THREE cloud lanes at
        // once: a cirrus sheet draws sheer (§25), casts a faint shadow (§23c) and bounces little
        // light (§23b) from one number, rather than from three subsystems being tuned to agree.
        public readonly float Opacity;

        // Size and speed relative to the low deck's. High cloud is drawn larger and faster because it
        // is both: cirrus forms in sheets tens of kilometres across and sits in the jet stream. The
        // speed spread is also the only parallax cue a top-down camera can carry — a fixed camera
        // height cannot give real parallax, but "the high stuff moves faster" is the part of it a
        // viewer actually reads.
        public readonly float SizeScale;
        public readonly float SpeedScale;

        // The shaping curve applied to the noise in CloudField.FillBlobAtlas. `Cut` is where the
        // noise starts counting as cloud and `Gain` how hard the rest is stretched to opaque: a high
        // cut with a high gain is a cloud with hard edges and solid middles (cumulus), a high cut
        // with a LOW gain is one that never fully fills in (cirrus, which you can see stars through).
        public readonly float ShapeCut;
        public readonly float ShapeGain;

        // How finely the noise is sampled across the blob, per axis, relative to the low deck. Two
        // numbers rather than one because the two things they express are genuinely different:
        //
        //   BOTH TOGETHER is element SIZE. Altocumulus is a mackerel sky — many small elements — and
        //   that is a frequency, not a contrast. Reaching for the gain instead (which is what this
        //   tried first) produces one solid mass with holes punched in it, which reads as less
        //   cloud-like than the deck below it rather than more.
        //
        //   THE RATIO BETWEEN THEM is streakiness. Cirrus is drawn out by the shear it sits in, so
        //   it wants long features across and thin ones along: a low u frequency and a high v one.
        //   Those streaks lie along the wind, which is the +x every sheet already travels, so u is
        //   the right axis for free.
        public readonly float FrequencyU;
        public readonly float FrequencyV;

        public Deck(
            float altitudeMetres, float opacity, float sizeScale, float speedScale,
            float shapeCut, float shapeGain, float frequencyU, float frequencyV)
        {
            AltitudeMetres = altitudeMetres;
            Opacity = opacity;
            SizeScale = sizeScale;
            SpeedScale = speedScale;
            ShapeCut = shapeCut;
            ShapeGain = shapeGain;
            FrequencyU = frequencyU;
            FrequencyV = frequencyV;
        }
    }

    // THE TABLE. Altitudes first, because they are the entries with a derivation rather than a taste
    // call behind them:
    //
    //   low   WeatherDimmingMath.PrecipitatingDeckDefaultAltitudeMetres, read rather than restated.
    //         The low deck is BY DEFINITION the one that rains, so the height the existing classifier
    //         already assigns a raining sky is this deck's height — and sharing the constant is what
    //         makes the all-low mixture below reproduce the classifier exactly instead of nearly.
    //   mid   5 km, the middle of meteorology's 2–7 km mid band.
    //   high  9.5 km, matching issue #88's "high cirrus (~10 km), the deck that lingers longest and
    //         reads as pink/magenta" — the row DESIGN.md §23 wrote down and nothing has been able to
    //         reach until now.
    //
    // The remaining columns are calibration, and are stated as such. Opacity is the one that carries:
    // cirrus is optically thin (you read print through it) and cumulus is not, so 0.55 against 1.0 is
    // a description rather than a knob. The shaping constants started from the single curve
    // FillBlobAtlas shipped with (cut 0.34, gain 3.2, frequency 1 — now the low deck's row,
    // unchanged, so a sky that draws only low cloud draws exactly what §25 drew before this file
    // existed).
    //
    // THE OTHER TWO ROWS WERE SET BY LOOKING, in Tools/CloudPreview, which renders this atlas offline
    // in about eighty milliseconds against a RimWorld boot. Both first guesses were wrong in ways the
    // numbers alone did not show: the mid row at gain 4.2 came out as one solid mass with holes in
    // it, less cloud-like than the deck below it, and the high row was so sheer (6% fill against the
    // low deck's 16%, then multiplied by its own opacity) that it would have been a subsystem nobody
    // could see. The fixes are in the two columns' own notes — frequency rather than gain for the
    // mackerel sky, and a cut that lets cirrus actually fill in.
    private static readonly Deck[] Decks =
    {
        new Deck(
            altitudeMetres: WeatherDimmingMath.PrecipitatingDeckDefaultAltitudeMetres,
            opacity: 1.00f, sizeScale: 1.00f, speedScale: 1.00f,
            shapeCut: 0.34f, shapeGain: 3.2f, frequencyU: 1.0f, frequencyV: 1.0f),
        new Deck(
            altitudeMetres: 5000f,
            opacity: 0.72f, sizeScale: 0.85f, speedScale: 1.35f,
            shapeCut: 0.38f, shapeGain: 3.4f, frequencyU: 2.2f, frequencyV: 2.2f),
        new Deck(
            altitudeMetres: 9500f,
            opacity: 0.55f, sizeScale: 1.30f, speedScale: 2.10f,
            shapeCut: 0.40f, shapeGain: 2.6f, frequencyU: 0.35f, frequencyV: 1.9f),
    };

    // Clamped rather than bounds-checked: every caller indexes this with something derived from
    // DeckFor, but the index also rides through Placement and out to three overlays, and a rendering
    // path is the wrong place to throw. An out-of-range deck draws as the low deck, which is the
    // behaviour §25 had before decks existed.
    public static Deck DeckAt(int index)
    {
        if (index < 0)
            return Decks[0];

        return index >= DeckCount ? Decks[DeckCount - 1] : Decks[index];
    }

    // The shaping columns, as the row-indexed arrays CloudField.FillBlobAtlas takes.
    //
    // THREE SEPARATE ARRAYS RATHER THAN THE DECK TABLE ITSELF, because CloudField is the primitive
    // this table is expressed in: it knows how to shape noise and deliberately does not know what a
    // cirrus is, so it takes floats. Allocated per call rather than cached — the only caller is an
    // atlas bake that happens once ever, in a static constructor, and a cached array handed out of a
    // static class is a mutable global waiting to be written to by the next caller.
    public static float[] ShapeCuts() => Column(0);

    public static float[] ShapeGains() => Column(1);

    public static float[] FrequenciesU() => Column(2);

    public static float[] FrequenciesV() => Column(3);

    private static float[] Column(int which)
    {
        float[] values = new float[DeckCount];
        for (int i = 0; i < DeckCount; i++)
        {
            values[i] = which switch
            {
                0 => Decks[i].ShapeCut,
                1 => Decks[i].ShapeGain,
                2 => Decks[i].FrequencyU,
                _ => Decks[i].FrequencyV,
            };
        }

        return values;
    }

    // The solar depression (degrees below the horizon, positive) at which this deck stops catching
    // direct sun.
    //
    // DELEGATED, NOT DERIVED. CloudUnderlightMath.ShadowEntryDepressionDegrees is already this
    // codebase's one answer to this question, itself deliberately built as the inverse of §19c's
    // Earth-shadow model rather than as a second approximation of it. A local arccos here would be a
    // third copy of the same geometry and exactly the drift DESIGN.md §20/§20d warns about — and it
    // would let §23's flat lane and §25's sheet disagree about when a sunset ends, which is the one
    // thing all this shared machinery exists to prevent.
    //
    //   low  (1.0 km)  1.014 degrees
    //   mid  (5.0 km)  2.267 degrees
    //   high (9.5 km)  3.126 degrees
    public static float ShadowEntryDegrees(int deck) =>
        CloudUnderlightMath.ShadowEntryDepressionDegrees(DeckAt(deck).AltitudeMetres);

    // --- The mixture ---

    // The three anchor mixtures the sky is interpolated between, as weights over (low, mid, high).
    //
    // ALL LOW WHEN IT IS RAINING, and this end is not a taste call: precipitation falls out of the
    // low deck, so a raining sky's drawn cloud is the low deck by definition. This anchor is what
    // makes the decomposition agree with the classifier it decomposes — feed it
    // PrecipitatingDeckDefaultAltitudeMetres and the mixture's own mean altitude comes back out as
    // exactly that number.
    private static readonly float[] RainingMix = { 1f, 0f, 0f };

    // A FAIR-WEATHER SKY, which is the common case and the one the sunset happens in (§22 invents a
    // fractional cloud amount precisely so a Clear evening has something in it). These three are
    // calibration. The reasoning behind them: fair-weather cumulus is what a dry sky mostly has, so
    // the low deck leads on COUNT; cirrus is common and comes in large sheets when it comes at all,
    // so it takes a real share of the count despite being the thinnest; the mid deck is the least
    // often present of the three. Note the count is not the visual share — the high deck's 0.42
    // opacity means a quarter of the sheets contribute well under a quarter of the sky's substance.
    private static readonly float[] FairMix = { 0.50f, 0.22f, 0.28f };

    // A DECLARED HIGH DECK. Only a WeatherDef that states an altitude through WeatherCloudDeck can
    // reach this end — the rain/snow/sand classifier tops out at DryDeckDefaultAltitudeMetres — and
    // stating 9.5 km or above is a def saying "this weather is high thin cloud", so it gets one.
    // Not (0, 0, 1): even a cirrus sky has something under it, and a mixture with a single member is
    // the uniform-slab failure §25 was rebuilt to escape, one deck up.
    private static readonly float[] HighMix = { 0f, 0.15f, 0.85f };

    // Fills `weights` (length DeckCount) with the deck mixture for a sky whose classified deck
    // altitude is `altitudeMetres` — i.e. what WeatherDimming.CloudAltitudeMetresFor returns.
    //
    // PIECEWISE LINEAR OVER THREE ANCHORS RATHER THAN A KERNEL. A Gaussian in log-altitude centred
    // on the classified value was tried first and is the obvious thing to reach for; it fails on the
    // case that matters most. DryDeckDefaultAltitudeMetres is 4000 m, which is not a claim that a dry
    // sky's clouds are at 4 km — it is a single number standing in for a layered sky — and a kernel
    // centred there hands almost everything to the 5 km mid deck, so a partly-cloudy Clear evening
    // comes out as altocumulus with no fair-weather cumulus in it at all. Anchoring on named mixtures
    // and interpolating between them keeps the classifier's two real endpoints while letting the
    // middle say the thing the classifier cannot.
    //
    // Monotone in altitude by construction, which matters more than it looks: the mixture is
    // re-evaluated every frame, and a weather transition slides the classified altitude continuously,
    // so a non-monotone mixture would have sheets converting back and forth across a boundary.
    public static void MixtureFor(float[] weights, float altitudeMetres)
    {
        if (weights == null || weights.Length < DeckCount)
            return;

        if (altitudeMetres <= WeatherDimmingMath.PrecipitatingDeckDefaultAltitudeMetres)
        {
            Copy(RainingMix, weights);
            return;
        }

        if (altitudeMetres >= Decks[HighDeck].AltitudeMetres)
        {
            Copy(HighMix, weights);
            return;
        }

        if (altitudeMetres <= WeatherDimmingMath.DryDeckDefaultAltitudeMetres)
        {
            float t = InverseLerp(
                WeatherDimmingMath.PrecipitatingDeckDefaultAltitudeMetres,
                WeatherDimmingMath.DryDeckDefaultAltitudeMetres,
                altitudeMetres);
            Blend(RainingMix, FairMix, t, weights);
            return;
        }

        float u = InverseLerp(
            WeatherDimmingMath.DryDeckDefaultAltitudeMetres,
            Decks[HighDeck].AltitudeMetres,
            altitudeMetres);
        Blend(FairMix, HighMix, u, weights);
    }

    // The unlayered sky: every sheet on the low deck, which is what §25 drew before §25b existed.
    //
    // Exists as its own named function rather than as a literal at the call site because it is the
    // BASELINE HALF OF AN A/B, not a degenerate case — CelestialLightingFeatures.CloudDeckVarieties
    // selects it, and the comparison is only honest if "off" is exactly the old sky rather than
    // approximately it.
    public static void SingleDeckMixture(float[] weights)
    {
        if (weights == null || weights.Length < DeckCount)
            return;

        Copy(RainingMix, weights);
    }

    // Which deck sheet `index` is on, given the mixture and a per-sheet uniform draw in [0, 1).
    //
    // THE DRAW IS COMPARED AGAINST CUMULATIVE WEIGHTS IN DECK ORDER, and the ordering is doing real
    // work rather than being the obvious way to write it. The mixture moves as the weather does, so
    // a sheet's deck can change under it. Because low comes before mid comes before high, a sheet
    // whose draw sits near a boundary converts LOW -> MID -> HIGH as the sky lifts, one sheet at a
    // time and always in that direction. The sky is seen to layer upward rather than to reshuffle,
    // which is the difference between weather and popping.
    //
    // A sheet's draw is stable for a whole crossing (see CloudSheetLayout.PlacementFor), so a cloud
    // does not change what kind of cloud it is while it is on screen for any reason other than the
    // weather genuinely moving underneath it.
    public static int DeckFor(float[] weights, float draw)
    {
        if (weights == null || weights.Length < DeckCount)
            return LowDeck;

        float total = 0f;
        for (int i = 0; i < DeckCount; i++)
            total += weights[i] > 0f ? weights[i] : 0f;

        if (!(total > 0f))
            return LowDeck;

        float target = Clamp01(draw) * total;
        float accumulated = 0f;

        for (int i = 0; i < DeckCount; i++)
        {
            accumulated += weights[i] > 0f ? weights[i] : 0f;
            if (target < accumulated)
                return i;
        }

        // Only reachable when draw rounds to exactly 1 against the accumulated sum; the top deck is
        // the right answer there, and returning it explicitly beats falling off the loop.
        return DeckCount - 1;
    }

    // The mixture's own mean cloud-base altitude, in metres. Exists to be asserted against rather
    // than to be drawn with: it is what lets the tests state the claim this whole file rests on —
    // that decomposing the classifier's single number into decks does not move the sky's centre of
    // mass at the two ends where the classifier is actually saying something.
    public static float MeanAltitudeMetres(float[] weights)
    {
        if (weights == null || weights.Length < DeckCount)
            return 0f;

        float total = 0f;
        float weighted = 0f;

        for (int i = 0; i < DeckCount; i++)
        {
            float weight = weights[i] > 0f ? weights[i] : 0f;
            total += weight;
            weighted += weight * Decks[i].AltitudeMetres;
        }

        return total > 0f ? weighted / total : 0f;
    }

    private static void Copy(float[] source, float[] destination)
    {
        for (int i = 0; i < DeckCount; i++)
            destination[i] = source[i];
    }

    private static void Blend(float[] a, float[] b, float t, float[] destination)
    {
        float mix = Clamp01(t);
        for (int i = 0; i < DeckCount; i++)
            destination[i] = a[i] + (b[i] - a[i]) * mix;
    }

    private static float InverseLerp(float a, float b, float v) =>
        MathF.Abs(b - a) < 1e-6f ? 0f : Clamp01((v - a) / (b - a));

    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
            return 0f;

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
