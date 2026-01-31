using BurstPhoto.Core.Models;
using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering.Debug;

/// <summary>
/// Helper class for dumping intermediate textures and analyzing pipeline output.
/// Extracted from VulkanComputePipeline for better code organization.
/// </summary>
public class PipelineDebugHelper
{
    /// <summary>
    /// Whether debug dumping is enabled. When false, all dump methods return immediately.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Output directory for debug DNG files.
    /// </summary>
    public string OutputDirectory { get; set; } = "DebugOutput";

    /// <summary>
    /// Dumps a VulkanImage to a DNG file for debugging purposes.
    /// For single-channel (R32Sfloat) textures, outputs directly.
    /// For multi-channel (RGBA) textures, extracts the first channel.
    /// </summary>
    public void DumpTexture(VulkanImage image, string stepName, RawImage refMeta, int outWidth, int outHeight, int pad)
    {
        if (!Enabled) return;

        try
        {
            // Ensure output directory exists
            if (!Directory.Exists(OutputDirectory))
            {
                Directory.CreateDirectory(OutputDirectory);
            }

            var outputPath = Path.Combine(OutputDirectory, $"{stepName}.dng");
            Console.WriteLine($"[DebugDump] Saving {stepName} to {outputPath}...");

            // Get float data from the image
            float[] floatData;
            var isRgba = image.Format == Format.R32G32B32A32Sfloat;

            if (isRgba)
            {
                // For RGBA textures (like FFT results), extract just the first channel
                var rgba = image.GetData<float>();
                var pixelCount = (int)(image.Width * image.Height);
                floatData = new float[pixelCount];
                for (var i = 0; i < pixelCount; i++)
                {
                    floatData[i] = rgba[i * 4]; // Take R channel only
                }
            }
            else
            {
                floatData = image.GetData<float>();
            }

            // Convert float to ushort, cropping to original dimensions
            var width = refMeta.Width;
            var height = refMeta.Height;
            var outputData = new ushort[width * height];

            var srcWidth = (int)image.Width;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var srcIdx = (y + pad) * srcWidth + (x + pad);
                    var dstIdx = y * width + x;

                    if (srcIdx >= 0 && srcIdx < floatData.Length)
                    {
                        var val = floatData[srcIdx];
                        outputData[dstIdx] = (ushort)Math.Clamp(val, 0, 65535);
                    }
                }
            }

            // Create a RawImage for the DNG writer
            var debugImage = new RawImage
            {
                Width = width,
                Height = height,
                Data = outputData,
                MosaicPatternWidth = refMeta.MosaicPatternWidth,
                WhiteLevel = refMeta.WhiteLevel,
                BlackLevel = refMeta.BlackLevel,
                ExposureBias = refMeta.ExposureBias,
                IsoExposureTime = refMeta.IsoExposureTime,
                ColorFactors = refMeta.ColorFactors,
                SourcePath = refMeta.SourcePath, // Critical for DngSdkWriter
                CfaPattern = refMeta.CfaPattern,
                ColorMatrix1 = refMeta.ColorMatrix1,
                ColorMatrix2 = refMeta.ColorMatrix2,
                CalibrationIlluminant1 = refMeta.CalibrationIlluminant1,
                CalibrationIlluminant2 = refMeta.CalibrationIlluminant2,
                AsShotNeutral = refMeta.AsShotNeutral,
                CameraMake = refMeta.CameraMake,
                CameraModel = refMeta.CameraModel,
                IsBayerData = refMeta.IsBayerData
            };

            // Write using LibTiffDngWriter to avoid native dependency for debug dumps
            var writer = new BurstPhoto.Core.Implementations.LibTiffDngWriter();
            writer.Write(outputPath, debugImage);

