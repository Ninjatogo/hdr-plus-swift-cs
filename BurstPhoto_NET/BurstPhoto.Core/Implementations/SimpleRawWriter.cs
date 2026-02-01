using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.IO;
using System.Text;

namespace BurstPhoto.Core.Implementations;

/// <summary>
/// Writes raw images to Netpbm format (PGM for grayscale, PPM for RGB).
/// </summary>
/// <remarks>
/// This is a simple writer primarily used for debugging and testing.
/// The output format is portable but not suitable for final output as it lacks metadata.
/// Pixel values are written as 16-bit big-endian values.
/// </remarks>
public class SimpleRawWriter : IRawImageWriter
{
    /// <summary>
    /// Maximum pixel value for 16-bit output.
    /// </summary>
    private const int MaxPixelValue = 65535;

    /// <inheritdoc />
    public void Write(RawImage image, string outputPath)
    {
        var pixelCount = image.Width * image.Height;
        var isRgb = image.Data.Length >= pixelCount * 3;

        using var fileStream = File.Create(outputPath);
        using var writer = new BinaryWriter(fileStream);

        // Write Netpbm header: P5 for grayscale (PGM), P6 for RGB (PPM)
        var formatMagic = isRgb ? "P6" : "P5";
        var header = $"{formatMagic}\n{image.Width} {image.Height}\n{MaxPixelValue}\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        writer.Write(headerBytes);

        // Convert pixel data to big-endian byte array (Netpbm uses big-endian)
        var pixelBuffer = new byte[image.Data.Length * 2];
        for (var pixelIndex = 0; pixelIndex < image.Data.Length; pixelIndex++)
        {
            var pixelValue = image.Data[pixelIndex];
            pixelBuffer[2 * pixelIndex] = (byte)(pixelValue >> 8);
            pixelBuffer[2 * pixelIndex + 1] = (byte)(pixelValue & 0xFF);
        }

        writer.Write(pixelBuffer);
    }

    /// <inheritdoc />
    public Task WriteAsync(RawImage image, string outputPath)
    {
        // Delegate to synchronous version - I/O is typically fast for single images
        return Task.Run(() => Write(image, outputPath));
    }
}
