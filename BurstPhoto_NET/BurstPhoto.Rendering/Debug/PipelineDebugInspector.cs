using BurstPhoto.Core.Models;
using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering.Debug;

/// <summary>
/// Handles inline debug inspection of pipeline textures and data.
/// This includes sampling texture data for debug logging, which requires GPU->CPU transfers.
/// All methods are no-ops when Enabled is false, avoiding any GPU stalls.
///
/// PERFORMANCE NOTE: When enabled, these methods cause significant GPU stalls due to
/// GetData() calls. Only enable for debugging, never in production.
/// </summary>
public class PipelineDebugInspector
{
    /// <summary>
    /// Whether debug inspection is enabled. When false, all methods return immediately
    /// without performing any GPU operations.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Logs a formatted debug message with a prefix.
    /// </summary>
    public void Log(string message)
    {
        if (!Enabled) return;
        Console.WriteLine($"[DEBUG] {message}");
    }

    /// <summary>
    /// Samples raw input data and logs statistics.
    /// </summary>
    public void InspectRawInput(ushort[] data, int iteration, int sampleSize = 10000)
    {
        if (!Enabled) return;

        var actualSampleSize = Math.Min(data.Length, sampleSize);
        var rawSum = 0L;
        for (var i = 0; i < actualSampleSize; i++)
        {
            rawSum += data[i];
        }
        Console.WriteLine($"[DEBUG] Iteration {iteration}: Raw input data: sum(first {actualSampleSize})={rawSum}, mean={rawSum / (double)actualSampleSize:F2}");
    }

    /// <summary>
    /// Inspects a prepared (float) texture and logs statistics from multiple regions.
    /// </summary>
    public void InspectPreparedTexture(VulkanImage texture, int iteration, int padLeft, int padTop, int width, int height)
    {
        if (!Enabled) return;

        var data = texture.GetData<float>();
        var texWidth = (int)texture.Width;

        // Mid-point sample (avoid padding)
        var startIdx = data.Length / 4;
        var sampleSize = Math.Min(10000, data.Length - startIdx);
        double midSum = 0;
        for (var i = 0; i < sampleSize; i++)
        {
            midSum += Math.Abs(data[startIdx + i]);
        }
        Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare: sum(mid {sampleSize})={midSum:F2}, mean={midSum / sampleSize:F4}");

        // Center region sample
        var rowStart = (padTop + height / 2) * texWidth + (padLeft + width / 2);
        double centerSum = 0;
        var centerSamples = Math.Min(1000, data.Length - rowStart);
        if (rowStart >= 0 && rowStart < data.Length)
        {
            for (var i = 0; i < centerSamples; i++)
            {
                centerSum += Math.Abs(data[rowStart + i]);
            }
        }
        Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare (center region): sum={centerSum:F2}, mean={centerSum / 1000.0:F4}");

        // Left edge sample
        var leftEdgeRow = padTop + (height / 2);
        var leftEdgeStart = leftEdgeRow * texWidth + padLeft;
        double leftEdgeSum = 0;
        var leftEdgeSamples = Math.Min(100, data.Length - leftEdgeStart);
        if (leftEdgeStart >= 0 && leftEdgeStart < data.Length)
        {
            for (var i = 0; i < leftEdgeSamples; i++)
            {
                leftEdgeSum += Math.Abs(data[leftEdgeStart + i]);
            }
        }
        Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare (LEFT EDGE row {leftEdgeRow}): sum={leftEdgeSum:F2}, mean={leftEdgeSum / 100.0:F4}");

        if (midSum < 0.01)
        {
            Console.WriteLine($"[WARNING] Prepare produced near-zero output!");
        }
    }

    /// <summary>
    /// Inspects an RGBA texture and logs comprehensive statistics.
    /// </summary>
    public void InspectRgbaTexture(VulkanImage texture, int iteration, string label)
    {
        if (!Enabled) return;

        var rgbaData = texture.GetData<float>();
        var rgbaWidth = (int)texture.Width;
        var rgbaHeight = (int)texture.Height;

        // Mid sample
        var startIdx = rgbaData.Length / 4;
        var sampleSize = Math.Min(10000, rgbaData.Length - startIdx);
        double rgbaSumMid = 0;
        for (var i = 0; i < sampleSize; i++)
        {
            rgbaSumMid += Math.Abs(rgbaData[startIdx + i]);
        }

        // Total sum
        double rgbaTotal = 0;
        foreach (var t in rgbaData)
        {
            rgbaTotal += Math.Abs(t);
        }

        // Find first non-zero row
        var firstNonZeroRow = -1;
        for (var row = 0; row < rgbaHeight && firstNonZeroRow < 0; row++)
        {
            double rowSum = 0;
            var rowStart = row * rgbaWidth * 4;
            for (var col = 0; col < Math.Min(100, rgbaWidth); col++)
            {
                var idx = rowStart + col * 4;
                if (idx < rgbaData.Length)
                {
                    rowSum += Math.Abs(rgbaData[idx]);
                }
            }
            if (rowSum > 0.01)
            {
                firstNonZeroRow = row;
            }
        }

        Console.WriteLine($"[DEBUG] Iteration {iteration}: {label}: mid10k={rgbaSumMid:F2}, TOTAL={rgbaTotal:F2}, firstNonZeroRow={firstNonZeroRow}");
        if (rgbaTotal < 0.01)
        {
            Console.WriteLine($"[WARNING] {label} produced COMPLETELY ZERO output!");
        }
    }

