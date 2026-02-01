namespace BurstPhoto.Rendering.Validation;

/// <summary>
/// Result of an FFT pipeline validation test.
/// </summary>
public class ValidationResult
{
    public string StageName { get; set; } = "";
    public string TestName { get; set; } = "";
    public bool Passed { get; set; }
    public string? FailureReason { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    
    public override string ToString()
    {
        var status = Passed ? "✓ PASS" : "✗ FAIL";
        var result = $"[{StageName}] {TestName}: {status}";
        if (!Passed && !string.IsNullOrEmpty(FailureReason))
        {
            result += $"\n  Reason: {FailureReason}";
        }
        foreach (var metric in Metrics)
        {
            result += $"\n  • {metric.Key}: {metric.Value:G6}";
        }
        return result;
    }
}

/// <summary>
/// Mathematical validation of FFT pipeline stages using invariants like Parseval's theorem.
/// </summary>
public static class FftValidator
{
    /// <summary>
    /// Tolerance for floating-point comparisons (0.1% = 0.001)
    /// </summary>
    public const double DefaultTolerance = 0.01; // 1% tolerance for FFT operations
    
    /// <summary>
    /// Computes sum, sum-of-squares, min, max, and count for a float array.
    /// </summary>
    public static TextureStats ComputeStats(float[] data)
    {
        double sum = 0;
        double sumSquares = 0;
        var min = double.MaxValue;
        var max = double.MinValue;
        var count = data.Length;
        var nonZeroCount = 0;
        
        for (var i = 0; i < count; i++)
        {
            double v = data[i];
            sum += v;
            sumSquares += v * v;
            if (v < min) min = v;
            if (v > max) max = v;
            if (Math.Abs(v) > 1e-10) nonZeroCount++;
        }
        
        return new TextureStats
        {
            Sum = sum,
            SumOfSquares = sumSquares,
            Min = min,
            Max = max,
            Count = count,
            NonZeroCount = nonZeroCount,
            Mean = count > 0 ? sum / count : 0
        };
    }
    
    /// <summary>
    /// Computes stats for an RGBA texture (4 floats per pixel).
    /// Returns separate stats for each channel plus combined.
    /// </summary>
    public static RgbaTextureStats ComputeRgbaStats(float[] rgbaData)
    {
        var pixelCount = rgbaData.Length / 4;
        var r = new double[pixelCount];
        var g = new double[pixelCount];
        var b = new double[pixelCount];
        var a = new double[pixelCount];
        
        for (var i = 0; i < pixelCount; i++)
        {
            r[i] = rgbaData[i * 4 + 0];
            g[i] = rgbaData[i * 4 + 1];
            b[i] = rgbaData[i * 4 + 2];
            a[i] = rgbaData[i * 4 + 3];
        }
        
        // Combined energy (sum of squares of all components)
        double totalEnergy = 0;
        double totalSum = 0;
        for (var i = 0; i < pixelCount; i++)
        {
            totalEnergy += r[i] * r[i] + g[i] * g[i] + b[i] * b[i] + a[i] * a[i];
            totalSum += r[i] + g[i] + b[i] + a[i];
        }
        
        return new RgbaTextureStats
        {
            PixelCount = pixelCount,
            TotalSum = totalSum,
            TotalEnergy = totalEnergy,
            MeanPerChannel = totalSum / (4.0 * pixelCount)
        };
    }
    
