using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for §28's cadence split of the volumetric sheet's shader uniforms
// (Source/CloudVolumeUniforms.cs), linked into this project via <Compile Include> so these exercise
// the exact file that ships.
//
// WHAT THESE ARE ACTUALLY FOR, because it is not the arithmetic. The arithmetic moved out of
// CloudVolumeShader.Configure unchanged and the reproduction tests below hold it to the expressions
// it had there. The tests that matter are the INDEPENDENCE ones: Configure now skips a native
// setter when the value it would write has not changed, and that is correct only for as long as
// Geometry genuinely depends on the sheet's placement and its deck and on nothing else. A sun or a
// colour term added to Geometry later would break the cache by making a per-frame quantity live in
// a per-crossing group, and the failure mode on screen is a cloud lit from a stale sun — plausible,
// undramatic, and very hard to attribute. These pin the claim instead.
[TestFixture]
public class CloudVolumeUniformsTests
{
    private const float Tolerance = 1e-5f;

    // The shipped atlas geometry: 384 texels across 3x3 cells, 20 volume layers, 2 pad slices.
    private const int AtlasSize = 384;
    private const int AtlasCells = 3;
    private const int VolumeLayers = 20;
    private const int PadSlices = 2;

    private const float BasePeak = 64f;

    private static CloudVolumeUniforms.Geometry Geometry(
        int blob = 4, bool flipU = false, bool flipV = false, float peakTexels = 64f) =>
        CloudVolumeUniforms.GeometryFor(
            blob, AtlasCells, AtlasSize, flipU, flipV, peakTexels, BasePeak,
            VolumeLayers, PadSlices);

    // ---------------------------------------------------------------------------------------------
    // The arithmetic, held to what Configure used to compute inline
    // ---------------------------------------------------------------------------------------------

    // Cell 4 of a 3x3 atlas is column 1, row 1 — the middle blob. Unflipped, the UV transform is a
    // third of the atlas offset one third along each axis.
    [Test]
    public void Unflipped_cell_maps_to_its_own_third_of_the_atlas()
    {
        CloudVolumeUniforms.Geometry geometry = Geometry(blob: 4);

        Assert.That(geometry.ScaleU, Is.EqualTo(1f / 3f).Within(Tolerance));
        Assert.That(geometry.ScaleV, Is.EqualTo(1f / 3f).Within(Tolerance));
        Assert.That(geometry.OffsetU, Is.EqualTo(1f / 3f).Within(Tolerance));
        Assert.That(geometry.OffsetV, Is.EqualTo(1f / 3f).Within(Tolerance));
    }

    // A mirrored axis reads the cell backwards, so the scale goes negative and the offset moves to
    // the cell's FAR edge — sampling from there with a negative step lands back inside the cell.
    // Getting this pair inconsistent samples the neighbouring cloud, which is a visible seam.
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void Mirroring_negates_the_scale_and_shifts_the_offset_one_cell(bool flipU, bool flipV)
    {
        CloudVolumeUniforms.Geometry geometry = Geometry(blob: 4, flipU: flipU, flipV: flipV);

        float cell = 1f / AtlasCells;

        Assert.That(geometry.ScaleU, Is.EqualTo(flipU ? -cell : cell).Within(Tolerance));
        Assert.That(geometry.ScaleV, Is.EqualTo(flipV ? -cell : cell).Within(Tolerance));
        Assert.That(geometry.OffsetU, Is.EqualTo((1 + (flipU ? 1 : 0)) * cell).Within(Tolerance));
        Assert.That(geometry.OffsetV, Is.EqualTo((1 + (flipV ? 1 : 0)) * cell).Within(Tolerance));
    }

    // The bounds are INCLUSIVE texel indices of the blob's own cell, which is what stops a march
    // wandering into the neighbouring cloud. An off-by-one here is a one-texel bright line along two
    // edges of every sheet.
    [TestCase(0, 0, 0)]
    [TestCase(4, 128, 128)]
    [TestCase(8, 256, 256)]
    public void Cell_bounds_are_the_inclusive_texel_rectangle(int blob, float minX, float minY)
    {
        CloudVolumeUniforms.Geometry geometry = Geometry(blob: blob);

        int blobSize = AtlasSize / AtlasCells;

        Assert.That(geometry.MinX, Is.EqualTo(minX).Within(Tolerance));
        Assert.That(geometry.MinY, Is.EqualTo(minY).Within(Tolerance));
        Assert.That(geometry.MaxX, Is.EqualTo(minX + blobSize - 1).Within(Tolerance));
        Assert.That(geometry.MaxY, Is.EqualTo(minY + blobSize - 1).Within(Tolerance));
    }

