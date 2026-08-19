using System;

namespace CelestialLighting.Tests;

// Offline coverage for §25c variant D, the cloud volume integrated along the view ray
// (Source/CloudRaymarchMath.cs, issue #144), linked into this project via <Compile Include> so these
// exercise the exact file that ships and the exact file the shader is transcribed from.
//
// WHAT THESE CAN AND CANNOT PROVE. The arithmetic here is a reference implementation of an HLSL
// fragment shader, and nothing offline can run HLSL. So these pin the parts that are ARITHMETIC —
// that the light march covers the span it claims to, that the alpha is the closed-form optical
// depth, that an empty volume draws nothing, that the vacuum gate fires before anything else — and
// the shader is kept a line-for-line transcription so that pinning them here means something about
// what the GPU does. Anything about how it LOOKS is Tools/CloudPreview's job and then the live
// harness's; see DESIGN.md §25c for the measured comparison against variant A2.
[TestFixture]
public class CloudRaymarchMathTests
{
    private const float Tolerance = 1e-4f;

    // A small atlas: one blob, 8x8 texels, 4 layers. Small enough to reason about by hand and to
    // fill exhaustively in a test, and the marches do not care how big the atlas is.
    private const int AtlasSize = 8;
    private const int BlobsPerAxis = 1;
    private const int Layers = 4;

    private static byte[] UniformVolume(byte density)
    {
        byte[] volume = new byte[AtlasSize * AtlasSize * Layers];
        for (int i = 0; i < volume.Length; i++)
            volume[i] = density;

        return volume;
    }

    private static float Peak => AtlasSize * 0.5f * CloudRaymarchMath.MaxHeightFraction;

    // ---------------------------------------------------------------------------------------------
    // The sun vector
    // ---------------------------------------------------------------------------------------------

    [TestCase(0f, 0f)]
    [TestCase(90f, 30f)]
    [TestCase(200f, -2.44f)]
    [TestCase(315f, 89f)]
    [TestCase(45f, -90f)]
    public void SunVector_is_unit_length(float azimuth, float elevation)
    {
        CloudRaymarchMath.SunVector(azimuth, elevation, out float lu, out float lv, out float lh);

        Assert.That(MathF.Sqrt(lu * lu + lv * lv + lh * lh), Is.EqualTo(1f).Within(Tolerance),
            "a non-unit light direction silently rescales every optical depth along it");
    }

    // The horizontal half must agree with the height field's own convention, or the two variants
    // would light the same volume from two different compass directions and the comparison between
    // them would be meaningless.
    [TestCase(0f)]
    [TestCase(90f)]
    [TestCase(200f)]
    [TestCase(287.5f)]
    public void SunVector_horizontal_part_matches_the_height_field_convention(float azimuth)
    {
        const float Elevation = 20f;
        CloudVolumeMath.SunDirection(azimuth, out float flatU, out float flatV);
        CloudRaymarchMath.SunVector(azimuth, Elevation, out float lu, out float lv, out _);

        float cosE = MathF.Cos(Elevation * (MathF.PI / 180f));

        Assert.Multiple(() =>
        {
            Assert.That(lu, Is.EqualTo(flatU * cosE).Within(Tolerance));
            Assert.That(lv, Is.EqualTo(flatV * cosE).Within(Tolerance));
        });
    }

