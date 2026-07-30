using System.IO;
using System.IO.Compression;

namespace CelestialLighting.Tools;

// Minimal RGBA PNG writer. Hand-rolled because this box has neither ImageMagick nor PIL, and pulling a
// NuGet imaging package into a dev-only preview tool would be a heavier dependency than the ~60 lines
// it saves. Only what a previewer needs: 8-bit RGBA, no interlacing, no palette, one IDAT.
public static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(string path, int width, int height, byte[] rgba)
    {
        using FileStream file = File.Create(path);

        file.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR: bit depth 8, colour type 6 (truecolour + alpha), deflate, no filter, no interlace.
        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(file, "IHDR", ihdr);

        // Scanlines, each prefixed by its filter type. Filter 0 (None) throughout: the images this tool
        // produces are noise fields, where a predictive filter buys little and costs clarity here.
        byte[] raw = new byte[height * (width * 4 + 1)];
        for (int y = 0; y < height; y++)
        {
            int src = y * width * 4;
            int dst = y * (width * 4 + 1);
            raw[dst] = 0;
            System.Array.Copy(rgba, src, raw, dst + 1, width * 4);
        }

        // ZLibStream rather than DeflateStream: PNG's IDAT is a zlib stream, so it needs the 2-byte
        // header and trailing Adler-32 that a bare deflate stream omits. Getting this wrong produces a
        // file that some decoders accept and others reject, which is a miserable thing to debug.
        using MemoryStream compressed = new MemoryStream();
        using (ZLibStream deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", System.Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        byte[] typeBytes = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc(typeBytes, data);
        byte[] crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
