using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BitMiracle.LibTiff.Classic;

namespace BurstPhoto.Tests.Helpers;

/// <summary>
/// Helper class for extracting and comparing DNG metadata and pixel data.
/// </summary>
public static class DngComparisonHelper
{
    /// <summary>
    /// Extracts key metadata from a DNG file using exiftool.
    /// </summary>
    public static DngMetadata ExtractMetadata(string dngPath, string? exiftoolPath = null)
    {
        exiftoolPath ??= TestDataPaths.ExiftoolPath;
        
        if (!File.Exists(dngPath))
            throw new FileNotFoundException($"DNG file not found: {dngPath}");
        
        if (!File.Exists(exiftoolPath))
            throw new FileNotFoundException($"Exiftool not found: {exiftoolPath}");
        
        // Run exiftool to get metadata
        var startInfo = new ProcessStartInfo
        {
            FileName = exiftoolPath,
            Arguments = $"-all \"{dngPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start exiftool process");
        
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000); // 30 second timeout
        
        return ParseExiftoolOutput(output);
    }

    /// <summary>
    /// Extracts raw pixel data from a DNG file using LibTiff.
    /// </summary>
    public static ushort[]? ExtractPixelData(string dngPath)
    {
        if (!File.Exists(dngPath))
            return null;
        
        using var tiff = Tiff.Open(dngPath, "r");
        if (tiff == null)
            return null;
        
        var width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
        var height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        var bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 16;
        var samplesPerPixel = tiff.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 1;
        
        if (width == 0 || height == 0)
            return null;
        
        // For compressed DNGs, we need to use decoded strips/tiles
        var compression = tiff.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? 1;
        
        // If JPEG compressed, LibTiff.Net may not be able to decode directly
        if (compression == 7) // JPEG
        {
            // Return null for now - would need special handling for lossy DNG
            return null;
        }
        
        // Read strips for uncompressed
        var stripCount = tiff.NumberOfStrips();
        var pixelCount = width * height * samplesPerPixel;
        var pixels = new ushort[pixelCount];
        
        var offset = 0;
        for (var strip = 0; strip < stripCount; strip++)
        {
            var buffer = new byte[tiff.StripSize()];
            var read = tiff.ReadEncodedStrip(strip, buffer, 0, buffer.Length);
            
            // Convert bytes to ushorts
            for (var i = 0; i < read && offset < pixels.Length; i += 2)
            {
                if (i + 1 < read)
                {
                    pixels[offset++] = BitConverter.ToUInt16(buffer, i);
                }
            }
        }
        
        return pixels;
    }

    /// <summary>
    /// Compares two pixel arrays and returns comparison metrics.
    /// </summary>
    public static PixelComparisonResult ComparePixels(ushort[] expected, ushort[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return new PixelComparisonResult(
                MeanAbsoluteError: double.MaxValue,
                RootMeanSquareError: double.MaxValue,
                PeakSignalNoiseRatio: 0,
                PercentMatchingPixels: 0,
                LengthMismatch: true);
        }
        
        double sumAbsError = 0;
        double sumSquaredError = 0;
        var matchingPixels = 0;
        
        for (var i = 0; i < expected.Length; i++)
        {
            var diff = Math.Abs(expected[i] - actual[i]);
            sumAbsError += diff;
            sumSquaredError += (double)diff * diff;
            
            if (diff == 0)
                matchingPixels++;
        }
        
        var mae = sumAbsError / expected.Length;
        var rmse = Math.Sqrt(sumSquaredError / expected.Length);
        var psnr = rmse > 0 ? 20 * Math.Log10(65535.0 / rmse) : double.PositiveInfinity;
        var percentMatch = 100.0 * matchingPixels / expected.Length;
        
        return new PixelComparisonResult(mae, rmse, psnr, percentMatch, false);
    }

    private static DngMetadata ParseExiftoolOutput(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .ToList();
        
        var width = ParseIntField(lines, "Image Width");
        var height = ParseIntField(lines, "Image Height");
        var photometric = ParseStringField(lines, "Photometric Interpretation") ?? "Unknown";
        var cfaPattern = ParseIntArrayField(lines, "CFA Pattern 2") ?? [];
        var blackLevel = ParseIntArrayField(lines, "Black Level") ?? [];
        var whiteLevel = ParseIntField(lines, "White Level");
        var colorMatrix1 = ParseDoubleArrayField(lines, "Color Matrix 1") ?? [];
        var asShotNeutral = ParseDoubleArrayField(lines, "As Shot Neutral") ?? [];
        var bitsPerSample = ParseIntField(lines, "Bits Per Sample");
        var samplesPerPixel = ParseIntField(lines, "Samples Per Pixel");
        var compression = ParseStringField(lines, "Compression") ?? "Unknown";
        
        return new DngMetadata(
            Width: width,
            Height: height,
            Photometric: photometric,
            CfaPattern: cfaPattern,
            BlackLevel: blackLevel,
            WhiteLevel: whiteLevel,
            ColorMatrix1: colorMatrix1,
            AsShotNeutral: asShotNeutral,
            BitsPerSample: bitsPerSample,
            SamplesPerPixel: samplesPerPixel,
            Compression: compression);
    }

    private static int ParseIntField(List<string> lines, string fieldName)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase));
        if (line == null) return 0;
        
        var parts = line.Split(':', 2);
        if (parts.Length < 2) return 0;
        
        var value = parts[1].Trim();
        // Handle "4096x3072" format
        if (value.Contains('x'))
        {
            var dims = value.Split('x');
            if (fieldName.Contains("Width", StringComparison.OrdinalIgnoreCase) && dims.Length > 0)
                return int.TryParse(dims[0], out var w) ? w : 0;
            if (fieldName.Contains("Height", StringComparison.OrdinalIgnoreCase) && dims.Length > 1)
                return int.TryParse(dims[1], out var h) ? h : 0;
        }
        
        // Try parsing first number
        var match = Regex.Match(value, @"[\d]+");
        if (match.Success && int.TryParse(match.Value, out var result))
            return result;
        
        return 0;
    }

    private static string? ParseStringField(List<string> lines, string fieldName)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase));
        if (line == null) return null;
        
        var parts = line.Split(':', 2);
        return parts.Length >= 2 ? parts[1].Trim() : null;
    }

    private static int[]? ParseIntArrayField(List<string> lines, string fieldName)
    {
        // Use regex to match exact field name followed by whitespace and colon
        // This handles exiftool format like "Black Level      : 1024 1024"
        // and avoids matching "Black Level Repeat Dim" when looking for "Black Level"
        var pattern = new Regex($"^{Regex.Escape(fieldName)}\\s*:", RegexOptions.IgnoreCase);
        var line = lines.FirstOrDefault(l => pattern.IsMatch(l));
        if (line == null) return null;
        
        var parts = line.Split(':', 2);
        if (parts.Length < 2) return null;
        
        var value = parts[1].Trim();
        var numbers = Regex.Matches(value, @"-?[\d]+")
                           .Select(m => int.TryParse(m.Value, out var n) ? n : 0)
                           .ToArray();
        
        return numbers.Length > 0 ? numbers : null;
    }

    private static double[]? ParseDoubleArrayField(List<string> lines, string fieldName)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase));
        if (line == null) return null;
        
        var parts = line.Split(':', 2);
        if (parts.Length < 2) return null;
        
        var value = parts[1].Trim();
        var numbers = Regex.Matches(value, @"-?[\d.]+")
                           .Select(m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0)
                           .ToArray();
        
        return numbers.Length > 0 ? numbers : null;
    }
}

/// <summary>
/// Metadata extracted from a DNG file.
/// </summary>
public record DngMetadata(
    int Width,
    int Height,
    string Photometric,
    int[] CfaPattern,
    int[] BlackLevel,
    int WhiteLevel,
    double[] ColorMatrix1,
    double[] AsShotNeutral,
    int BitsPerSample,
    int SamplesPerPixel,
    string Compression);

/// <summary>
/// Result of comparing two pixel arrays.
/// </summary>
public record PixelComparisonResult(
    double MeanAbsoluteError,
    double RootMeanSquareError,
    double PeakSignalNoiseRatio,
    double PercentMatchingPixels,
    bool LengthMismatch);
