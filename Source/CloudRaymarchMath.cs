using System;
using System.Threading.Tasks;

namespace CelestialLighting;

// §25c variant D: the cloud volume integrated along the VIEW ray, one march per screen pixel
// (DESIGN.md §25c, issue #144).
//
// WHAT THIS IS, AGAINST THE THREE VARIANTS THAT CAME BEFORE IT. `CloudVolumeMath.ShadeBlobVolume`
// (variant A2) bakes a lit atlas: for each atlas texel it walks DOWN the column to find the one
// depth where the cloud's visible surface sits, lights that single point, and writes the result as
// an 8-bit modulation. That is a surface renderer wearing a volume as a hat. It is bounded by three
// things at once, and this file exists because all three are properties of the bake rather than of
// the model:
//
//   1. IT SHADES AT ATLAS RESOLUTION. A blob is 128 texels and is drawn across several hundred
//      screen pixels, so every self-shadow edge the march computes is magnified 3-4x on the way to
//      the screen. The shading is the one part of the cloud with fine structure and it is the part
//      being resampled the hardest.
//   2. IT LIGHTS ONE DEPTH PER PIXEL. A sunset cloud's whole character is that its top is in the
//      beam while its own bulk is not, and the interesting band is the gradient BETWEEN those —
//      light that entered the flank, crossed some depth of cloud, and came out dimmer and redder.
//      Picking a surface layer throws that band away and keeps its two endpoints.
//   3. IT CAN ONLY DARKEN. The bake is multiplied into `material.color` by ShaderDatabase.Transparent
//      and an 8-bit modulation has no value above 1, so the silver lining — the single term #144
//      names as the biggest contributor to "vivid" — can only be expressed as "everything else is
//      dimmer", never as a rim that is genuinely brighter than the cloud.
//
// So this integrates instead. Down the view ray, front to back: at every sample take the density,
// march THAT sample toward the sun for its own transmittance, and accumulate colour weighted by how
// much of the pixel that sample still owns. What comes out is a premultiplied radiance and an alpha
// that is a real optical depth, which fixes all three at once — it runs per pixel, every depth
// contributes, and a premultiplied output blended `One OneMinusSrcAlpha` has unbounded headroom.
//
// WHY §11a's REJECTION OF RAYMARCHING STILL DOES NOT APPLY, FOR A DIFFERENT REASON THAN §25c's.
// §11a dropped a raymarcher for the aurora because a fixed top-down orthographic camera gives no
// parallax to smear along the view ray. That is still true, and this file does not pretend
// otherwise: the view ray here is straight down and the march along it produces no parallax
// whatsoever. What it produces is the INTEGRAL — the alpha and the colour of a column of scattering
// medium — and that is visible from any angle, including straight down. The march is worth its cost
// for what it accumulates, not for the direction it accumulates along.
//
// THIS FILE IS THE REFERENCE FOR A SHADER, AND IS WRITTEN TO BE TRANSCRIBED. Every function below
// has a line-for-line counterpart in `Shaders/CelestialCloudVolume.shader`; the C# is what the
// offline tests and Tools/CloudPreview run, and the HLSL is what ships. Keeping them a transcription
// rather than two independent implementations is the only way the unit tests say anything at all
// about what the GPU draws, so a change here is a change there — see the shader's header for the
// two places where HLSL genuinely cannot say the same thing and what it says instead.
public static class CloudRaymarchMath
{
    // Samples down the view ray, through the deck's whole vertical extent.
    //
    // 24 rather than VolumeLayers' 20 so the view march is not phase-locked to the voxel grid.
    // Sampling a trilinearly filtered volume at exactly its own layer centres reads back the raw
    // voxels and reintroduces the stair-stepping the filtering exists to remove.
    public const int ViewSteps = 24;

    // Samples along the light ray, per view sample. This is the multiplier on the whole cost —
    // 24 x 8 texture fetches per pixel — so it is the first knob to turn if this is ever too slow.
    //
    // 8 is far coarser than A2's 24 and costs less than it looks like it should, because a view
    // march AVERAGES its light marches: 24 slightly-wrong transmittances stacked down a column land
    // much closer to the truth than one slightly-wrong transmittance at a surface. Undersampling
    // here shows up as banding in a single hard shadow edge, which is exactly the case the volume's
    // own softness hides.
    public const int LightSteps = 8;