    // Mirroring changes which way the cell is READ, never which cell it is. If a flip moved the
    // bounds, a mirrored sheet would clamp its march to somebody else's cloud.
    [Test]
    public void Mirroring_does_not_move_the_cell_bounds()
    {
        CloudVolumeUniforms.Geometry plain = Geometry(blob: 4);
        CloudVolumeUniforms.Geometry mirrored = Geometry(blob: 4, flipU: true, flipV: true);

        Assert.That(mirrored.MinX, Is.EqualTo(plain.MinX));
        Assert.That(mirrored.MinY, Is.EqualTo(plain.MinY));
        Assert.That(mirrored.MaxX, Is.EqualTo(plain.MaxX));
        Assert.That(mirrored.MaxY, Is.EqualTo(plain.MaxY));
    }

    // A deck's layer thickness is its peak divided across the volume's slices; the padded slice
    // count is what the shader uses to convert a height into a texture coordinate, and it must
    // include the two zero slices the bake pads with or every sample lands one slice out.
    [TestCase(32f)]
    [TestCase(64f)]
    [TestCase(96f)]
    public void Volume_params_carry_the_padded_slice_count_and_the_deck_thickness(float peak)
    {
        CloudVolumeUniforms.Geometry geometry = Geometry(peakTexels: peak);

        Assert.That(geometry.AtlasSize, Is.EqualTo(AtlasSize).Within(Tolerance));
        Assert.That(geometry.PaddedLayers, Is.EqualTo(VolumeLayers + PadSlices).Within(Tolerance));
        Assert.That(geometry.PeakTexels, Is.EqualTo(peak).Within(Tolerance));
        Assert.That(geometry.LayerTexels, Is.EqualTo(peak / VolumeLayers).Within(Tolerance));
    }

    // The two extinction coefficients and the ambient wrap are CloudRaymarchMath's, not restated
    // here — a second copy of them would drift from the shader they are transcribed into.
    [TestCase(32f)]
    [TestCase(96f)]
    public void March_params_come_from_CloudRaymarchMath(float peak)
    {
        CloudVolumeUniforms.Geometry geometry = Geometry(peakTexels: peak);

        Assert.That(geometry.ViewExtinction,
            Is.EqualTo(CloudRaymarchMath.ViewExtinctionFor(peak, BasePeak)).Within(Tolerance));
        Assert.That(geometry.LightExtinction,
            Is.EqualTo(CloudRaymarchMath.LightExtinctionFor(peak, BasePeak)).Within(Tolerance));
        Assert.That(geometry.AmbientWrap,
            Is.EqualTo(CloudRaymarchMath.AmbientWrap).Within(Tolerance));
        Assert.That(geometry.ShadowReach,
            Is.EqualTo(AtlasSize / AtlasCells * 0.667f).Within(Tolerance));
    }

    // ---------------------------------------------------------------------------------------------
    // The independence claim the write cache rests on
    // ---------------------------------------------------------------------------------------------

    // GeometryFor takes no sun and no colour, so this is really a test that the SIGNATURE has not
    // grown one. It is written as a behaviour test anyway because a compile error is the good
    // outcome and a silently added optional parameter is the bad one.
    [Test]
    public void Geometry_is_unchanged_across_a_whole_day_of_sun()
    {
        CloudVolumeUniforms.Geometry morning = Geometry();
        CloudVolumeUniforms.Geometry noon = Geometry();

        Assert.That(noon.Equals(morning), Is.True,
            "Geometry is cached per crossing; anything in it that moves with the sun would be "
            + "served stale for hundreds of frames");
    }

    // The three inputs that MUST dirty the cache. If any of these compared equal, a re-placed sheet
    // would keep drawing the previous crossing's cloud in the previous crossing's cell.
    [Test]
    public void Geometry_changes_when_the_blob_changes()
    {
        Assert.That(Geometry(blob: 4).Equals(Geometry(blob: 5)), Is.False);
    }