    // Below the horizon the ray DESCENDS as it travels, which is the whole of the sunset: the light
    // dives into the cloud's own bulk instead of climbing clear of it.
    [Test]
    public void SunVector_climbs_above_the_horizon_and_descends_below_it()
    {
        CloudRaymarchMath.SunVector(200f, 10f, out _, out _, out float up);
        CloudRaymarchMath.SunVector(200f, -10f, out _, out _, out float down);

        Assert.Multiple(() =>
        {
            Assert.That(up, Is.GreaterThan(0f));
            Assert.That(down, Is.LessThan(0f));
            Assert.That(up, Is.EqualTo(-down).Within(Tolerance));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Sampling
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Sample_reads_a_voxel_at_its_own_centre()
    {
        byte[] volume = new byte[AtlasSize * AtlasSize * Layers];
        volume[CloudVolumeMath.VolumeIndex(3, 4, 2, AtlasSize, Layers)] = 255;

        float layerTexels = Peak / Layers;
        float value = CloudRaymarchMath.Sample(
            volume, AtlasSize, Layers, AtlasSize, 0, 0, 3f, 4f, 2.5f * layerTexels, Peak);

        Assert.That(value, Is.EqualTo(1f).Within(Tolerance));
    }

    // Outside the blob, and above or below the deck, there is NO cloud rather than a clamped copy of
    // the edge voxel. A clamped edge would extend the outermost wisp to infinity and wrap every
    // cloud in an endless shadow-casting skirt.
    [TestCase(-1f, 4f, 0.5f)]
    [TestCase(9f, 4f, 0.5f)]
    [TestCase(3f, -1f, 0.5f)]
    [TestCase(3f, 9f, 0.5f)]
    [TestCase(3f, 4f, -0.6f)]
    [TestCase(3f, 4f, 1.6f)]
    public void Sample_is_empty_outside_the_blob(float x, float y, float heightFraction)
    {
        byte[] volume = UniformVolume(255);

        float value = CloudRaymarchMath.Sample(
            volume, AtlasSize, Layers, AtlasSize, 0, 0, x, y, heightFraction * Peak, Peak);

        Assert.That(value, Is.EqualTo(0f).Within(Tolerance));
    }

    // Halfway between a full voxel and an empty one is half full. Pinned because the trilinear read
    // is the one part of this file the GPU does in FIXED-FUNCTION hardware rather than in code, so
    // the C# has to match the hardware's convention (texel centres, not texel corners) or the
    // reference and the shader disagree by half a texel everywhere.
    [Test]
    public void Sample_interpolates_between_neighbouring_texels()
    {
        byte[] volume = new byte[AtlasSize * AtlasSize * Layers];
        volume[CloudVolumeMath.VolumeIndex(3, 4, 2, AtlasSize, Layers)] = 255;

        float layerTexels = Peak / Layers;
        float value = CloudRaymarchMath.Sample(
            volume, AtlasSize, Layers, AtlasSize, 0, 0, 3.5f, 4f, 2.5f * layerTexels, Peak);

        Assert.That(value, Is.EqualTo(0.5f).Within(0.005f));
    }

    // ---------------------------------------------------------------------------------------------
    // The light march
    // ---------------------------------------------------------------------------------------------

    // The geometric step lengths must still sum to exactly the span, or raising the growth constant
    // would quietly shorten the march and lighten every shadow. Through a solid volume with a
    // horizontal sun the answer is the closed form: span x density x extinction.
    [Test]
    public void LightDepth_covers_the_whole_span_through_a_solid_volume()
    {
        byte[] volume = UniformVolume(255);

        // Dead horizontal, so the ray never climbs out and the span is the horizontal cap.
        CloudRaymarchMath.SunVector(90f, 0f, out float lu, out float lv, out float lh);

        float tau = CloudRaymarchMath.LightDepth(
            volume, AtlasSize, Layers, AtlasSize, 0, 0,
            x: 0f, y: 4f, height: Peak * 0.5f, peakTexels: Peak, lu: lu, lv: lv, lh: lh);

        float expected = AtlasSize * 0.667f * CloudRaymarchMath.ExtinctionPerTexel;

        Assert.That(tau, Is.EqualTo(expected).Within(0.01f),
            "the geometric segments no longer sum to the span they are derived from");
    }

    [Test]
    public void LightDepth_is_zero_through_an_empty_volume()
    {
        byte[] volume = new byte[AtlasSize * AtlasSize * Layers];
        CloudRaymarchMath.SunVector(200f, -2.44f, out float lu, out float lv, out float lh);

        float tau = CloudRaymarchMath.LightDepth(
            volume, AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, Peak * 0.5f, Peak, lu, lv, lh);

        Assert.That(tau, Is.EqualTo(0f).Within(Tolerance));
    }

    // A grazing sun sees more cloud than an overhead one, because it stays inside the deck instead
    // of climbing out of the top of it. This is the sunset in one assertion.
    [Test]
    public void LightDepth_grows_as_the_sun_drops()
    {
        byte[] volume = UniformVolume(200);

        float Depth(float elevation)
        {
            CloudRaymarchMath.SunVector(200f, elevation, out float lu, out float lv, out float lh);
            return CloudRaymarchMath.LightDepth(
                volume, AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, Peak * 0.5f, Peak, lu, lv, lh);
        }

        Assert.That(Depth(2f), Is.GreaterThan(Depth(20f)));
        Assert.That(Depth(20f), Is.GreaterThan(Depth(70f)));
    }

    // ---------------------------------------------------------------------------------------------
    // The view march
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void MarchPixel_draws_nothing_through_an_empty_volume()
    {
        byte[] volume = new byte[AtlasSize * AtlasSize * Layers];
        CloudRaymarchMath.SunVector(200f, 5f, out float lu, out float lv, out float lh);

        CloudRaymarchMath.MarchPixel(
            volume, AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, Peak, lu, lv, lh,
            1f, 1f, 1f, 0.3f, 0.3f, 0.3f,
            out float r, out float g, out float b, out float a);

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(r, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(g, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(b, Is.EqualTo(0f).Within(Tolerance));
        });
    }

    // Alpha is a real optical depth, not a density read off a texture — the property that fixes
    // #144's sheer sunset — so it must equal the closed form 1 - exp(-tau) for a uniform column.
    [TestCase((byte)64)]
    [TestCase((byte)128)]
    [TestCase((byte)255)]
    public void MarchPixel_alpha_is_the_columns_optical_depth(byte density)
    {
        // A FINELY LAYERED deck, unlike the rest of this file, and the reason is a real property
        // rather than a workaround. The trilinear read tapers the outermost half-layer away to
        // nothing at each end, which is correct — a cloud's top is a surface it fades through, not a
        // cliff — and costs about a third of a layer of optical depth. Against this file's usual 4
        // layers that is 9% of the whole column and swamps the closed form being checked; against
        // 64 it is a rounding error, so what this pins is the INTEGRAL rather than the taper.
        const int FineLayers = 64;
        byte[] volume = new byte[AtlasSize * AtlasSize * FineLayers];
        for (int i = 0; i < volume.Length; i++)
            volume[i] = density;

        CloudRaymarchMath.SunVector(200f, 45f, out float lu, out float lv, out float lh);

        CloudRaymarchMath.MarchPixel(
            volume, AtlasSize, FineLayers, AtlasSize, 0, 0, 4f, 4f, Peak, lu, lv, lh,
            1f, 1f, 1f, 0.3f, 0.3f, 0.3f,
            out _, out _, out _, out float a);

        float tau = (density / 255f) * CloudRaymarchMath.ExtinctionPerTexel * Peak;

        Assert.That(a, Is.EqualTo(1f - MathF.Exp(-tau)).Within(0.005f));
    }

    // The taper itself, pinned so that the allowance made above is a KNOWN quantity rather than a
    // tolerance somebody widened until the test went green. A coarse deck must come out slightly
    // sheerer than the closed form, never denser, and never by more than one layer's worth.
    [Test]
    public void MarchPixel_alpha_loses_a_little_to_the_soft_top_and_bottom()
    {
        byte[] volume = UniformVolume(255);
        CloudRaymarchMath.SunVector(200f, 45f, out float lu, out float lv, out float lh);

        CloudRaymarchMath.MarchPixel(
            volume, AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, Peak, lu, lv, lh,
            1f, 1f, 1f, 0.3f, 0.3f, 0.3f,
            out _, out _, out _, out float a);

        float tau = CloudRaymarchMath.ExtinctionPerTexel * Peak;
        float closedForm = 1f - MathF.Exp(-tau);
        float oneLayerLess = 1f - MathF.Exp(-tau * (Layers - 1f) / Layers);

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.LessThan(closedForm));
            Assert.That(a, Is.GreaterThan(oneLayerLess));
        });
    }

