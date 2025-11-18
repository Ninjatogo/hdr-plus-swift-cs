using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Text;

namespace HdrPlus.IO;

/// <summary>
/// Simple RAW/DNG reader implementation using ImageSharp.
/// This is a minimal fallback implementation.
/// For production use, prefer LibRawDngReader which supports 700+ RAW formats
/// and extracts full metadata including color matrices and EXIF data.
/// </summary>
public class SimpleRawReader : IDngReader
{
    public string[] SupportedExtensions => new[] { ".dng", ".tif", ".tiff" };

    public bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    public DngImage ReadDng(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"DNG file not found: {filePath}");
        }

        // For now, use ImageSharp to load as 16-bit grayscale
        // TODO: Replace with LibRaw for proper RAW/DNG support with full metadata
        using var image = Image.Load<L16>(filePath);

        int width = image.Width;
        int height = image.Height;
        var rawData = new ushort[width * height];

        // Extract pixel data
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    rawData[y * width + x] = row[x].PackedValue;
                }
            }
        });

        // Default metadata (TODO: parse from TIFF/DNG tags)
        return new DngImage
        {
            RawData = rawData,
            Width = width,
            Height = height,
            MosaicPatternWidth = 2, // Assume Bayer
            MosaicPattern = "RGGB",
            BlackLevels = new int[] { 512, 512, 512, 512 }, // Typical for 12-bit
            WhiteLevel = 4095, // 12-bit max
            ExposureBias = 0,
            IsoExposureTime = 1.0,
            ColorFactors = new double[] { 1.0, 1.0, 1.0 },
            CameraMake = "Unknown",
            CameraModel = "Unknown",
            FilePath = filePath
        };
    }
}