            Console.WriteLine($"[DebugDump] Saved {stepName} successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DebugDump] Error saving {stepName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug: Saves an RGBA texture as a Bayer-pattern DNG file.
    /// RGBA channels are mapped to 2x2 Bayer pattern (R->top-left, G1->top-right, G2->bottom-left, B->bottom-right).
    /// </summary>
    public void DumpRgbaTexture(VulkanImage rgbaImage, string stepName, RawImage refMeta)
    {
        if (!Enabled) return;

        try
        {
            if (!Directory.Exists(OutputDirectory))
            {
                Directory.CreateDirectory(OutputDirectory);
            }

            var outputPath = Path.Combine(OutputDirectory, $"{stepName}.dng");
            Console.WriteLine($"[DebugDump] Saving RGBA {stepName} to {outputPath}...");

            // Get RGBA data
            var rgbaData = rgbaImage.GetData<float>();
            var rgbaWidth = (int)rgbaImage.Width;
            var rgbaHeight = (int)rgbaImage.Height;

            // Convert RGBA to Bayer (2x dimensions)
            var bayerWidth = rgbaWidth * 2;
            var bayerHeight = rgbaHeight * 2;
            var outputData = new ushort[bayerWidth * bayerHeight];

            for (var y = 0; y < rgbaHeight; y++)
            {
                for (var x = 0; x < rgbaWidth; x++)
                {
                    var rgbaIdx = (y * rgbaWidth + x) * 4;
                    var r = rgbaData[rgbaIdx + 0];
                    var g1 = rgbaData[rgbaIdx + 1];
                    var g2 = rgbaData[rgbaIdx + 2];
                    var b = rgbaData[rgbaIdx + 3];

                    // Map to 2x2 Bayer pattern (RGGB)
                    var bx = x * 2;
                    var by = y * 2;
                    outputData[by * bayerWidth + bx] = (ushort)Math.Clamp(r, 0, 65535);           // R
                    outputData[by * bayerWidth + bx + 1] = (ushort)Math.Clamp(g1, 0, 65535);     // G1
                    outputData[(by + 1) * bayerWidth + bx] = (ushort)Math.Clamp(g2, 0, 65535);   // G2
                    outputData[(by + 1) * bayerWidth + bx + 1] = (ushort)Math.Clamp(b, 0, 65535); // B
                }
            }

            // Create a RawImage for the DNG writer
            var debugImage = new RawImage
            {
                Width = bayerWidth,
                Height = bayerHeight,
                Data = outputData,
                MosaicPatternWidth = 2,
                WhiteLevel = refMeta.WhiteLevel,
                BlackLevel = refMeta.BlackLevel,
                ExposureBias = refMeta.ExposureBias,
                IsoExposureTime = refMeta.IsoExposureTime,
                ColorFactors = refMeta.ColorFactors,
                SourcePath = refMeta.SourcePath,
                CfaPattern = refMeta.CfaPattern,
                ColorMatrix1 = refMeta.ColorMatrix1,
                ColorMatrix2 = refMeta.ColorMatrix2,
                CalibrationIlluminant1 = refMeta.CalibrationIlluminant1,
                CalibrationIlluminant2 = refMeta.CalibrationIlluminant2,
                AsShotNeutral = refMeta.AsShotNeutral,
                CameraMake = refMeta.CameraMake,
                CameraModel = refMeta.CameraModel,
                IsBayerData = true
            };

            var writer = new BurstPhoto.Core.Implementations.LibTiffDngWriter();
            writer.Write(outputPath, debugImage);

            Console.WriteLine($"[DebugDump] Saved RGBA {stepName} ({bayerWidth}x{bayerHeight} Bayer) successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DebugDump] Error saving RGBA {stepName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug: Saves alignment vectors as a visualization DNG.
    /// X displacement shown in R, Y displacement shown in G (scaled and biased to be visible).
    /// </summary>
    public void DumpAlignment(VulkanImage alignment, string stepName, RawImage refMeta)
    {
        if (!Enabled) return;

        try
        {
            if (!Directory.Exists(OutputDirectory))
            {
                Directory.CreateDirectory(OutputDirectory);
            }

            var outputPath = Path.Combine(OutputDirectory, $"{stepName}.dng");
            Console.WriteLine($"[DebugDump] Saving alignment {stepName} to {outputPath}...");

            // Get int16 alignment data (RGBA format: x, y, 0, 0)
            var alignData = alignment.GetData<short>();
            var alignWidth = (int)alignment.Width;
            var alignHeight = (int)alignment.Height;

            // Analyze alignment data
            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            for (var i = 0; i < alignData.Length; i += 4)
            {
                var x = alignData[i];
                var y = alignData[i + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            Console.WriteLine($"[DebugDump] Alignment range: X=[{minX}, {maxX}], Y=[{minY}, {maxY}]");

            // Create Bayer visualization (2x dimensions)
            var bayerWidth = alignWidth * 2;
            var bayerHeight = alignHeight * 2;
            var outputData = new ushort[bayerWidth * bayerHeight];

            // Scale factor: map alignment range to 0-65535
            var scaleX = (maxX != minX) ? 65535f / (maxX - minX) : 1f;
            var scaleY = (maxY != minY) ? 65535f / (maxY - minY) : 1f;

            for (var y = 0; y < alignHeight; y++)
            {
                for (var x = 0; x < alignWidth; x++)
                {
                    var alignIdx = (y * alignWidth + x) * 4;
                    var dx = alignData[alignIdx];
                    var dy = alignData[alignIdx + 1];

                    // Scale to visible range
                    var rVal = (ushort)Math.Clamp((dx - minX) * scaleX, 0, 65535);
                    var gVal = (ushort)Math.Clamp((dy - minY) * scaleY, 0, 65535);
                    var bVal = (ushort)32768; // Neutral

                    // Map to 2x2 Bayer pattern (RGGB)
                    var bx = x * 2;
                    var by = y * 2;
                    outputData[by * bayerWidth + bx] = rVal;           // R (dx)
                    outputData[by * bayerWidth + bx + 1] = gVal;       // G1 (dy)
                    outputData[(by + 1) * bayerWidth + bx] = gVal;     // G2 (dy)
                    outputData[(by + 1) * bayerWidth + bx + 1] = bVal; // B (neutral)
                }
            }

            var debugImage = new RawImage
            {
                Width = bayerWidth,
                Height = bayerHeight,
                Data = outputData,
                MosaicPatternWidth = 2,
                WhiteLevel = 65535,
                BlackLevel = refMeta.BlackLevel,
                ExposureBias = 0,
                IsoExposureTime = 1.0f,
                ColorFactors = refMeta.ColorFactors,
                SourcePath = refMeta.SourcePath,
                CfaPattern = refMeta.CfaPattern,
                ColorMatrix1 = refMeta.ColorMatrix1,
                ColorMatrix2 = refMeta.ColorMatrix2,
                CalibrationIlluminant1 = refMeta.CalibrationIlluminant1,
                CalibrationIlluminant2 = refMeta.CalibrationIlluminant2,
                AsShotNeutral = refMeta.AsShotNeutral,
                CameraMake = "Debug",
                CameraModel = "AlignmentVisualization",
                IsBayerData = true
            };

            var writer = new BurstPhoto.Core.Implementations.LibTiffDngWriter();
            writer.Write(outputPath, debugImage);

            Console.WriteLine($"[DebugDump] Saved alignment {stepName} ({bayerWidth}x{bayerHeight} Bayer) successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DebugDump] Error saving alignment {stepName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Analyzes tile boundary patterns in the final Bayer accumulator.
    /// Checks for systematic differences at tile boundaries vs centers.
    /// </summary>
    public void AnalyzeBayerTileBoundaries(float[] bayerData, int width, int height, int padX, int padY, int bayerTileSize)
    {
        Console.WriteLine($"[BAYER_TILE_DIAG] Analyzing Bayer accumulator for tile boundary artifacts...");
        Console.WriteLine($"[BAYER_TILE_DIAG] Dimensions: {width}x{height}, Padding: ({padX},{padY}), BayerTileSize: {bayerTileSize}");

        // Sample region (skip padding)
        var startX = padX + 100;
        var startY = padY + 100;
        var endX = Math.Min(startX + 400, width - padX - 100);
        var endY = Math.Min(startY + 400, height - padY - 100);

        if (endX <= startX || endY <= startY)
        {
            Console.WriteLine($"[BAYER_TILE_DIAG] Sample region too small, skipping analysis");
            return;
        }

        // Collect statistics for different positions relative to tile boundaries
        // Position 0 = at boundary, Position 1-7 = inside tile
        var positionSums = new double[bayerTileSize];
        var positionCounts = new int[bayerTileSize];

        for (var y = startY; y < endY; y++)
        {
            for (var x = startX; x < endX; x++)
            {
                var idx = y * width + x;
                if (idx >= bayerData.Length)
                {
                    continue;
                }

                var val = bayerData[idx];

                // Calculate position within tile (0 = boundary)
                var posInTileX = x % bayerTileSize;
                var posInTileY = y % bayerTileSize;

                // Use minimum distance to any boundary
                var distToXBoundary = Math.Min(posInTileX, bayerTileSize - 1 - posInTileX);
                var distToYBoundary = Math.Min(posInTileY, bayerTileSize - 1 - posInTileY);
                var minDist = Math.Min(distToXBoundary, distToYBoundary);

                positionSums[minDist] += val;
                positionCounts[minDist]++;
            }
        }

        Console.WriteLine($"[BAYER_TILE_DIAG] Value by distance from tile boundary:");
        for (var d = 0; d < bayerTileSize / 2 + 1 && d < positionSums.Length; d++)
        {
            if (positionCounts[d] > 0)
            {
                var mean = positionSums[d] / positionCounts[d];
                Console.WriteLine($"  Distance {d}: mean={mean:F2}, count={positionCounts[d]}");
            }
        }

        // Calculate boundary vs center ratio
        var boundaryMean = positionCounts[0] > 0 ? positionSums[0] / positionCounts[0] : 0;
        double centerMean = 0;
        var centerCount = 0;
        for (var d = 2; d < bayerTileSize / 2 && d < positionSums.Length; d++)
        {
            centerMean += positionSums[d];
            centerCount += positionCounts[d];
        }
        centerMean = centerCount > 0 ? centerMean / centerCount : 0;

        Console.WriteLine($"[BAYER_TILE_DIAG] Summary: boundaryMean={boundaryMean:F2}, centerMean={centerMean:F2}, ratio={boundaryMean/Math.Max(1, centerMean):F4}");

        // Check for a specific horizontal line artifact
        // Sample values along a horizontal line at Y where Y % bayerTileSize == 0 vs Y % bayerTileSize == bayerTileSize/2
        var lineYBoundary = ((startY / bayerTileSize) + 1) * bayerTileSize; // First tile boundary after startY
        var lineYCenter = lineYBoundary + bayerTileSize / 2;

        if (lineYBoundary >= endY || lineYCenter >= endY)
        {
            return;
        }

        double boundaryLineSum = 0;
        double centerLineSum = 0;
        var lineCount = 0;

        for (var x = startX; x < endX; x++)
        {
            var idxBoundary = lineYBoundary * width + x;
            var idxCenter = lineYCenter * width + x;

            if (idxBoundary >= bayerData.Length || idxCenter >= bayerData.Length)
            {
                continue;
            }
            boundaryLineSum += bayerData[idxBoundary];
            centerLineSum += bayerData[idxCenter];
            lineCount++;
        }

        if (lineCount <= 0)
        {
            return;
        }
        Console.WriteLine($"[BAYER_TILE_DIAG] Horizontal line comparison (Y={lineYBoundary} vs Y={lineYCenter}):");
        Console.WriteLine($"  Boundary line mean: {boundaryLineSum/lineCount:F2}");
        Console.WriteLine($"  Center line mean: {centerLineSum/lineCount:F2}");
        Console.WriteLine($"  Ratio: {(boundaryLineSum/lineCount) / Math.Max(1, centerLineSum/lineCount):F4}");
    }

    /// <summary>
    /// Analyzes tile border values to diagnose tile boundary artifacts.
    /// Compares values at tile borders vs tile centers.
    /// </summary>
    public void AnalyzeTileBorders(float[] rgbaData, int rgbaWidth, int rgbaHeight, int tileSize, int iteration, string phase)
    {
        // RGBA data has 4 channels per pixel
        const int channels = 4;
        var stride = rgbaWidth * channels;

        // Sample a few tiles to analyze border vs center values
        var numTilesX = rgbaWidth / tileSize;
        var numTilesY = rgbaHeight / tileSize;

        // Collect statistics for borders and centers
        double borderSum = 0, borderAbsSum = 0;
        double centerSum = 0, centerAbsSum = 0;
        int borderCount = 0, centerCount = 0;
        double borderMin = double.MaxValue, borderMax = double.MinValue;
        double centerMin = double.MaxValue, centerMax = double.MinValue;

        // Sample tiles (skip first and last to avoid edge effects)
        var sampleTileX1 = Math.Min(5, numTilesX - 2);
        var sampleTileX2 = Math.Min(10, numTilesX - 2);
        var sampleTileY1 = Math.Min(5, numTilesY - 2);
        var sampleTileY2 = Math.Min(10, numTilesY - 2);

        for (var tileY = sampleTileY1; tileY <= sampleTileY2 && tileY < numTilesY; tileY++)
        {
            for (var tileX = sampleTileX1; tileX <= sampleTileX2 && tileX < numTilesX; tileX++)
            {
                var tileStartX = tileX * tileSize;
                var tileStartY = tileY * tileSize;

                for (var dy = 0; dy < tileSize; dy++)
                {
                    for (var dx = 0; dx < tileSize; dx++)
                    {
                        var px = tileStartX + dx;
                        var py = tileStartY + dy;
                        var idx = (py * rgbaWidth + px) * channels;

                        if (idx + 3 >= rgbaData.Length) continue;

                        // Sample R channel (index 0)
                        var val = rgbaData[idx];

                        var isBorder = (dx == 0 || dx == tileSize - 1 || dy == 0 || dy == tileSize - 1);

                        if (isBorder)
                        {
                            borderSum += val;
                            borderAbsSum += Math.Abs(val);
                            borderMin = Math.Min(borderMin, val);
                            borderMax = Math.Max(borderMax, val);
                            borderCount++;
                        }
                        else
                        {
                            centerSum += val;
                            centerAbsSum += Math.Abs(val);
                            centerMin = Math.Min(centerMin, val);
                            centerMax = Math.Max(centerMax, val);
                            centerCount++;
                        }
                    }
                }
            }
        }

        var borderMean = borderCount > 0 ? borderSum / borderCount : 0;
        var borderAbsMean = borderCount > 0 ? borderAbsSum / borderCount : 0;
        var centerMean = centerCount > 0 ? centerSum / centerCount : 0;
        var centerAbsMean = centerCount > 0 ? centerAbsSum / centerCount : 0;

        Console.WriteLine($"[TILE_BORDER_DIAG] Iteration {iteration} {phase}:");
        Console.WriteLine($"  Border pixels ({borderCount}): mean={borderMean:F2}, |mean|={borderAbsMean:F2}, min={borderMin:F2}, max={borderMax:F2}");
        Console.WriteLine($"  Center pixels ({centerCount}): mean={centerMean:F2}, |mean|={centerAbsMean:F2}, min={centerMin:F2}, max={centerMax:F2}");
        Console.WriteLine($"  Ratio (border/center): mean={borderMean/Math.Max(1, centerMean):F4}, |mean|={borderAbsMean/Math.Max(1, centerAbsMean):F4}");

        // Also sample specific tile boundaries to see discontinuities
        // Look at the boundary between tile (5,5) and tile (6,5)
        if (sampleTileX1 >= numTilesX - 1 || sampleTileY1 >= numTilesY)
        {
            return;
        }

        var boundaryX = (sampleTileX1 + 1) * tileSize; // First column of tile (6,5)
        var midY = sampleTileY1 * tileSize + tileSize / 2;

        var idxLeft = (midY * rgbaWidth + boundaryX - 1) * channels;  // Last col of tile (5,5)
        var idxRight = (midY * rgbaWidth + boundaryX) * channels;      // First col of tile (6,5)

        if (idxLeft < 0 || idxRight + 3 >= rgbaData.Length)
        {
            return;
        }

        var leftVal = rgbaData[idxLeft];
        var rightVal = rgbaData[idxRight];
        Console.WriteLine($"  Boundary sample at ({boundaryX-1},{midY})->({boundaryX},{midY}): left={leftVal:F2}, right={rightVal:F2}, diff={Math.Abs(rightVal-leftVal):F2}");
    }
}