    /// <summary>
    /// Inspects FFT output texture and logs statistics.
    /// </summary>
    public void InspectFftOutput(VulkanImage texture, int iteration, string label)
    {
        if (!Enabled) return;

        var fftData = texture.GetData<float>();
        var sampleSize = Math.Min(fftData.Length, 10000);

        // First 10k
        double fftSumFirst = 0;
        for (var i = 0; i < sampleSize; i++)
        {
            fftSumFirst += Math.Abs(fftData[i]);
        }

        // Mid sample
        double fftSumMid = 0;
        var midStart = fftData.Length / 2;
        for (var i = 0; i < sampleSize && midStart + i < fftData.Length; i++)
        {
            fftSumMid += Math.Abs(fftData[midStart + i]);
        }

        // Total
        double fftTotal = 0;
        foreach (var t in fftData)
        {
            fftTotal += Math.Abs(t);
        }

        Console.WriteLine($"[DEBUG] Iteration {iteration}: {label}: first10k={fftSumFirst:F2}, mid10k={fftSumMid:F2}, TOTAL={fftTotal:F2}");
        if (fftTotal < 0.01)
        {
            Console.WriteLine($"[WARNING] {label} produced COMPLETELY ZERO output!");
        }
    }

    /// <summary>
    /// Inspects warp input/output and logs before/after statistics.
    /// </summary>
    public void InspectWarpOperation(VulkanImage input, VulkanImage output, int padLeft, int padTop, int iterOutWidth)
    {
        if (!Enabled) return;

        // Check input before warp
        var prepAltData = input.GetData<float>();
        var dataStartIdx = padTop * iterOutWidth + padLeft;
        double prepAltSum = 0;
        var samples = Math.Min(1000, prepAltData.Length - dataStartIdx);
        if (dataStartIdx >= 0 && dataStartIdx < prepAltData.Length)
        {
            for (var idx = 0; idx < samples; idx++)
            {
                prepAltSum += Math.Abs(prepAltData[dataStartIdx + idx]);
            }
        }
        Console.WriteLine($"[WARP DEBUG] Input BEFORE warp (at data region): sum={prepAltSum:F2}, mean={prepAltSum / samples:F4}");
        if (prepAltSum < 0.01)
        {
            Console.WriteLine($"[WARP DEBUG] ERROR: Input is EMPTY before warp!");
        }

        // Check output after warp
        var warpData = output.GetData<float>();
        double warpSum = 0;
        var warpSamples = Math.Min(warpData.Length, 1000);
        for (var i = 0; i < warpSamples; i++)
        {
            warpSum += Math.Abs(warpData[i]);
        }

        double warpDataSum = 0;
        samples = Math.Min(1000, warpData.Length - dataStartIdx);
        if (dataStartIdx >= 0 && dataStartIdx < warpData.Length)
        {
            for (var idx = 0; idx < samples; idx++)
            {
                warpDataSum += Math.Abs(warpData[dataStartIdx + idx]);
            }
        }

        Console.WriteLine($"[WARP DEBUG] Output AFTER warp (first 1000): sum={warpSum:F2}, mean={warpSum / warpSamples:F4}");
        Console.WriteLine($"[WARP DEBUG] Output AFTER warp (at data region): sum={warpDataSum:F2}, mean={warpDataSum / samples:F4}");
        if (warpSum < 0.01 && warpDataSum < 0.01)
        {
            Console.WriteLine($"[WARP DEBUG] ERROR: Output is EMPTY after warp!");
        }
    }

