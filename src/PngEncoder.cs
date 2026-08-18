using System;
using System.IO;
using System.IO.Compression;

namespace Ramblers;

/// <summary>
/// Encodes RGB24 pixels as PNG using only managed BCL types.
///
/// Unity's own encoders cannot be used: Big Walk's IL2CPP build strips the
/// entire <c>ImageConversion</c> surface — <c>EncodeToJPG</c>,
/// <c>EncodeToPNG</c>, and <c>LoadImage</c> are all absent — so calling one
/// resolves a null native pointer and kills the process. See
/// <see cref="UnityApiProbe"/>.
/// </summary>
internal static class PngEncoder
{
    internal const string MediaType = "image/png";

    private static readonly byte[] Signature =
        { 137, 80, 78, 71, 13, 10, 26, 10 };

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="bottomUp">
    /// True when the first row of <paramref name="rgb"/> is the bottom of the
    /// image, which is how Unity hands back read-back pixel data.
    /// </param>
    internal static byte[] EncodeRgb24(
        byte[] rgb,
        int width,
        int height,
        bool bottomUp)
    {
        if (rgb == null || width <= 0 || height <= 0)
            throw new ArgumentException("An RGB24 buffer with a size is required.");

        var stride = width * 3;
        if (rgb.Length < stride * height)
            throw new ArgumentException("The RGB24 buffer is shorter than its stated size.");

        // Each PNG scanline carries a leading filter byte. Filter 0 (none)
        // keeps this cheap; deflate still finds most of the redundancy.
        var rawStride = stride + 1;
        var raw = new byte[rawStride * height];
        for (var row = 0; row < height; row++)
        {
            var sourceRow = bottomUp ? height - 1 - row : row;
            Buffer.BlockCopy(
                rgb,
                sourceRow * stride,
                raw,
                row * rawStride + 1,
                stride);
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            // ZLibStream emits the zlib header and Adler-32 that PNG's IDAT
            // requires; DeflateStream would emit neither. Fastest keeps the
            // main-thread cost of a capture down at some expense in size.
            using (var deflate = new ZLibStream(
                       buffer,
                       CompressionLevel.Fastest,
                       true))
            {
                deflate.Write(raw, 0, raw.Length);
            }

            compressed = buffer.ToArray();
        }

        using (var png = new MemoryStream(compressed.Length + 128))
        {
            png.Write(Signature, 0, Signature.Length);
            WriteChunk(png, "IHDR", BuildHeader(width, height));
            WriteChunk(png, "IDAT", compressed);
            WriteChunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }
    }

    private static byte[] BuildHeader(int width, int height)
    {
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour RGB
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter method: adaptive
        header[12] = 0; // interlace: none
        return header;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var header = new byte[8];
        WriteBigEndian(header, 0, (uint)data.Length);
        for (var index = 0; index < 4; index++)
            header[4 + index] = (byte)type[index];
        stream.Write(header, 0, header.Length);
        stream.Write(data, 0, data.Length);

        // The CRC covers the chunk type and its data, but not the length.
        var crc = Crc32(0xFFFFFFFFu, header, 4, 4);
        crc = Crc32(crc, data, 0, data.Length);
        var trailer = new byte[4];
        WriteBigEndian(trailer, 0, crc ^ 0xFFFFFFFFu);
        stream.Write(trailer, 0, trailer.Length);
    }

    private static uint Crc32(uint crc, byte[] data, int offset, int count)
    {
        for (var index = offset; index < offset + count; index++)
            crc = CrcTable[(crc ^ data[index]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint entry = 0; entry < 256; entry++)
        {
            var value = entry;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? 0xEDB88320u ^ (value >> 1)
                    : value >> 1;
            }

            table[entry] = value;
        }

        return table;
    }
}