    // How much longer each light-march segment is than the one before it.
    //
    // A UNIFORM march is the wrong shape for this problem, and the first cut of this file proved it
    // by rendering softer than the baked atlas it was meant to beat. At a grazing sun the ray has to
    // be followed most of a blob to find what is shadowing this pixel, so a uniform 8-step march
    // spreads its samples about ten texels apart — and the ten texels nearest the sample, which is
    // where a cloud's own lumps carve the shadow detail the eye reads as shape, get ONE sample
    // between them. The far field only needs to know roughly how much cloud is out there.
    //
    // So the segments grow. At 1.7 the first of eight covers about 1% of the span and the last
    // covers 40%, which puts four samples inside the first tenth of the ray without extending the
    // march or paying for another fetch.
    public const float LightStepGrowth = 1.7f;

    // How much light a sample gets from the SKY rather than from the sun, where nothing is in the
    // way of either — the same multiple-scattering floor as `CloudVolumeMath.AmbientWrap`, and set
    // deliberately lower for two compounding reasons.
    //
    // A2 applies its wrap once, at the surface, and then multiplies the result by a second
    // `ShadowFloor` in modulation space, so its darkest texel lands near 0.30 of the lit colour.
    // Here the wrap is the ONLY floor. It is also OCCLUDED rather than constant (see MarchPixel), so
    // the number is what an unshadowed sample gets, not what every sample gets, and a deep sample
    // gets a fraction of it.
    // Lowered from 0.35 for contrast. This is the floor a fully self-shadowed sample sits at, so it
    // sets directly how dark the dark parts get — and the bright-peaks-over-dark-body read is a
    // RATIO between the two, which a high floor compresses. At 0.35 the darkest a cloud could be was
    // just under a third of its lit colour; 0.18 doubles the range the shading has to work in.
    //
    // Not lower: this stands in for multiple scattering, which is why a real cloud's shadowed side
    // is grey rather than black, and zero here makes clouds read as lit STONES.
    public const float AmbientWrap = 0.18f;

    // The visible thickness of a deck, as a fraction of the blob radius, at the reference thickness.
    // Shared with A2 so the volume that was baked for one is the volume the other marches.
    // How tall a cloud stands, as a fraction of its own radius, for the RAYMARCH.
    //
    // NOT CloudVolumeMath's 0.35, and the divergence is the point. That constant was tuned for the
    // height-field variant, where every texel occludes along a 2-D surface and anything above ~0.5
    // smears the whole blob into shadow. A marched volume does not have that failure: occlusion is
    // resolved in three dimensions, so extra height buys relief instead of mud.
    //
    // AND WITHOUT THE HEIGHT THERE IS NOTHING TO SHADE. At 0.35 a low-deck cloud stands 22 texels on
    // a 128-texel blob — six times wider than tall, a pancake. The look this subsystem is for, a
    // convective top with its own bulk keeping the sun off everything below it, needs a shape with
    // somewhere for the light to fall FROM. 0.75 makes the cloud about as tall as it is broad, which
    // is roughly what a cumulus congestus is.
    public const float MaxHeightFraction = 0.75f;

    // Optical depth per unit density per texel of path, AT THE REFERENCE DECK.
    //
    // READ THE UNITS BEFORE COMPARING IT TO ANYTHING. This is no longer a coefficient any march uses
    // directly: ViewExtinctionFor and LightExtinctionFor both scale it by the deck's own thickness,
    // in opposite directions, so the numbers that reach the shader span roughly 0.07 to 0.47 across
    // the shipped deck table. A bare comparison against A2's 0.16, or against an earlier value of
    // this constant, is comparing two different quantities.
    //
    // 0.07 is CALIBRATED, not chosen: it is the value at which the marched column's alpha matches the
    // 2-D atlas's, deck for deck — measured at 1.03 / 0.95 / 0.98 low to high. That target is what
    // makes CelestialLightingFeatures.CloudVolume an honest A/B: with the two lanes drawing the same
    // amount of cloud, the flag switches the RENDERER and nothing else, and a difference in the frame
    // is a difference in shading rather than in how much sky got covered.
    //
    // It looks small against the 0.55 an earlier revision used, and the reason is the normalisation
    // above rather than a thinner cloud: the view coefficient is multiplied by the reference deck's
    // 48 texels, so the column depth it produces is what the old 0.55 produced over 22.
    public const float ExtinctionPerTexel = 0.07f;

