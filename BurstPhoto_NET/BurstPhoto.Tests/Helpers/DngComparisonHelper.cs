using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        
        int width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
        int height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        int bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 16;
        int samplesPerPixel = tiff.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 1;
        
        if (width == 0 || height == 0)
            return null;
        
        // For compressed DNGs, we need to use decoded strips/tiles
        int compression = tiff.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? 1;
        
        // If JPEG compressed, LibTiff.Net may not be able to decode directly
        if (compression == 7) // JPEG
        {
            // Return null for now - would need special handling for lossy DNG
            return null;
        }
        
        // Read strips for uncompressed
        int stripCount = tiff.NumberOfStrips();
        int pixelCount = width * height * samplesPerPixel;
        ushort[] pixels = new ushort[pixelCount];
        
        int offset = 0;
        for (int strip = 0; strip < stripCount; strip++)
        {
            byte[] buffer = new byte[tiff.StripSize()];
            int read = tiff.ReadEncodedStrip(strip, buffer, 0, buffer.Length);
            
            // Convert bytes to ushorts
            for (int i = 0; i < read && offset < pixels.Length; i += 2)
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
        int matchingPixels = 0;
        
        for (int i = 0; i < expected.Length; i++)
        {
            int diff = Math.Abs(expected[i] - actual[i]);
            sumAbsError += diff;
            sumSquaredError += (double)diff * diff;
            
            if (diff == 0)
                matchingPixels++;
        }
        
        double mae = sumAbsError / expected.Length;
        double rmse = Math.Sqrt(sumSquaredError / expected.Length);
        double psnr = rmse > 0 ? 20 * Math.Log10(65535.0 / rmse) : double.PositiveInfinity;
        double percentMatch = 100.0 * matchingPixels / expected.Length;
        
        return new PixelComparisonResult(mae, rmse, psnr, percentMatch, false);
    }

    private static DngMetadata ParseExiftoolOutput(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .ToList();
        
        int width = ParseIntField(lines, "Image Width");
        int height = ParseIntField(lines, "Image Height");
        string photometric = ParseStringField(lines, "Photometric Interpretation") ?? "Unknown";
        int[] cfaPattern = ParseIntArrayField(lines, "CFA Pattern 2") ?? Array.Empty<int>();
        int[] blackLevel = ParseIntArrayField(lines, "Black Level") ?? Array.Empty<int>();
        int whiteLevel = ParseIntField(lines, "White Level");
        double[] colorMatrix1 = ParseDoubleArrayField(lines, "Color Matrix 1") ?? Array.Empty<double>();
        double[] asShotNeutral = ParseDoubleArrayField(lines, "As Shot Neutral") ?? Array.Empty<double>();
        int bitsPerSample = ParseIntField(lines, "Bits Per Sample");
        int samplesPerPixel = ParseIntField(lines, "Samples Per Pixel");
        string compression = ParseStringField(lines, "Compression") ?? "Unknown";
        
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
                return int.TryParse(dims[0], out int w) ? w : 0;
            if (fieldName.Contains("Height", StringComparison.OrdinalIgnoreCase) && dims.Length > 1)
                return int.TryParse(dims[1], out int h) ? h : 0;
        }
        
        // Try parsing first number
        var match = Regex.Match(value, @"[\d]+");
        if (match.Success && int.TryParse(match.Value, out int result))
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
                           .Select(m => int.TryParse(m.Value, out int n) ? n : 0)
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
                           .Select(m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : 0)
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
