namespace CelestialLighting.Tests;

/// <summary>
/// The whole-buffer row flip the in-place texture upload uses on RimWorld 1.7
/// (<see cref="TextureRowOrderMath"/>).
/// </summary>
[TestFixture]
public class TextureRowOrderMathTests
{
    [Test]
    public void FlipRows_ReversesRowsAndKeepsEachRowIntact()
    {
        byte[] source = { 1, 2, 3, 4, 5, 6 };
        byte[] destination = new byte[6];

        TextureRowOrderMath.FlipRows(source, destination, rowBytes: 2, rows: 3);

        Assert.That(destination, Is.EqualTo(new byte[] { 5, 6, 3, 4, 1, 2 }));
    }

    [Test]
    public void FlipRows_AgreesWithThePerTexelMapping()
    {
        // The same mapping VectorLightMath.FlippedRowIndex states one texel at a time, so the two
        // forms cannot drift apart. Four bytes per texel, as RGBA32 stores them. Square, because
        // FlippedRowIndex is: it reads the row count off the width, which every field texture allows.
        const int width = 4, height = 4;
        byte[] source = new byte[width * height * 4];
        for (int i = 0; i < source.Length; i++)
            source[i] = (byte)i;

        byte[] destination = new byte[source.Length];
        TextureRowOrderMath.FlipRows(source, destination, rowBytes: width * 4, rows: height);

        for (int texel = 0; texel < width * height; texel++)
        {
            int flipped = VectorLightMath.FlippedRowIndex(texel, width);
            Assert.That(destination[flipped * 4], Is.EqualTo(source[texel * 4]), $"texel {texel}");
        }
    }

    [Test]
    public void FlipRows_OfOneRowIsACopy()
    {
        byte[] destination = new byte[3];

        TextureRowOrderMath.FlipRows(new byte[] { 7, 8, 9 }, destination, rowBytes: 3, rows: 1);

        Assert.That(destination, Is.EqualTo(new byte[] { 7, 8, 9 }));
    }

    [TestCase(5, 6)]
    [TestCase(6, 5)]
    public void FlipRows_RefusesBuffersOfTheWrongLength(int sourceLength, int destinationLength)
    {
        Assert.Throws<ArgumentException>(() =>
            TextureRowOrderMath.FlipRows(new byte[sourceLength], new byte[destinationLength], 2, 3));
    }
}
