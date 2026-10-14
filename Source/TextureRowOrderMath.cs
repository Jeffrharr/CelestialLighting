using System;

namespace CelestialLighting;

// Moving a whole texture between Unity's row order (row 0 at the BOTTOM) and RimWorld 1.7's native
// order (row 0 at the TOP), for buffers that are written whole rather than a texel at a time.
// VectorLightMath.FlippedRowIndex is the per-texel form of the same mapping; this is the per-row form,
// which is a block copy per row rather than an index computation per texel.
//
// Its own function for the reason FlippedRowIndex is: a flip off by one row compiles, renders, and
// shifts the whole image by a row, which reads as an artistic bug rather than an index bug.
public static class TextureRowOrderMath
{
    // Copies `source` into `destination` with the row order reversed. Both must be exactly
    // rowBytes * rows long — a short buffer would leave the far rows holding the previous frame,
    // which renders as a plausible stale band rather than an error.
    public static void FlipRows(ReadOnlySpan<byte> source, Span<byte> destination, int rowBytes, int rows)
    {
        int length = rowBytes * rows;

        if (source.Length != length || destination.Length != length)
            throw new ArgumentException(
                $"buffers hold {source.Length} and {destination.Length} bytes, need {length}");

        for (int row = 0; row < rows; row++)
            source.Slice(row * rowBytes, rowBytes).CopyTo(destination.Slice((rows - 1 - row) * rowBytes));
    }
}
