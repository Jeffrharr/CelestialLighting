using System;

namespace CelestialLighting;

// When each of §11a's aurora displays is in the sky, and for how long. Pure — no UnityEngine, no
// Verse — so the schedule is covered by offline tests rather than by watching a night go by.
//
// ================================================================================================
// THE PROBLEM THIS SOLVES
//
// A display used to last exactly as long as the condition driving it. One patch was placed when the
// aurora began, held its position and size for the whole event, and faded out with it. That is not
// what an aurora does and, worse, it is not what an aurora does ON SCREEN: the player sees a single
// static patch of sky that happens to be lit for a day, and if it landed somewhere they never pan
// to, they see nothing at all and never learn there was an aurora.
//
// A real display is a SEQUENCE. Arcs brighten, hang for a while, dim, and are replaced by others
// elsewhere in the sky. The event is the weather; the displays are what you actually watch.
//
// ================================================================================================
// THE MODEL: FIXED SLOTS, EACH RUNNING ITS OWN CLOCK
//
// There are AuroraSheetLayout.MaxSheets slots. A slot is not a display — it is a channel that
// repeatedly spawns one. Slot s cycles with period CycleTicks[s]: lit for LifeTicks[s] at the start
// of each cycle, dark for the remainder, forever, offset by PhaseTicks[s] so the slots do not all
// begin together.
//
// Each pass through a slot's cycle is a GENERATION, and the generation number goes into the seed.
// So every time a slot relights it is a genuinely new display — new size, new position inside its
// band, new mirror, new alpha — rather than the same patch blinking. That is the whole point, and it
// is why the seed is (event, slot, generation) and not just (event, slot).
//
// WHY THE PERIODS ARE UGLY NUMBERS. 9700 / 11300 / 12700 / 16300 are pairwise non-harmonic (they are
// 100x four primes), so the four slots never fall back into step: the pattern's true repeat is
// 100 x 97 x 113 x 127 x 163, i.e. ~400 in-game YEARS. Round periods like 10000 and 20000 would
// resynchronise every few hours and the sky would visibly pulse.
//
// WHY THE DUTY CYCLES ARE ALL THE SAME (~0.65) WHILE THE PERIODS ARE NOT. Each slot owns a fixed
// horizontal band of the map (see AuroraSheetLayout's band overload), so a slot that were lit more of
// the time than its neighbours would put a permanent north-south brightness gradient on the map for
// no physical reason at all. Equal duty, unequal period: the bands are equally busy, and they are
// never busy together in a repeating way.
//
// The lifetimes that falls out to are 6300-10600 ticks, i.e. 2.5 to 4.2 in-game hours. That is the
// "few in-game hours" the display should own, and the spread means two displays that spawn together
// do not die together.
//
// The sky is completely empty about 2% of the time, in stretches of at most ~1.2 in-game hours.
// That is deliberate rather than tolerated — a display that never lulls is a filter, not an event —
// and §11's flat tint still colours the whole sky underneath, so a lull is a quiet sky rather than a
// black one. AuroraDisplaysTests pins both of those figures so a retune cannot quietly turn the
// aurora into a permanent green wash or into a mostly-empty one.
public readonly struct AuroraDisplay
{
    // Which slot this display belongs to. Doubles as the index into the overlay's material array and
    // the layout's band, so a display never has to be told where it may stand.
    public readonly int Slot;

    // Seeds size, position, mirroring and base alpha. Distinct for every (event, slot, generation).
    public readonly int Seed;

    // This display's own fade envelope, in [0, 1]: 0 at both ends of its life, 1 through the middle.
    // Multiplied by — not confused with — the condition's own ramp, which fades the whole effect.
    public readonly float Alpha;

    public AuroraDisplay(int slot, int seed, float alpha)
    {
        Slot = slot;
        Seed = seed;
        Alpha = alpha;
    }
}

public static class AuroraDisplays
{
    // One display per slot at most, so the ceiling is the material array's.
    public const int MaxLive = AuroraSheetLayout.MaxSheets;

    // How long a display takes to fade in, and again to fade out, inside its own lifetime. ~36
    // in-game minutes: slow enough to read as a display gathering rather than a light switch, short
    // enough that a 2.5-hour display still spends most of its life at full strength.
    //
    // Well under half the shortest lifetime, which matters: ConditionRampFactor combines the two ends
    // with Min, so a lifetime shorter than 2x this would never reach full alpha at all.
    public const float FadeTicks = 1500f;