    /// <summary>
    /// Inspects aligned RGBA texture after conversion.
    /// </summary>
    public void InspectAlignedRgba(VulkanImage texture, int rgbaWidth, int rgbaHeight)
    {
        if (!Enabled) return;

        var rgbaData = texture.GetData<float>();
        double rgbaSum = 0;
        double rgbaSumMid = 0;
        var rgbaSamples = Math.Min(rgbaData.Length, 1000);
        var midStart = rgbaData.Length / 2;

        for (var i = 0; i < rgbaSamples; i++)
        {
            rgbaSum += Math.Abs(rgbaData[i]);
        }
        for (var i = 0; i < rgbaSamples && midStart + i < rgbaData.Length; i++)
        {
            rgbaSumMid += Math.Abs(rgbaData[midStart + i]);
        }

        Console.WriteLine($"[WARP DEBUG] alignedTextureRgba AFTER convert: first1000 sum={rgbaSum:F2}, mid1000 sum={rgbaSumMid:F2}");
        Console.WriteLine($"[WARP DEBUG]   Total size={rgbaData.Length} floats ({rgbaWidth}x{rgbaHeight}x4)");
    }

    /// <summary>
    /// Inspects deconvolution before/after.
    /// </summary>
    public void InspectDeconvolution(VulkanImage texture, int iteration, bool isBefore)
    {
        if (!Enabled) return;

        var data = texture.GetData<float>();
        double total = 0;
        foreach (var t in data)
        {
            total += Math.Abs(t);
        }

        var phase = isBefore ? "Before" : "After";
        Console.WriteLine($"[DEBUG] Iteration {iteration}: {phase} deconvolution: TOTAL={total:F2}, mean={total / data.Length:F4}");

        if (!isBefore && total < 0.01)
        {
            Console.WriteLine($"[WARNING] Deconvolution produced near-zero output!");
        }
    }

    /// <summary>
    /// Inspects backward FFT output with shader debug info decoding.
    /// </summary>
    public void InspectBackwardFftOutput(VulkanImage texture, int iteration, int tileSizeMerge, int rgbaWidth, int rgbaHeight)
    {
        if (!Enabled) return;

        var backFftData = texture.GetData<float>();

        // Decode debug info from corner pixels
        Console.WriteLine("[DEBUG] === Shader Debug Info (backward_fft) ===");
        Console.WriteLine("[DEBUG] First 16x16 pixels encode: R=threadX, G=threadY, B=nTilesX, A=nTilesY");

        var threadX00 = backFftData[0];
        var threadY00 = backFftData[1];
        var shaderNTilesX = backFftData[2];
        var shaderNTilesY = backFftData[3];
        Console.WriteLine($"[DEBUG] Pixel(0,0): threadID=({threadX00:F0},{threadY00:F0}), shader_nTilesX={shaderNTilesX:F0}, shader_nTilesY={shaderNTilesY:F0}");
        Console.WriteLine($"[DEBUG] Dispatched: {rgbaWidth / tileSizeMerge}x{rgbaHeight / tileSizeMerge} threads (for {rgbaWidth}x{rgbaHeight} texture, tileSize={tileSizeMerge})");

        // Check for threads beyond expected range
        var foundBeyond128 = false;
        int maxThreadX = 0, maxThreadY = 0;
        for (var y = 0; y < 16 && y < rgbaHeight; y++)
        {
            for (var x = 0; x < 16 && x < rgbaWidth; x++)
            {
                var idx = (y * rgbaWidth + x) * 4;
                var threadX = backFftData[idx + 0];
                var threadY = backFftData[idx + 1];
                maxThreadX = Math.Max(maxThreadX, (int)threadX);
                maxThreadY = Math.Max(maxThreadY, (int)threadY);

                if (threadX >= 128 || threadY >= 96)
                {
                    foundBeyond128 = true;
                }
            }
        }
        Console.WriteLine($"[DEBUG] Max thread IDs in debug region: ({maxThreadX}, {maxThreadY})");
        if (!foundBeyond128)
        {
            Console.WriteLine("[DEBUG] WARNING: No threads with X>=128 or Y>=96 found in debug region!");
        }

        // Total sum
        double backFftTotal = 0;
        foreach (var t in backFftData)
        {
            backFftTotal += Math.Abs(t);
        }

        Console.WriteLine($"[DEBUG] Iteration {iteration}: After backward_fft: TOTAL={backFftTotal:F2}, mean={backFftTotal / backFftData.Length:F4}");
        if (backFftTotal < 0.01)
        {
            Console.WriteLine($"[WARNING] Backward FFT produced near-zero output!");
        }
    }

    /// <summary>
    /// Inspects Bayer output after convert_to_bayer.
    /// </summary>
    public void InspectBayerOutput(VulkanImage texture, int iteration)
    {
        if (!Enabled) return;

        var bayerData = texture.GetData<float>();
        double bayerTotal = 0;
        foreach (var t in bayerData)
        {
            bayerTotal += Math.Abs(t);
        }

        Console.WriteLine($"[DEBUG] Iteration {iteration}: After convert_to_bayer: TOTAL={bayerTotal:F2}, mean={bayerTotal / bayerData.Length:F4}");
        if (bayerTotal < 0.01)
        {
            Console.WriteLine($"[WARNING] Convert to Bayer produced near-zero output!");
        }
    }