    // The coefficient the LIGHT ray uses, as a fraction of the view ray's, for a deck of the
    // reference thickness. Scaled by the deck's own thickness at the call site.
    //
    // See the shader's _MarchParams note for why these are two numbers rather than one. In short: at
    // the density that makes a thin deck visible from above, a grazing sunset ray stays inside it far
    // enough to be fully occluded, so the deck that should be the last thing lit renders as the
    // darkest. One coefficient cannot be both, and this is the fiat stated out loud.
    public const float LightExtinctionScale = 1f;

    // The VIEW ray's coefficient for a deck standing `peakTexels` tall against a reference deck.
    //
    // NORMALISED BY THICKNESS, so a column's optical depth — and therefore its alpha — is the same
    // for every deck and is decided by the density field alone. That is what the 2-D atlas does: its
    // alpha is the shape, and a deck's thinness is expressed downstream by CloudDeckMath's per-deck
    // Opacity. The march did it twice, once through geometry and once through that opacity, so the
    // high deck came out at 0.61 of the atlas's alpha and the low deck at 1.46 — the same cloud
    // rendered a different thickness depending on which lane drew it.
    //
    // THIS IS ONLY SAFE BECAUSE THE LIGHT RAY HAS ITS OWN COEFFICIENT. Scaling extinction up for a
    // thin deck also drives its light march to full occlusion, which is what pins a cirrus sheet at
    // the ambient floor; that was measured, and it is why the two were split before this was done.
    // Clamped because the ratio grows without bound as a deck thins, and an unbounded coefficient
    // makes a wisp opaque.
    public static float ViewExtinctionFor(float peakTexels, float referencePeakTexels)
    {
        if (peakTexels <= 0f || referencePeakTexels <= 0f)
            return ExtinctionPerTexel;

        float scale = referencePeakTexels / peakTexels;
        return ExtinctionPerTexel * (scale > MaxViewExtinctionScale ? MaxViewExtinctionScale : scale);
    }

    // How far the normalisation above may push a thin deck's coefficient. 8 covers the shipped table
    // (the high deck needs 6.6) with a little room, and stops a deck thinner than any in it from
    // becoming a solid slab.
    public const float MaxViewExtinctionScale = 8f;

    // The light-ray coefficient for a deck standing `peakTexels` tall against a reference deck.
    public static float LightExtinctionFor(float peakTexels, float referencePeakTexels)
    {
        if (referencePeakTexels <= 0f)
            return ExtinctionPerTexel;

        float thickness = peakTexels / referencePeakTexels;
        return ExtinctionPerTexel * LightExtinctionScale * (thickness < 1f ? thickness : 1f);
    }

    // The 3-D unit vector pointing at the sun, in the volume's own texel space.
    //
    // Texel space is isotropic BY CONSTRUCTION — heights are carried in texels above the deck base,
    // the same unit the atlas's u and v are in — which is why a unit vector works here and why this
    // file needs none of A2's tan(elevation) bookkeeping. That bookkeeping is also where A2's
    // near-horizon guard came from: tan diverges at +-90 degrees and has to be floored by magnitude
    // with the sign kept. sin and cos do not, so a sun exactly at the zenith and a sun exactly at
    // the horizon are both ordinary cases here.
    public static void SunVector(
        float azimuthDegrees, float elevationDegrees, out float lu, out float lv, out float lh)
    {
        float azimuth = azimuthDegrees * (MathF.PI / 180f);
        float elevation = elevationDegrees * (MathF.PI / 180f);
        float cosE = MathF.Cos(elevation);

        lu = MathF.Sin(azimuth) * cosE;
        lv = MathF.Cos(azimuth) * cosE;
        lh = MathF.Sin(elevation);
    }

