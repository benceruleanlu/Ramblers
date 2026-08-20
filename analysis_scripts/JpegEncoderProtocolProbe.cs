using System;
using System.Drawing;
using System.IO;

namespace Ramblers;

internal static class JpegEncoderProtocolProbe
{
    private const int Width = 640;
    private const int Height = 360;

    public static int Main(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Pass one JPEG output path.");

        // Construct the buffer in Unity's bottom-up order. The contrasting
        // halves make the probe verify orientation after decoding, not merely
        // that a JPEG parser accepts the byte stream.
        var rgb = new byte[Width * Height * 3];
        for (var rowFromBottom = 0; rowFromBottom < Height; rowFromBottom++)
        {
            var topHalf = rowFromBottom >= Height / 2;
            for (var x = 0; x < Width; x++)
            {
                var offset = (rowFromBottom * Width + x) * 3;
                var stripe = ((x / 32) & 1) == 0 ? 24 : 0;
                rgb[offset] = (byte)(topHalf ? 205 + stripe : 20);
                rgb[offset + 1] = (byte)(35 + (x * 110 / Width));
                rgb[offset + 2] = (byte)(topHalf ? 25 : 205 + stripe);
            }
        }

        var encoded = JpegEncoder.EncodeRgb24(rgb, Width, Height, true);
        if (encoded.Length < 4 || encoded[0] != 0xFF || encoded[1] != 0xD8 ||
            encoded[encoded.Length - 2] != 0xFF ||
            encoded[encoded.Length - 1] != 0xD9)
        {
            throw new InvalidDataException("The encoder did not emit a complete JPEG stream.");
        }

        File.WriteAllBytes(args[0], encoded);
        using (var stream = new MemoryStream(encoded))
        using (var bitmap = new Bitmap(stream))
        {
            if (bitmap.Width != Width || bitmap.Height != Height)
                throw new InvalidDataException("The decoded dimensions changed.");

            var top = bitmap.GetPixel(Width / 2, Height / 4);
            var bottom = bitmap.GetPixel(Width / 2, Height * 3 / 4);
            if (top.R <= top.B || bottom.B <= bottom.R)
                throw new InvalidDataException("The decoded image is vertically inverted.");
        }

        Console.WriteLine(
            "JPEG protocol probe passed: {0}x{1}, quality={2}, bytes={3}, rawBytes={4}.",
            Width,
            Height,
            JpegEncoder.DefaultQuality,
            encoded.Length,
            rgb.Length);
        return 0;
    }
}