    /// <summary>
    /// Computes stats for frequency domain data (complex, 2x width).
    /// Real values at even columns, imaginary at odd columns.
    /// </summary>
    public static FrequencyStats ComputeFrequencyStats(float[] ftData, int spatialWidth, int height)
    {
        // FT data is stored as 2*spatialWidth x height x 4 (RGBA, complex)
        // Each spatial position has Real at column 2*x, Imaginary at column 2*x+1
        var ftWidth = spatialWidth * 2;
        var pixelCount = spatialWidth * height;
        
        double totalEnergy = 0;  // Sum of |X[k]|^2 = Re^2 + Im^2
        double dcSumReal = 0;    // Sum of DC bins (real parts at frequency 0,0 of each tile)
        double dcSumImag = 0;    // Sum of DC bins (imaginary parts should be ~0)
        
        // For RGBA textures, we have 4 channels per pixel
        // FT stores: pixel[x] = (Re.r, Re.g, Re.b, Re.a) at even column, (Im.r, Im.g, Im.b, Im.a) at odd
        var floatsPerRow = ftWidth * 4; // ftWidth pixels, 4 floats each
        
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < spatialWidth; x++)
            {
                var reIdx = (y * ftWidth + 2 * x) * 4;     // Real pixel (4 floats: RGBA)
                var imIdx = (y * ftWidth + 2 * x + 1) * 4; // Imaginary pixel (4 floats: RGBA)
                
                if (reIdx + 3 < ftData.Length && imIdx + 3 < ftData.Length)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        double re = ftData[reIdx + c];
                        double im = ftData[imIdx + c];
                        totalEnergy += re * re + im * im;
                    }
                }
            }
        }
        
        return new FrequencyStats
        {
            TotalEnergy = totalEnergy,
            PixelCount = pixelCount
        };
    }
    
    /// <summary>
    /// Validates Parseval's theorem: Energy in spatial domain = Energy in frequency domain / N
    /// </summary>
    public static ValidationResult ValidateParseval(
        double spatialEnergy,
        double frequencyEnergy,
        int tileSize,
        string stageName)
    {
        // Parseval: sum(|x[n]|^2) = sum(|X[k]|^2) / N
        // For 2D FFT: N = tileSize * tileSize
        var n = tileSize * tileSize;
        var expectedFreqEnergy = spatialEnergy * n;

        var ratio = frequencyEnergy / expectedFreqEnergy;
        var diff = Math.Abs(ratio - 1.0);
        var passed = diff < DefaultTolerance;

        return new ValidationResult
        {
            StageName = stageName,
            TestName = "Parseval's Theorem",
            Passed = passed,
            FailureReason = passed ? null : $"Energy ratio {ratio:F4} differs from expected 1.0 by {diff * 100:F2}%",
            Metrics = new Dictionary<string, double>
            {
                ["SpatialEnergy"] = spatialEnergy,
                ["FrequencyEnergy"] = frequencyEnergy,
                ["ExpectedFreqEnergy"] = expectedFreqEnergy,
                ["Ratio"] = ratio,
                ["DiffPercent"] = diff * 100
            }
        };
    }

    /// <summary>
    /// Validates Parseval's theorem accounting for the Hann (cosine) window applied in forward FFT.
    /// The window is: w(x,y) = (0.5 - 0.5*cos(2π(x+0.5)/N)) * (0.5 - 0.5*cos(2π(y+0.5)/N))
    /// This destroys ~86% of energy, which is CORRECT behavior for windowed FFT.
    /// </summary>
    public static ValidationResult ValidateParsevalWithWindow(
        double spatialEnergy,
        double frequencyEnergy,
        int tileSize,
        string stageName)
    {
        // Calculate Hann window energy reduction factor
        // For a 1D Hann window: integral of w(x)^2 over [0,N] ≈ 3N/8
        // For 2D: windowEnergyFactor = (3N/8) * (3N/8) / N^2 = 9/64 ≈ 0.140625
        var windowEnergyFactor = 9.0 / 64.0; // Theoretical value for Hann window^2

        // For 2D FFT: Parseval's theorem with window:
        // sum(|w[n]*x[n]|^2) = sum(|X[k]|^2) / N
        // We measured spatialEnergy BEFORE windowing, so we need to apply the window factor
        var windowedSpatialEnergy = spatialEnergy * windowEnergyFactor;
        var n = tileSize * tileSize;
        var expectedFreqEnergy = windowedSpatialEnergy * n;

        var ratio = frequencyEnergy / expectedFreqEnergy;
        var diff = Math.Abs(ratio - 1.0);
        var passed = diff < 0.10; // Allow 10% tolerance since window calculation is approximate

        return new ValidationResult
        {
            StageName = stageName,
            TestName = "Parseval's Theorem (Window-Aware)",
            Passed = passed,
            FailureReason = passed ? null :
                $"Energy ratio {ratio:F4} differs from expected 1.0 by {diff * 100:F2}%. " +
                $"Window should reduce energy to ~{windowEnergyFactor:P1} of original.",
            Metrics = new Dictionary<string, double>
            {
                ["SpatialEnergy (raw)"] = spatialEnergy,
                ["Window Energy Factor"] = windowEnergyFactor,
                ["Windowed Spatial Energy"] = windowedSpatialEnergy,
                ["Frequency Energy"] = frequencyEnergy,
                ["Expected Freq Energy"] = expectedFreqEnergy,
                ["Ratio"] = ratio,
                ["DiffPercent"] = diff * 100
            }
        };
    }
    
    /// <summary>
    /// Validates round-trip: FFT(IFFT(x)) ≈ x
    /// Returns max absolute difference and percentage of pixels within tolerance.
    /// </summary>
    public static ValidationResult ValidateRoundTrip(
        float[] original, 
        float[] afterRoundTrip,
        double range,
        string stageName)
    {
        if (original.Length != afterRoundTrip.Length)
        {
            return new ValidationResult
            {
                StageName = stageName,
                TestName = "Round-Trip",
                Passed = false,
                FailureReason = $"Array length mismatch: {original.Length} vs {afterRoundTrip.Length}",
                Metrics = new Dictionary<string, double>()
            };
        }
        
        double maxDiff = 0;
        double sumDiff = 0;
        var pixelsWithinTolerance = 0;
        var tolerance = range * DefaultTolerance;
        
        for (var i = 0; i < original.Length; i++)
        {
            double diff = Math.Abs(original[i] - afterRoundTrip[i]);
            sumDiff += diff;
            if (diff > maxDiff) maxDiff = diff;
            if (diff <= tolerance) pixelsWithinTolerance++;
        }
        
        var percentWithinTolerance = 100.0 * pixelsWithinTolerance / original.Length;
        var avgDiff = sumDiff / original.Length;
        var passed = percentWithinTolerance > 99.0 && maxDiff < tolerance * 10;
        
        return new ValidationResult
        {
            StageName = stageName,
            TestName = "Round-Trip (Forward→Backward)",
            Passed = passed,
            FailureReason = passed ? null : $"Max diff {maxDiff:F2} > tolerance {tolerance:F2}, only {percentWithinTolerance:F1}% within tolerance",
            Metrics = new Dictionary<string, double>
            {
                ["MaxDiff"] = maxDiff,
                ["AvgDiff"] = avgDiff,
                ["Tolerance"] = tolerance,
                ["PixelsWithinTolerance%"] = percentWithinTolerance,
                ["Range"] = range
            }
        };
    }
    
    /// <summary>
    /// Validates that output mean matches expected DC / normalization factor
    /// </summary>
    public static ValidationResult ValidateDcComponent(
        double dcBinValue,
        double outputMean,
        int normFactor,
        string stageName)
    {
        var expectedMean = dcBinValue / normFactor;
        var ratio = outputMean / expectedMean;
        var diff = Math.Abs(ratio - 1.0);
        var passed = diff < DefaultTolerance;
        
        return new ValidationResult
        {
            StageName = stageName,
            TestName = "DC Component",
            Passed = passed,
            FailureReason = passed ? null : $"Output mean {outputMean:F2} vs expected {expectedMean:F2} (ratio: {ratio:F4})",
            Metrics = new Dictionary<string, double>
            {
                ["DCBinValue"] = dcBinValue,
                ["NormFactor"] = normFactor,
                ["ExpectedMean"] = expectedMean,
                ["ActualMean"] = outputMean,
                ["Ratio"] = ratio
            }
        };
    }
    
    /// <summary>
    /// Prints all validation results with clear formatting
    /// </summary>
    public static void PrintResults(IEnumerable<ValidationResult> results)
    {
        Console.WriteLine("\n=== FFT Pipeline Validation Results ===\n");
        
        var testNum = 1;
        var anyFailed = false;
        
        foreach (var result in results)
        {
            var emoji = result.Passed ? "✓" : "✗";
            var color = result.Passed ? "PASS" : "FAIL";
            
            Console.WriteLine($"[{testNum}] {result.StageName}: {result.TestName}");
            Console.WriteLine($"    Result: {emoji} {color}");
            
            if (!result.Passed)
            {
                anyFailed = true;
                Console.WriteLine($"    Reason: {result.FailureReason}");
            }
            
            foreach (var metric in result.Metrics)
            {
                Console.WriteLine($"    • {metric.Key}: {metric.Value:G6}");
            }
            Console.WriteLine();
            testNum++;
        }
        
        if (anyFailed)
        {
            Console.WriteLine(">>> VALIDATION FAILED - See failures above for diagnosis");
        }
        else
        {
            Console.WriteLine(">>> ALL VALIDATION TESTS PASSED");
        }
        Console.WriteLine();
    }
}

