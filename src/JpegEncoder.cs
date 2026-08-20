using System;
using System.IO;
using StbImageWriteSharp;

namespace Ramblers;

/// <summary>
/// Encodes RGB24 pixels as a baseline JPEG without touching Unity's stripped
/// <c>ImageConversion</c> surface. StbImageWriteSharp is a managed, no-native
/// port of stb_image_write and is pinned by hash in <c>build.ps1</c>.
/// </summary>
internal static class JpegEncoder
{
    internal const string MediaType = "image/jpeg";
    internal const int DefaultQuality = 82;

    /// <param name="bottomUp">
    /// True when the first row of <paramref name="rgb"/> is the bottom of the
    /// image, which is how Unity hands back read-back pixel data.
    /// </param>
    internal static byte[] EncodeRgb24(
        byte[] rgb,
        int width,
        int height,
        bool bottomUp,
        int quality = DefaultQuality)
    {
        if (rgb == null || width <= 0 || height <= 0)
            throw new ArgumentException("An RGB24 buffer with a size is required.");
        if (quality < 1 || quality > 100)
            throw new ArgumentOutOfRangeException(nameof(quality));

        var requiredLength = (long)width * height * 3;
        if (requiredLength > int.MaxValue || rgb.Length < requiredLength)
            throw new ArgumentException("The RGB24 buffer is shorter than its stated size.");

        var pixels = rgb;
        if (bottomUp)
        {
            var stride = width * 3;
            pixels = new byte[(int)requiredLength];
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(
                    rgb,
                    (height - 1 - row) * stride,
                    pixels,
                    row * stride,
                    stride);
            }
        }

        using (var encoded = new MemoryStream(
                   Math.Max(1024, (int)requiredLength / 4)))
        {
            var writer = new ImageWriter();
            writer.WriteJpg(
                pixels,
                width,
                height,
                ColorComponents.RedGreenBlue,
                encoded,
                quality);
            return encoded.ToArray();
        }
    }
}