    /// <summary>
    /// Inspects iteration output and accumulation.
    /// </summary>
    public void InspectIterationOutput(float[] iterOutput, int iteration)
    {
        if (!Enabled) return;

        double iterSum = 0;
        var sampleSize = Math.Min(iterOutput.Length, 100000);
        for (var i = 0; i < sampleSize; i++)
        {
            iterSum += iterOutput[i];
        }
        Console.WriteLine($"[DEBUG] Iteration {iteration} output: sum={iterSum:F2}, mean={iterSum / sampleSize:F2}");
    }

    /// <summary>
    /// Logs weight sum tracking information for cross-iteration analysis.
    /// </summary>
    public void LogWeightSumTracking(float[] iterOutput, int iteration, int cropLeft, int cropTop, int bayerWidth, int trackY = 60)
    {
        if (!Enabled) return;

        Console.WriteLine($"[WEIGHT_SUM] Iteration {iteration}: cropLeft={cropLeft} Bayer = {cropLeft / 2} RGBA");

        for (var finalX = 0; finalX < 8; finalX++)
        {
            var srcBayerX = cropLeft + finalX * 2;
            var srcBayerY = cropTop + trackY * 2;

            if (srcBayerY * bayerWidth + srcBayerX + 1 >= iterOutput.Length ||
                (srcBayerY + 1) * bayerWidth + srcBayerX + 1 >= iterOutput.Length)
                continue;

            double p0 = iterOutput[srcBayerY * bayerWidth + srcBayerX];
            double p1 = iterOutput[srcBayerY * bayerWidth + srcBayerX + 1];
            double p2 = iterOutput[(srcBayerY + 1) * bayerWidth + srcBayerX];
            double p3 = iterOutput[(srcBayerY + 1) * bayerWidth + srcBayerX + 1];
            var rgbaSum = p0 + p1 + p2 + p3;

            var srcRgbaX = srcBayerX / 2;
            var tileRelX = srcRgbaX % 8;

            Console.WriteLine($"[WEIGHT_SUM]   FinalX={finalX}: srcBayer={srcBayerX}, srcRgba={srcRgbaX}, tileRel={tileRelX}, sum={rgbaSum:F1}");
        }
    }

    /// <summary>
    /// Inspects accumulator state.
    /// </summary>
    public void InspectAccumulator(VulkanImage accumulator, int iteration, int padAlignX, int padAlignY, int accWidth, string phase)
    {
        if (!Enabled) return;

        var data = accumulator.GetData<float>();
        var dataStartIdx = padAlignY * accWidth + padAlignX;
        double sum = 0;
        var samples = Math.Min(10000, data.Length - dataStartIdx);

        for (var i = 0; i < samples; i++)
        {
            sum += Math.Abs(data[dataStartIdx + i]);
        }

        Console.WriteLine($"[DEBUG] Iteration {iteration}: Accumulator {phase}: sum={sum:F2} (at offset {dataStartIdx})");

        if (samples > 0 && phase.Contains("AFTER"))
        {
            Console.WriteLine($"[DEBUG] First 5 values at data region: {data[dataStartIdx]:F4}, {data[dataStartIdx + 1]:F4}, {data[dataStartIdx + 2]:F4}, {data[dataStartIdx + 3]:F4}, {data[dataStartIdx + 4]:F4}");
        }
    }

    /// <summary>
    /// Inspects final accumulator statistics.
    /// </summary>
    public void InspectFinalAccumulator(float[] floatData, int padAlignX, int padAlignY, int accWidth, int width, int height)
    {
        if (!Enabled) return;

        var dataStartIdx = padAlignY * accWidth + padAlignX;
        double sum = 0;
        double absSum = 0;
        var min = double.MaxValue;
        var max = double.MinValue;
        var dataRegionSize = Math.Min(width * height, floatData.Length - dataStartIdx);

        for (var i = 0; i < dataRegionSize; i++)
        {
            var val = floatData[dataStartIdx + i];
            sum += val;
            absSum += Math.Abs(val);
            if (val < min) min = val;
            if (val > max) max = val;
        }

        Console.WriteLine($"[DEBUG] FinalAccumulator stats (data region): sum={sum:F2}, absSum={absSum:F2}, min={min:F2}, max={max:F2}, mean={sum / dataRegionSize:F2}");
    }
}