public struct TextureStats
{
    public double Sum;
    public double SumOfSquares;
    public double Min;
    public double Max;
    public int Count;
    public int NonZeroCount;
    public double Mean;
    
    public double Energy => SumOfSquares;
}

public struct RgbaTextureStats
{
    public int PixelCount;
    public double TotalSum;
    public double TotalEnergy;
    public double MeanPerChannel;
}

public struct FrequencyStats
{
    public double TotalEnergy;
    public int PixelCount;
}

/// <summary>
/// Diagnostic test: Compute the ACTUAL window energy factor from input data.
/// This tells us exactly what the shader is doing, not what we think it should do.
/// </summary>
public static class WindowDiagnostics
{
    /// <summary>
    /// Creates a test pattern with known energy, runs FFT, and measures actual window factor.
    /// This is PROOF of whether the windowing is working correctly.
    /// </summary>
    public static double MeasureActualWindowFactor(float[] originalData, float[] ftData, int width, int height, int tileSize)
    {
        // Compute energy before FFT
        var originalStats = FftValidator.ComputeRgbaStats(originalData);
        var inputEnergy = originalStats.TotalEnergy;

        // Compute energy after FFT
        var ftStats = FftValidator.ComputeFrequencyStats(ftData, width, height);
        var outputEnergy = ftStats.TotalEnergy;

        // The ratio tells us the ACTUAL window factor
        // Expected for Hann window: ~0.140625 (9/64)
        var n = tileSize * tileSize;
        var actualWindowFactor = outputEnergy / (inputEnergy * n);

        return actualWindowFactor;
    }

    /// <summary>
    /// Validates that the measured window factor matches theoretical Hann window.
    /// </summary>
    public static ValidationResult ValidateWindowFunction(
        double measuredFactor,
        int tileSize,
        string stageName)
    {
        var theoreticalHannFactor = 9.0 / 64.0; // ~0.140625
        var ratio = measuredFactor / theoreticalHannFactor;
        var diff = Math.Abs(ratio - 1.0);
        var passed = diff < 0.10; // 10% tolerance

        return new ValidationResult
        {
            StageName = stageName,
            TestName = "Window Function Diagnostic",
            Passed = passed,
            FailureReason = passed ? null :
                $"Measured window factor {measuredFactor:F6} differs from Hann window {theoreticalHannFactor:F6} by {diff * 100:F1}%",
            Metrics = new Dictionary<string, double>
            {
                ["Measured Window Factor"] = measuredFactor,
                ["Theoretical Hann Factor"] = theoreticalHannFactor,
                ["Ratio"] = ratio,
                ["DiffPercent"] = diff * 100,
                ["Interpretation"] = passed ?
                    1.0 : // "Window working correctly"
                    (measuredFactor < 0.05 ? -1.0 : 0.0) // "Shader writing zeros" : "Unknown issue"
            }
        };
    }
}