    [Test]
    public void MarchPixel_alpha_rises_with_density()
    {
        float Alpha(byte density)
        {
            CloudRaymarchMath.SunVector(200f, 45f, out float lu, out float lv, out float lh);
            CloudRaymarchMath.MarchPixel(
                UniformVolume(density), AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, Peak,
                lu, lv, lh, 1f, 1f, 1f, 0.3f, 0.3f, 0.3f,
                out _, out _, out _, out float a);
            return a;
        }

        Assert.That(Alpha(32), Is.LessThan(Alpha(128)));
        Assert.That(Alpha(128), Is.LessThan(Alpha(255)));
    }

    // The colour is PREMULTIPLIED, so it can never exceed the alpha it is multiplied by — a pixel
    // cannot emit more light than the cloud covering it. A regression here would show on screen as
    // a rim that stays bright as it fades out, which is the artefact that looks most like a bug.
    [TestCase(90f)]
    [TestCase(10f)]
    [TestCase(-2.44f)]
    public void MarchPixel_premultiplied_colour_never_exceeds_its_alpha(float elevation)
    {
        byte[] volume = UniformVolume(180);
        CloudRaymarchMath.SunVector(200f, elevation, out float lu, out float lv, out float lh);

        CloudRaymarchMath.MarchPixel(
            volume, AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, Peak, lu, lv, lh,
            1f, 1f, 1f, 0.3f, 0.3f, 0.3f,
            out float r, out float g, out float b, out float a);

        Assert.Multiple(() =>
        {
            Assert.That(r, Is.LessThanOrEqualTo(a + Tolerance));
            Assert.That(g, Is.LessThanOrEqualTo(a + Tolerance));
            Assert.That(b, Is.LessThanOrEqualTo(a + Tolerance));
        });
    }