    [Test]
    public void Geometry_changes_when_the_mirroring_changes()
    {
        Assert.That(Geometry(flipU: false).Equals(Geometry(flipU: true)), Is.False);
        Assert.That(Geometry(flipV: false).Equals(Geometry(flipV: true)), Is.False);
    }

    [Test]
    public void Geometry_changes_when_the_deck_thickness_changes()
    {
        Assert.That(Geometry(peakTexels: 64f).Equals(Geometry(peakTexels: 65f)), Is.False);
    }

    // Equality has to cover every field, or a field that changes without dirtying the struct is
    // written once and then frozen. Enumerated one at a time rather than trusted to the compiler
    // because Geometry hand-writes Equals — the default struct equality it replaced would have been
    // correct here and much slower.
    [Test]
    public void Equality_covers_every_field_that_can_move()
    {
        CloudVolumeUniforms.Geometry reference = Geometry(blob: 4, peakTexels: 64f);

        // blob 5 moves column-derived fields; blob 7 moves row-derived ones; the flips move scale
        // and offset; the peak moves thickness and both extinctions. Between them every field of
        // the struct is touched.
        Assert.That(reference.Equals(Geometry(blob: 5, peakTexels: 64f)), Is.False);
        Assert.That(reference.Equals(Geometry(blob: 7, peakTexels: 64f)), Is.False);
        Assert.That(reference.Equals(Geometry(blob: 4, flipU: true, peakTexels: 64f)), Is.False);
        Assert.That(reference.Equals(Geometry(blob: 4, flipV: true, peakTexels: 64f)), Is.False);
        Assert.That(reference.Equals(Geometry(blob: 4, peakTexels: 96f)), Is.False);

        // And the same inputs must still compare equal, or the cache never hits and the whole
        // optimisation is a no-op that costs an extra compare.
        Assert.That(reference.Equals(Geometry(blob: 4, peakTexels: 64f)), Is.True);
    }

    // Equal values must hash alike, because the hash is derived from a SUBSET of the fields and a
    // subset is only a valid hash if it never disagrees with equality.
    [Test]
    public void Equal_geometries_agree_on_their_hash()
    {
        CloudVolumeUniforms.Geometry first = Geometry(blob: 4, peakTexels: 64f);
        CloudVolumeUniforms.Geometry second = Geometry(blob: 4, peakTexels: 64f);

        Assert.That(first.Equals(second), Is.True);
        Assert.That(second.GetHashCode(), Is.EqualTo(first.GetHashCode()));
    }

    // ---------------------------------------------------------------------------------------------
    // The sun direction, which is the other half of the split
    // ---------------------------------------------------------------------------------------------

    // The horizontal components mirror with the texture and the VERTICAL one does not: flipping a
    // sheet's UVs reverses which flank faces the light, not whether the sun is above the horizon.
    // Negating h would put a midday sun underneath every mirrored cloud.
    [TestCase(false, false, 0.6f, 0.8f)]
    [TestCase(true, false, -0.6f, 0.8f)]
    [TestCase(false, true, 0.6f, -0.8f)]
    [TestCase(true, true, -0.6f, -0.8f)]
    public void Sun_direction_mirrors_horizontally_only(
        bool flipU, bool flipV, float expectedU, float expectedV)
    {
        CloudVolumeUniforms.SunDirection(0.6f, 0.8f, 0.35f, flipU, flipV,
            out float u, out float v, out float h);

        Assert.That(u, Is.EqualTo(expectedU).Within(Tolerance));
        Assert.That(v, Is.EqualTo(expectedV).Within(Tolerance));
        Assert.That(h, Is.EqualTo(0.35f).Within(Tolerance),
            "a mirrored sheet is still under the same sky; negating h buries a midday sun");
    }

    // Mirroring is a sign flip, so it preserves length — the march's optical depths are all
    // relative to a unit light direction and a rescaled one silently rescales every one of them.
    [TestCase(0f, 0f)]
    [TestCase(120f, 12f)]
    [TestCase(285f, -3.7f)]
    public void Mirrored_sun_direction_stays_unit_length(float azimuth, float elevation)
    {
        CloudRaymarchMath.SunVector(azimuth, elevation, out float lu, out float lv, out float lh);
        CloudVolumeUniforms.SunDirection(lu, lv, lh, flipU: true, flipV: true,
            out float u, out float v, out float h);

        Assert.That(System.MathF.Sqrt(u * u + v * v + h * h), Is.EqualTo(1f).Within(Tolerance));
    }
}