    // See the header for why these are what they are. Index is the slot.
    private static readonly int[] CycleTicks = { 9700, 11300, 12700, 16300 };
    private static readonly int[] LifeTicks = { 6300, 7300, 8300, 10600 };
    private static readonly int[] PhaseTicks = { 0, 3700, 7300, 12100 };

    // Faintest a display may be at its own peak, as a fraction of full. Drawn from the display's seed
    // rather than from its slot: ranking alpha by slot would make the band nearest the south edge
    // permanently the brightest, which is a map-wide gradient nobody asked for. Seeded per display,
    // the bright one is somewhere different every time.
    public const float MinPeakAlpha = 0.55f;

    // Fills `into` with the displays lit right now and returns how many there are.
    //
    // Caller-allocated array on purpose: this runs once per frame while an aurora is up, and a
    // freshly allocated list per frame would put §11a back into the GC's path for no benefit —
    // MaxLive is a compile-time constant, so the buffer can live for the process.
    //
    // `ticksSinceStart` is measured from the tick the aurora lit, not from TicksGame, so the sequence
    // always begins at the beginning: slot 0 spawns immediately and the rest stagger in behind it.
    public static int Resolve(int eventSeed, int ticksSinceStart, AuroraDisplay[] into)
    {
        int age = ticksSinceStart < 0 ? 0 : ticksSinceStart;
        int count = 0;

        for (int slot = 0; slot < MaxLive; slot++)
        {
            float alpha = AlphaAt(slot, age);

            // A slot in its dark stretch contributes nothing — no material set, no draw call, no
            // placement work — rather than being drawn at zero alpha.
            if (alpha > 0f)
            {
                into[count] = new AuroraDisplay(slot, SeedFor(eventSeed, slot, GenerationAt(slot, age)), alpha);
                count++;
            }
        }

        return count;
    }

    // Which pass through its cycle slot `slot` is on at this age. Every increment is a brand new
    // display in that slot.
    public static int GenerationAt(int slot, int ticksSinceStart)
    {
        int t = OffsetAge(slot, ticksSinceStart);
        return t / CycleTicks[slot];
    }

    // The slot's own fade envelope at this age: 0 while it is in its dark stretch, ramping 0 -> 1 -> 0
    // across its lit stretch.
    //
    // Reuses AuroraMath.ConditionRampFactor rather than growing a second ramp of its own. The shape
    // wanted here — ease in over the first N, ease out over the last N, hold between, and peak lower
    // rather than exceed 1 if the window is too short for both — is exactly the shape that function
    // already has for conditions. Two copies of a ramp is two things to retune.
    public static float AlphaAt(int slot, int ticksSinceStart)
    {
        int t = OffsetAge(slot, ticksSinceStart);
        int intoCycle = t % CycleTicks[slot];
        int life = LifeTicks[slot];

        if (intoCycle >= life)
            return 0f;

        return AuroraMath.ConditionRampFactor(intoCycle, life - intoCycle, FadeTicks);
    }

    // The seed a given slot's given generation runs on.
    //
    // Salted with the event seed as well, so two auroras are not the same sequence of displays played
    // back — the whole reason the schedule is keyed on generation in the first place.
    public static int SeedFor(int eventSeed, int slot, int generation)
    {
        unchecked
        {
            uint h = (uint)(eventSeed * 374761393 + slot * 668265263 + generation * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (int)h;
        }
    }

    // Age with the slot's stagger folded in, kept non-negative so the integer division below floors
    // the way the model assumes. C# integer division truncates toward zero, so a negative age would
    // put two different ages on generation 0 and a display would relight in the same place.
    private static int OffsetAge(int slot, int ticksSinceStart)
    {
        int t = ticksSinceStart < 0 ? 0 : ticksSinceStart;
        return t + PhaseTicks[slot];
    }

    // Exposed for the tests, which have to be able to state the schedule's properties (equal duty,
    // bounded dark stretches) without hard-coding the tables a retune is allowed to move.
    public static int CycleTicksFor(int slot) => CycleTicks[slot];

    public static int LifeTicksFor(int slot) => LifeTicks[slot];

    public static int PhaseTicksFor(int slot) => PhaseTicks[slot];
}
