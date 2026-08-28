namespace CelestialLighting.Probes;

// The arithmetic behind the door-swing instrument (issue #218): given every value one cell rendered
// at across a transition, how far did it leave the band its two endpoints define?
//
// WHY THIS PROPERTY AND NOT A DIFFERENCE. The defect under test is not "the light is wrong once the
// door is open" — it settles correctly, which is exactly why every scenario in this repo was blind to
// it. It is that the value OVERSHOOTS mid-transition and comes back: a section bakes vanilla's fresh
// glow against our stale coverage for a frame, and the region renders darker (opening) or brighter
// (closing) than either end of the swing. So the thing to assert is monotonicity — a value on its way
// from `first` to `last` should never be outside [first, last] — and the number to report is how far
// outside it went. Zero means the transition was monotone; anything else is the frame nobody could
// see before.
//
// SIGNLESS ON PURPOSE. Opening undershoots and closing overshoots, and a scenario should not have to
// know which case it is measuring to pin the same property. The worst departure in either direction
// is one number, and it is zero for a correct build either way.
//
// NO UnityEngine/Verse HERE — pure primitives in and out, per the repo rule, so the edge cases below
// are pinned by an offline NUnit sweep instead of inferred from a live capture that costs a game boot.
// It lives under Probes/ rather than beside the mod's own *Math.cs files because it is instrument
// arithmetic: the shipped DLL has no use for it and <Compile Remove="Probes/**"> keeps it out.
public static class SwingExcursionMath
{
    // How far a series left the closed interval between its endpoints, in the value's own units.
    //
    // The endpoints are NOT assumed ordered — `first` above `last` is the closing swing and is just
    // as valid a band — so the interval is built from whichever is smaller, not from the argument
    // order. Getting that wrong would report every closing swing as a defect.
    public static float Excursion(float first, float last, float min, float max)
    {
        float low = first < last ? first : last;
        float high = first < last ? last : first;

        // Two departures, one per side. A single frame can only be on one side of the band, but a
        // series can cross it, so both are measured and the worse of the two is the answer.
        float below = low - min;
        float above = max - high;

        float worst = below > above ? below : above;

        // Clamped at zero rather than returned signed: a series that stayed inside its band has a
        // negative "departure" on both sides, which is not a small excursion but no excursion.
        return worst > 0f ? worst : 0f;
    }

    // One cell's whole history across a transition, folded a sample at a time.
    //
    // A STRUCT RETURNED BY VALUE rather than a class mutated in place, so the fold is a pure function
    // an offline test can drive one sample at a time without a live map — the probe holds an array of
    // these and writes back `traces[i] = traces[i].Add(v)`.
    public struct Trace
    {
        // Samples accepted. Zero is the answer "this cell was never read", which is a different
        // statement from "this cell never moved" and must not be allowed to look like it.
        public int Count;

        // Samples REFUSED because the reader could not resolve them — a negative sentinel from the
        // mesh walk. Carried separately and reported as its own probe rather than silently dropped:
        // an instrument that cannot read its subject reports zero excursion, which is
        // indistinguishable from a clean build. The rejected count is what tells the two apart.
        public int Rejected;

        public float First;
        public float Last;
        public float Min;
        public float Max;

        public Trace Add(float sample)
        {
            Trace next = this;

            // The mesh walk answers a negative sentinel when the vertex layout is not the one it
            // expects. Folding that into Min would report a colossal undershoot on any frame the
            // section happened not to be built, which is the most convincing possible false positive.
            if (sample < 0f)
            {
                next.Rejected++;
                return next;
            }

            if (next.Count == 0)
            {
                next.First = sample;
                next.Min = sample;
                next.Max = sample;
            }
            else
            {
                next.Min = sample < next.Min ? sample : next.Min;
                next.Max = sample > next.Max ? sample : next.Max;
            }

            next.Last = sample;
            next.Count++;

            return next;
        }

        // Zero until there are two samples, because one sample is its own band and cannot depart from
        // it — reporting anything else would make an armed-but-never-swung scenario look defective.
        public float Excursion =>
            Count < 2 ? 0f : SwingExcursionMath.Excursion(First, Last, Min, Max);
    }
}
