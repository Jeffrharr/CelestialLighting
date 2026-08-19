namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same rule and same reason as
// AuroraMath.cs: it is compiled into both Source (net481, inside RimWorld) and Tests (net8.0,
// standalone) through a linked <Compile Include>, so the shipped code is the tested code.
//
// This is the noise primitive behind §11a's aurora curtain (DESIGN.md §11a). Unity ships
// Mathf.PerlinNoise, and we deliberately do not use it: it is undocumented as to exact algorithm,
// has never been guaranteed stable across Unity versions, is not tileable, and — the disqualifier —
// lives in UnityEngine, which would drag the whole field generator out of the pure core and out of
// offline tests. A few dozen lines of value noise we own is cheaper than any of that.
//
// TILEABILITY IS THE WHOLE POINT. The curtain texture is drawn over the map and then UV-panned every
// frame to give the aurora its drift (see AuroraCurtainOverlay). A texture that does not wrap
// seamlessly shows a hard seam sweeping across the colony once per pan cycle — exactly the artifact
// that makes a scrolling-texture effect look cheap. So the lattice is indexed modulo an integer
// period in each axis: sampling at x and at x + xPeriod returns bit-identical values, by
// construction rather than by tuning.
//
// The two periods are separate because auroral arcs are not isotropic blobs — they are long bands.
// A caller that wants bands running east-west asks for a low period across the map (few, wide
// features) and a high one along it (many, narrow ones); with a single period the only shapes
// available are round.
public static class AuroraNoise
{
    // Integer hash → [0, 1). Three odd multipliers to decorrelate the axes and the seed, then an
    // xorshift-multiply avalanche so neighbouring lattice points don't produce visibly correlated
    // values (a plain multiply-and-truncate leaves diagonal banding in the field, which reads as a
    // grid once the ribbons are drawn on top of it).
    //
    // The mask to 24 bits keeps the result exactly representable in a float, so the same lattice
    // point returns the identical value on every call. That determinism is load-bearing, not
    // incidental: AuroraCurtain regenerates the field a few rows at a time across many frames, and
    // rows generated in different frames must agree about the lattice they share or the texture
    // tears along the slice boundary.
    private static float Hash01(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) * (1f / 16777216f);
        }
    }

    // Value noise on an integer lattice, bilinearly interpolated with a smootherstep fade, wrapping
    // at xPeriod / yPeriod.
    //
    // Smootherstep (6t⁵-15t⁴+10t³) rather than the classic smoothstep: value noise's giveaway is
    // visible creases along the lattice lines, because smoothstep matches the first derivative at the
    // cell boundary but not the second. Smootherstep matches both, and at three octaves that is the
    // difference between "soft cloud" and "quilted".
    public static float Value(float x, float y, int xPeriod, int yPeriod, int seed)
    {
        if (xPeriod < 1)
            xPeriod = 1;
        if (yPeriod < 1)
            yPeriod = 1;

        int xi = FloorToInt(x);
        int yi = FloorToInt(y);
        float fx = Fade(x - xi);
        float fy = Fade(y - yi);

        int x0 = Wrap(xi, xPeriod);
        int y0 = Wrap(yi, yPeriod);
        int x1 = Wrap(xi + 1, xPeriod);
        int y1 = Wrap(yi + 1, yPeriod);

        float v00 = Hash01(x0, y0, seed);
        float v10 = Hash01(x1, y0, seed);
        float v01 = Hash01(x0, y1, seed);
        float v11 = Hash01(x1, y1, seed);

        float bottom = Lerp(v00, v10, fx);
        float top = Lerp(v01, v11, fx);
        return Lerp(bottom, top, fy);
    }

    // Fractal sum of `octaves` Value layers, each at double the frequency and half the amplitude of
    // the last, normalised back into [0, 1].
    //
    // Each octave's lattice periods double alongside its frequency. That is what preserves the wrap:
    // octave 2 samples at twice the coordinate but wraps at twice the period, so it still repeats
    // over the same tile as octave 1. Halving the amplitude each time (rather than exposing a gain)
    // is the plain 1/f spectrum — an aurora's structure is dominated by its largest scale, and the
    // finer octaves exist only to keep the ribbon edges off the interpolation grid.
    public static float Fbm(float x, float y, int xPeriod, int yPeriod, int seed, int octaves)
    {
        if (octaves < 1)
            octaves = 1;

        float sum = 0f;
        float norm = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        int px = xPeriod < 1 ? 1 : xPeriod;
        int py = yPeriod < 1 ? 1 : yPeriod;

        for (int octave = 0; octave < octaves; octave++)
        {
            // Offsetting the seed per octave keeps the layers independent; without it every octave
            // hashes the same lattice points at the coarse scale and the sum is visibly biased toward
            // the base layer's extrema.
            sum += amplitude * Value(x * frequency, y * frequency, px, py, seed + octave * 1013);
            norm += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
            px *= 2;
            py *= 2;
        }

        return sum / norm;
    }

    // --- the 3-D pair, added for §25c's cloud volume (issue #144) ---
    //
    // WHY 3-D AT ALL, WHEN §11a AND §23b ARE FLAT. Those two draw fields that ARE flat: an auroral
    // contour and the light bounced off a deck are both surfaces, and a third axis would be an
    // invention. A cloud is not — it has an inside, and the thing §25c is trying to render is what
    // happens to a light ray THROUGH that inside when the sun is grazing. §25c's first attempt
    // compressed the volume to a height field and it read as flat, for a reason worth recording: the
    // density field it took its heights from is contrast-stretched and CLIPS at 1, so the whole core
    // of every blob came out at the same peak height and self-shadowed like a mesa rather than a
    // cloud. A real third axis is the fix; a cleverer compression of the same 2-D field is not.
    //
    // Same hash, same fade, same wrap, same per-octave seed offset as the 2-D pair above — this is
    // deliberately the same noise with one more axis, not a second noise.
    private static float Hash01(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1440662683 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) * (1f / 16777216f);
        }
    }

    // Trilinearly interpolated value noise, wrapping on all three axes.
    //
    // The z period is separate from the other two for the same reason x and y are separate from each
    // other, and it matters more here: a cloud deck is far wider than it is deep, so the vertical
    // axis wants FEW lattice cells across the whole thickness. Giving z the same period as x would
    // put the same number of features through 300 m of cirrus as across kilometres of sky, and the
    // result reads as static rather than as cloud.
    public static float Value(float x, float y, float z, int xPeriod, int yPeriod, int zPeriod, int seed)
    {
        if (xPeriod < 1)
            xPeriod = 1;
        if (yPeriod < 1)
            yPeriod = 1;
        if (zPeriod < 1)
            zPeriod = 1;

        int xi = FloorToInt(x);
        int yi = FloorToInt(y);
        int zi = FloorToInt(z);
        float fx = Fade(x - xi);
        float fy = Fade(y - yi);
        float fz = Fade(z - zi);

        int x0 = Wrap(xi, xPeriod);
        int y0 = Wrap(yi, yPeriod);
        int z0 = Wrap(zi, zPeriod);
        int x1 = Wrap(xi + 1, xPeriod);
        int y1 = Wrap(yi + 1, yPeriod);
        int z1 = Wrap(zi + 1, zPeriod);

        float v000 = Hash01(x0, y0, z0, seed);
        float v100 = Hash01(x1, y0, z0, seed);
        float v010 = Hash01(x0, y1, z0, seed);
        float v110 = Hash01(x1, y1, z0, seed);
        float v001 = Hash01(x0, y0, z1, seed);
        float v101 = Hash01(x1, y0, z1, seed);
        float v011 = Hash01(x0, y1, z1, seed);
        float v111 = Hash01(x1, y1, z1, seed);

        float near = Lerp(Lerp(v000, v100, fx), Lerp(v010, v110, fx), fy);
        float far = Lerp(Lerp(v001, v101, fx), Lerp(v011, v111, fx), fy);
        return Lerp(near, far, fz);
    }

    public static float Fbm(
        float x, float y, float z, int xPeriod, int yPeriod, int zPeriod, int seed, int octaves)
    {
        if (octaves < 1)
            octaves = 1;

        float sum = 0f;
        float norm = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        int px = xPeriod < 1 ? 1 : xPeriod;
        int py = yPeriod < 1 ? 1 : yPeriod;
        int pz = zPeriod < 1 ? 1 : zPeriod;

        for (int octave = 0; octave < octaves; octave++)
        {
            sum += amplitude * Value(
                x * frequency, y * frequency, z * frequency, px, py, pz, seed + octave * 1013);
            norm += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
            px *= 2;
            py *= 2;
            pz *= 2;
        }

        return sum / norm;
    }

    // Floor-toward-negative-infinity, which is what a lattice index needs: C#'s (int) cast truncates
    // toward zero, so a coordinate of -0.3 would land in cell 0 alongside +0.3 and mirror the field
    // about the origin. Sampling never goes negative from a texture coordinate, but the drift and
    // warp offsets are free to, and a mirrored field is the kind of bug that only shows up hours into
    // a live run.
    private static int FloorToInt(float v)
    {
        int i = (int)v;
        return v < i ? i - 1 : i;
    }

    // Always-positive modulo. C#'s % keeps the dividend's sign, which would index the hash outside
    // [0, period) for negative coordinates and break the wrap in exactly one direction.
    private static int Wrap(int v, int period)
    {
        int m = v % period;
        return m < 0 ? m + period : m;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