    // Trilinear sample of the baked volume, in atlas texel coordinates and texels above the base.
    //
    // Clamped to the sampling blob's OWN cell for the same reason A2's marches are: neighbouring
    // atlas cells are different clouds stored side by side, and letting one shadow the next puts a
    // hard seam down the middle of a sheet. Outside the cell, and above or below the volume, the
    // density is ZERO rather than clamped — a clamped edge would extend the outermost voxel to
    // infinity and wrap every cloud in an endless shadow-casting skirt.
    public static float Sample(
        byte[] volume, int atlasSize, int layers, int blobSize, int blobX, int blobY,
        float x, float y, float height, float peakTexels)
    {
        float layerTexels = peakTexels / layers;
        float layerCoord = (height / layerTexels) - 0.5f;

        int minX = blobX * blobSize;
        int minY = blobY * blobSize;
        int maxX = minX + blobSize - 1;
        int maxY = minY + blobSize - 1;

        // Sample positions sit at texel CENTRES, so a coordinate of 0.0 is the centre of texel 0 and
        // the fractional part is the weight toward the next texel up.
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int l0 = (int)MathF.Floor(layerCoord);

        float fx = x - x0;
        float fy = y - y0;
        float fl = layerCoord - l0;

        float c00 = Lerp(Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0, y0, l0),
                         Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0, y0, l0 + 1), fl);
        float c10 = Lerp(Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0 + 1, y0, l0),
                         Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0 + 1, y0, l0 + 1), fl);
        float c01 = Lerp(Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0, y0 + 1, l0),
                         Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0, y0 + 1, l0 + 1), fl);
        float c11 = Lerp(Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0 + 1, y0 + 1, l0),
                         Fetch(volume, atlasSize, layers, minX, minY, maxX, maxY, x0 + 1, y0 + 1, l0 + 1), fl);

        return Lerp(Lerp(c00, c10, fx), Lerp(c01, c11, fx), fy);
    }

    // Optical depth between a point inside the volume and the sun.
    //
    // The march length is whichever comes first: leaving the blob sideways, or climbing clear of the
    // deck's top. Below the horizon the ray DESCENDS, and the same expression covers it because the
    // distance to leave the volume vertically is symmetric — it is the descent that makes sunset
    // shadows run the length of the cloud, and it falls out rather than needing a branch.
    public static float LightDepth(
        byte[] volume, int atlasSize, int layers, int blobSize, int blobX, int blobY,
        float x, float y, float height, float peakTexels,
        float lu, float lv, float lh) =>
        LightDepth(volume, atlasSize, layers, blobSize, blobX, blobY, x, y, height, peakTexels,
            lu, lv, lh, ExtinctionPerTexel);

    // The same with the light coefficient given; see LightExtinctionFor.
    public static float LightDepth(
        byte[] volume, int atlasSize, int layers, int blobSize, int blobX, int blobY,
        float x, float y, float height, float peakTexels,
        float lu, float lv, float lh, float lightExtinction)
    {
        // Distance along the ray at which it clears the volume vertically. Floored well away from
        // zero so a sun within a hair of the horizon does not produce a span of millions of texels.
        float verticalSpan = peakTexels / MathF.Max(MathF.Abs(lh), 0.02f);

        // ... and the horizontal cap, which is what a grazing sun actually hits. Two thirds of a
        // blob: past that the ray has left the cloud that owns this pixel, and everything beyond is
        // a different cloud that the clamp in Sample zeroes anyway.
        float span = MathF.Min(blobSize * 0.667f, verticalSpan);

        // The first segment's length, chosen so that a geometric series of LightSteps terms starting
        // there sums to exactly the span. Written as the closed form rather than accumulated, so the
        // march covers the same distance whatever the growth constant is set to.
        float growthTotal = 1f;
        float term = 1f;
        for (int i = 1; i < LightSteps; i++)
        {
            term *= LightStepGrowth;
            growthTotal += term;
        }

        float step = span / growthTotal;
        float tau = 0f;
        float travelled = 0f;

        for (int i = 0; i < LightSteps; i++)
        {
            // Sample at each segment's MIDPOINT and weight by that segment's own length, so a long
            // far-field segment counts for the distance it covers rather than for one texel of it.
            float t = travelled + step * 0.5f;
            float density = Sample(
                volume, atlasSize, layers, blobSize, blobX, blobY,
                x + lu * t, y + lv * t, height + lh * t, peakTexels);

            tau += density * lightExtinction * step;
            travelled += step;
            step *= LightStepGrowth;
        }

        return tau;
    }

    // The whole march for one pixel: down the view ray, lighting every sample.
    //
    // `litR/G/B` is the colour of a voxel in full sun and `shadowR/G/B` the colour of one the beam
    // never reaches — both ABSOLUTE radiances, unlike A2's ratio-based modulation, because a
    // premultiplied output is a radiance and has nothing to be a modulation OF.
    //
    // The colour returned is PREMULTIPLIED: it is the light this column sends toward the camera, and
    // it is already scaled by how much of the pixel the column covers. Composite it as
    // `dst * (1 - a) + rgb`, which is `Blend One OneMinusSrcAlpha`. Dividing it back out by alpha to
    // get a "straight" colour is lossy at the wispy edges that matter most here — a rim texel can be
    // both very bright and very nearly transparent, and that is the silver lining, not an artefact.
    public static void MarchPixel(
        byte[] volume, int atlasSize, int layers, int blobSize, int blobX, int blobY,
        float x, float y, float peakTexels,
        float lu, float lv, float lh,
        float litR, float litG, float litB,
        float shadowR, float shadowG, float shadowB,
        out float r, out float g, out float b, out float a) =>
        MarchPixel(volume, atlasSize, layers, blobSize, blobX, blobY, x, y, peakTexels,
            lu, lv, lh, litR, litG, litB, shadowR, shadowG, shadowB,
            ExtinctionPerTexel, out r, out g, out b, out a);

    // The same with the light coefficient given; see LightExtinctionFor for why it is not the view
    // ray's.
    public static void MarchPixel(
        byte[] volume, int atlasSize, int layers, int blobSize, int blobX, int blobY,
        float x, float y, float peakTexels,
        float lu, float lv, float lh,
        float litR, float litG, float litB,
        float shadowR, float shadowG, float shadowB,
        float lightExtinction,
        out float r, out float g, out float b, out float a) =>
        MarchPixel(volume, atlasSize, layers, blobSize, blobX, blobY, x, y, peakTexels,
            lu, lv, lh, litR, litG, litB, shadowR, shadowG, shadowB,
            lightExtinction, ExtinctionPerTexel, out r, out g, out b, out a);

    // ...and with the view coefficient given too; see ViewExtinctionFor.
    public static void MarchPixel(
        byte[] volume, int atlasSize, int layers, int blobSize, int blobX, int blobY,
        float x, float y, float peakTexels,
        float lu, float lv, float lh,
        float litR, float litG, float litB,
        float shadowR, float shadowG, float shadowB,
        float lightExtinction, float viewExtinction,
        out float r, out float g, out float b, out float a)
    {
        float step = peakTexels / ViewSteps;

        // Front to back, so the running transmittance is the weight of everything still unoccluded.
        // The camera is above, so front is the TOP of the deck.
        float transmittance = 1f;
        r = 0f;
        g = 0f;
        b = 0f;

        for (int i = 0; i < ViewSteps; i++)
        {
            float height = peakTexels - (i + 0.5f) * step;
            float density = Sample(
                volume, atlasSize, layers, blobSize, blobX, blobY, x, y, height, peakTexels);

            // Empty space contributes nothing and, more usefully, costs no light march. Most of a
            // cloud's bounding box is empty, so this is where the eight-fold inner loop gets paid
            // for. Written as a guard rather than a `continue` per the house style, and it stays a
            // branch in the shader for the same reason it is one here.
            if (density > 0.002f)
            {
                float tau = LightDepth(
                    volume, atlasSize, layers, blobSize, blobX, blobY,
                    x, y, height, peakTexels, lu, lv, lh, lightExtinction);

                // Sky light, occluded by everything above this sample — and `transmittance` IS
                // that occlusion, already accumulated, for free.
                //
                // THIS IS THE ONE PLACE THE VIEW MARCH PAYS FOR ITSELF TWICE. A cloud is lit from
                // above by the whole sky as well as by the sun, and the camera is also above, so the
                // downward transmittance the compositing loop is already carrying is exactly the
                // fraction of sky a sample can see. A2 has no equivalent: it lights one surface
                // point, where the answer is trivially "all of it", so its ambient term has to be a
                // constant — which is why a self-shadowed A2 cloud goes uniformly grey while this
                // one keeps a gradient from its rim down into its core, at a cost of zero fetches.
                float ambient = AmbientWrap * transmittance;
                float direct = ambient + (1f - AmbientWrap) * MathF.Exp(-tau);

                // How much of the remaining light this segment absorbs. The exponential, rather than
                // `density * step`, because the segments are thick enough here that the linear
                // approximation would make a dense core noticeably too transparent.
                float absorbed = 1f - MathF.Exp(-density * viewExtinction * step);
                float weight = transmittance * absorbed;

                r += weight * (shadowR + (litR - shadowR) * direct);
                g += weight * (shadowG + (litG - shadowG) * direct);
                b += weight * (shadowB + (litB - shadowB) * direct);

                transmittance *= 1f - absorbed;
            }
        }

        a = 1f - transmittance;
    }

    // Renders the whole atlas grid at `supersample` pixels per texel, into an RGBA buffer of
    // (atlasSize * supersample) squared.
    //
    // This exists for Tools/CloudPreview and for the offline tests, NOT as a fallback path: at one
    // sample per screen pixel this is 24 x 8 volume fetches per pixel on the CPU, which is a shader's
    // workload and nothing else. A machine whose GPU cannot run the shader gets A2's baked atlas
    // instead — see CloudSheetOverlay for that gate.
    //
    // `rowPeakTexels` is the deck row's vertical extent; null gives every row the reference height.
    // A2 has the same per-row intent recorded in its comments but never wired the parameter through,
    // so a cirrus row there stands as tall as a cumulus one. Here it is a real argument, because on
    // the GPU it is a per-draw uniform and each deck is already its own draw call.
    public static void Render(
        byte[] rgba, byte[] volume, int atlasSize, int blobsPerAxis, int layers, int supersample,
        float sunAzimuthDegrees, float sunElevationDegrees,
        float litR, float litG, float litB,
        float shadowR, float shadowG, float shadowB,
        float[] rowPeakTexels, bool inVacuum)
    {
        if (rgba == null || volume == null || atlasSize <= 0 || blobsPerAxis <= 0 || layers <= 0)
            return;

        int size = atlasSize * supersample;

        // A vacuum map has no cloud and no atmosphere to light one, per the Vacuum.cs convention:
        // early return at the top, before any arithmetic, writing fully transparent.
        if (inVacuum)
        {
            Array.Clear(rgba, 0, size * size * 4);
            return;
        }

        SunVector(sunAzimuthDegrees, sunElevationDegrees, out float lu, out float lv, out float lh);

        int blobSize = atlasSize / blobsPerAxis;
        float defaultPeak = blobSize * 0.5f * MaxHeightFraction;

        // Rows in parallel. This is the OFFLINE renderer — the shipped mod never calls it — and one
        // still at the magnification the game draws at is 1.3 million marches, which is a minute of
        // one core and three seconds of eight. Rows are fully independent, so there is nothing to
        // synchronise and nothing that makes the output depend on the scheduling.
        Parallel.For(0, size, py =>
        {
            float y = (py + 0.5f) / supersample - 0.5f;
            int blobY = Math.Min((int)(y / blobSize), blobsPerAxis - 1);
            float peakTexels = rowPeakTexels != null && blobY < rowPeakTexels.Length
                ? rowPeakTexels[blobY]
                : defaultPeak;

            for (int px = 0; px < size; px++)
            {
                float x = (px + 0.5f) / supersample - 0.5f;
                int blobX = Math.Min((int)(x / blobSize), blobsPerAxis - 1);

                MarchPixel(
                    volume, atlasSize, layers, blobSize, blobX, blobY, x, y, peakTexels,
                    lu, lv, lh, litR, litG, litB, shadowR, shadowG, shadowB,
                    LightExtinctionFor(peakTexels, defaultPeak),
                    ViewExtinctionFor(peakTexels, defaultPeak),
                    out float r, out float g, out float b, out float a);

                int o = (py * size + px) * 4;
                rgba[o + 0] = ToByte(r);
                rgba[o + 1] = ToByte(g);
                rgba[o + 2] = ToByte(b);
                rgba[o + 3] = ToByte(a);
            }
        });
    }

    // The peak height in texels for each deck, from the deck thicknesses A2 already tabulates.
    public static float[] RowPeakTexels(int blobSize, int deckCount)
    {
        float[] peaks = new float[deckCount];
        float reference = blobSize * 0.5f * MaxHeightFraction;

        for (int deck = 0; deck < deckCount; deck++)
        {
            float ratio = CloudVolumeMath.ThicknessMetres(deck) / CloudVolumeMath.ThicknessReferenceMetres;
            peaks[deck] = reference * ratio;
        }

        return peaks;
    }

    private static float Fetch(
        byte[] volume, int atlasSize, int layers,
        int minX, int minY, int maxX, int maxY, int x, int y, int layer)
    {
        bool inside = x >= minX && x <= maxX && y >= minY && y <= maxY
            && layer >= 0 && layer < layers;

        return inside
            ? volume[CloudVolumeMath.VolumeIndex(x, y, layer, atlasSize, layers)] * (1f / 255f)
            : 0f;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static byte ToByte(float value)
    {
        float clamped = value < 0f ? 0f : (value > 1f ? 1f : value);
        return (byte)(int)(clamped * 255f + 0.5f);
    }
}