    // A thin column is lit close to the sunlit colour; a deep one is dragged toward the shadow
    // colour by its own optical depth. This is the effect the whole subsystem exists to produce,
    // stated as a comparison so it cannot be satisfied by dimming everything.
    [Test]
    public void MarchPixel_darkens_a_deep_column_relative_to_a_thin_one()
    {
        CloudRaymarchMath.SunVector(200f, 3f, out float lu, out float lv, out float lh);

        float Brightness(byte density)
        {
            CloudRaymarchMath.MarchPixel(
                UniformVolume(density), AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, Peak,
                lu, lv, lh, 1f, 1f, 1f, 0.25f, 0.25f, 0.25f,
                out float r, out _, out _, out float a);

            // Divided back out of the premultiplication, so this compares how LIT the cloud is
            // rather than how much of it there is.
            return a <= 1e-4f ? 0f : r / a;
        }

        Assert.That(Brightness(255), Is.LessThan(Brightness(40)),
            "a deep column must shade itself; if it does not, the light march is not reaching it");
    }

    // ---------------------------------------------------------------------------------------------
    // The whole-atlas render
    // ---------------------------------------------------------------------------------------------

    // The Vacuum.cs convention: the gate is the LAST required parameter, it is never defaulted, and
    // it early-returns at the top before any arithmetic. On a vacuum map there is no cloud and no
    // atmosphere to light one.
    [Test]
    public void Render_draws_nothing_in_vacuum()
    {
        const int Supersample = 2;
        byte[] volume = UniformVolume(255);
        byte[] rgba = new byte[AtlasSize * Supersample * AtlasSize * Supersample * 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = 200;

        CloudRaymarchMath.Render(
            rgba, volume, AtlasSize, BlobsPerAxis, Layers, Supersample, 200f, -2.44f,
            1f, 1f, 1f, 0.3f, 0.3f, 0.3f, rowPeakTexels: null, inVacuum: true);

        Assert.That(rgba, Is.All.EqualTo(0));
    }

    [Test]
    public void Render_tolerates_a_missing_buffer()
    {
        Assert.DoesNotThrow(() => CloudRaymarchMath.Render(
            null, null, AtlasSize, BlobsPerAxis, Layers, 1, 200f, 0f,
            1f, 1f, 1f, 0.3f, 0.3f, 0.3f, rowPeakTexels: null, inVacuum: false));
    }

    // Every deck row stands at its own thickness, in the same ratios CloudVolumeMath tabulates in
    // metres — which is what makes cirrus a flat sheet that barely self-shadows and cumulus a deep
    // one that does. A2 records the same intent in its comments but never wired the parameter
    // through, so this is the assertion that says D actually did.
    [Test]
    public void RowPeakTexels_scales_each_deck_by_its_own_thickness()
    {
        const int BlobSize = 128;
        float[] peaks = CloudRaymarchMath.RowPeakTexels(BlobSize, CloudDeckMath.DeckCount);
        float reference = BlobSize * 0.5f * CloudRaymarchMath.MaxHeightFraction;

        Assert.Multiple(() =>
        {
            Assert.That(peaks, Has.Length.EqualTo(CloudDeckMath.DeckCount));
            Assert.That(peaks[0], Is.EqualTo(reference).Within(Tolerance),
                "the low deck is the reference thickness the height fraction was calibrated at");

            for (int deck = 0; deck < CloudDeckMath.DeckCount; deck++)
            {
                float ratio = CloudVolumeMath.ThicknessMetres(deck)
                    / CloudVolumeMath.ThicknessReferenceMetres;
                Assert.That(peaks[deck], Is.EqualTo(reference * ratio).Within(Tolerance));
            }

            Assert.That(peaks[2], Is.LessThan(peaks[1]));
            Assert.That(peaks[1], Is.LessThan(peaks[0]));
        });
    }

    // A flatter deck cannot shadow itself as far, so the same volume marched at a cirrus thickness
    // comes out brighter than at a cumulus one. Pinned because it is the one visible consequence of
    // the parameter above, and a silent revert to the reference height would pass every other test
    // in this file.
    [Test]
    public void A_thin_deck_self_shadows_less_than_a_deep_one()
    {
        byte[] volume = UniformVolume(220);
        CloudRaymarchMath.SunVector(200f, 4f, out float lu, out float lv, out float lh);

        float Brightness(float peak)
        {
            CloudRaymarchMath.MarchPixel(
                volume, AtlasSize, Layers, AtlasSize, 0, 0, 4f, 4f, peak,
                lu, lv, lh, 1f, 1f, 1f, 0.25f, 0.25f, 0.25f,
                out float r, out _, out _, out float a);
            return a <= 1e-4f ? 0f : r / a;
        }

        Assert.That(Brightness(Peak * 0.15f), Is.GreaterThan(Brightness(Peak)));
    }

    // ---------------------------------------------------------------------------------------------
    // The two extinction coefficients (issue #144)
    // ---------------------------------------------------------------------------------------------

    // The VIEW coefficient is normalised by thickness, so every deck's column reaches the same
    // optical depth for the same density. That is what the 2-D atlas does — its alpha is the shape,
    // and a deck's thinness is expressed downstream by CloudDeckMath's per-deck Opacity — and the
    // march used to do it twice, through geometry AND that opacity, so the same cloud came out a
    // different thickness depending on which lane drew it.
    [TestCase(48f, 19.2f)]
    [TestCase(48f, 7.2f)]
    [TestCase(30f, 30f)]
    public void ViewExtinctionKeepsColumnDepthConstantAcrossDecks(float reference, float peak)
    {
        float referenceDepth = CloudRaymarchMath.ViewExtinctionFor(reference, reference) * reference;
        float deckDepth = CloudRaymarchMath.ViewExtinctionFor(peak, reference) * peak;

        Assert.That(deckDepth, Is.EqualTo(referenceDepth).Within(1e-3f),
            "a deck's column depth must not depend on how thick the deck is");
    }

    // ...but only so far. The ratio grows without bound as a deck thins, and an unbounded
    // coefficient turns a wisp into a slab.
    [Test]
    public void ViewExtinctionIsClamped()
    {
        float vanishing = CloudRaymarchMath.ViewExtinctionFor(0.001f, 48f);

        Assert.That(vanishing,
            Is.EqualTo(CloudRaymarchMath.ExtinctionPerTexel * CloudRaymarchMath.MaxViewExtinctionScale)
                .Within(1e-4f));
    }

    // The LIGHT coefficient goes the OTHER way: it falls with the deck's thickness. Both directions
    // are needed at once and that is the whole point of there being two. A thin deck has to be
    // opaque enough to see from above (view coefficient up) while staying translucent to a grazing
    // sun (light coefficient down) — with one coefficient, making it visible pins it at the ambient
    // floor, which is measured and is the opposite of the sunset it exists to draw.
    [Test]
    public void TheTwoCoefficientsMoveInOppositeDirections()
    {
        const float Reference = 48f;
        const float Thin = 7.2f;

        float viewThick = CloudRaymarchMath.ViewExtinctionFor(Reference, Reference);
        float viewThin = CloudRaymarchMath.ViewExtinctionFor(Thin, Reference);
        float lightThick = CloudRaymarchMath.LightExtinctionFor(Reference, Reference);
        float lightThin = CloudRaymarchMath.LightExtinctionFor(Thin, Reference);

        Assert.Multiple(() =>
        {
            Assert.That(viewThin, Is.GreaterThan(viewThick), "a thin deck must still read from above");
            Assert.That(lightThin, Is.LessThan(lightThick), "a thin deck must stay lit by a low sun");
        });
    }

    // A deck at or above the reference thickness gets the plain coefficient for its light ray —
    // the scaling only ever thins it, never the reverse, so a deep deck is not made artificially
    // transparent to the sun.
    [TestCase(48f)]
    [TestCase(96f)]
    public void LightExtinctionNeverExceedsTheBaseCoefficient(float peak)
    {
        Assert.That(CloudRaymarchMath.LightExtinctionFor(peak, 48f),
            Is.EqualTo(CloudRaymarchMath.ExtinctionPerTexel).Within(1e-5f));
    }

    // The height that makes the shading possible at all. A cloud six times wider than it is tall has
    // nowhere for light to fall FROM, which is why the convective read — bright peaks over a
    // self-shadowed body — was absent however the shading was tuned.
    [Test]
    public void ACloudIsAboutAsTallAsItIsBroad()
    {
        Assert.That(CloudRaymarchMath.MaxHeightFraction, Is.GreaterThan(0.5f),
            "below this there is not enough vertical relief to self-shadow");
    }
}
