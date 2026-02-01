using BurstPhoto.Rendering.Validation;

namespace BurstPhoto.Tests.TestHelpers;

/// <summary>
/// Image quality metrics for comparing pipeline outputs.
/// Extends FftValidator with additional metrics commonly used in image processing tests.
/// </summary>
public static class MetricsCalculator
{
    /// <summary>
    /// Calculates Mean Squared Error between two arrays.
    /// </summary>
    public static double CalculateMSE(float[] expected, float[] actual)
    {
        if (expected.Length != actual.Length)
            throw new ArgumentException($"Arrays must be same length. Expected: {expected.Length}, Actual: {actual.Length}");

        double sum = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var diff = expected[i] - actual[i];
            sum += diff * diff;
        }
        return sum / expected.Length;
    }

    /// <summary>
    /// Calculates Peak Signal-to-Noise Ratio.
    /// Higher is better. Typical good values: > 30 dB.
    /// Returns infinity if MSE is 0 (identical images).
    /// </summary>
    public static double CalculatePSNR(float[] expected, float[] actual, float maxValue = 65535f)
    {
        var mse = CalculateMSE(expected, actual);
        if (mse == 0) return double.PositiveInfinity;
        return 10 * Math.Log10(maxValue * maxValue / mse);
    }

    /// <summary>
    /// Calculates Structural Similarity Index (SSIM).
    /// Range: -1 to 1, where 1 = identical.
    /// This is a simplified global SSIM; for more accurate results, use windowed SSIM.
    /// </summary>
    public static double CalculateSSIM(
        float[] expected,
        float[] actual,
        int width,
        int height,
        float maxValue = 65535f)
    {
        if (expected.Length != actual.Length)
            throw new ArgumentException("Arrays must be same length");

        // Constants for stability (based on image dynamic range)
        var c1 = (0.01 * maxValue) * (0.01 * maxValue);
        var c2 = (0.03 * maxValue) * (0.03 * maxValue);

        // Calculate means
        var meanX = expected.Average();
        var meanY = actual.Average();

        // Calculate variances and covariance
        double varX = 0, varY = 0, covarXY = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var dx = expected[i] - meanX;
            var dy = actual[i] - meanY;
            varX += dx * dx;
            varY += dy * dy;
            covarXY += dx * dy;
        }
        varX /= expected.Length;
        varY /= expected.Length;
        covarXY /= expected.Length;

        // SSIM formula
        var numerator = (2 * meanX * meanY + c1) * (2 * covarXY + c2);
        var denominator = (meanX * meanX + meanY * meanY + c1) * (varX + varY + c2);

        return numerator / denominator;
    }

    /// <summary>
    /// Calculates total energy (sum of squares).
    /// Uses FftValidator.ComputeStats for efficiency.
    /// </summary>
    public static double CalculateEnergy(float[] data)
    {
        var stats = FftValidator.ComputeStats(data);
        return stats.Energy;
    }

    /// <summary>
    /// Calculates variance of the data.
    /// </summary>
    public static double CalculateVariance(float[] data)
    {
        var mean = data.Average();
        return data.Average(x => (x - mean) * (x - mean));
    }

    /// <summary>
    /// Calculates standard deviation.
    /// </summary>
    public static double CalculateStdDev(float[] data)
    {
        return Math.Sqrt(CalculateVariance(data));
    }

    /// <summary>
    /// Calculates mean absolute error.
    /// </summary>
    public static double CalculateMAE(float[] expected, float[] actual)
    {
        if (expected.Length != actual.Length)
            throw new ArgumentException("Arrays must be same length");

        double sum = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            sum += Math.Abs(expected[i] - actual[i]);
        }
        return sum / expected.Length;
    }

    /// <summary>
    /// Calculates the maximum absolute difference.
    /// </summary>
    public static double CalculateMaxDiff(float[] expected, float[] actual)
    {
        if (expected.Length != actual.Length)
            throw new ArgumentException("Arrays must be same length");

        double maxDiff = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var diff = Math.Abs(expected[i] - actual[i]);
            if (diff > maxDiff) maxDiff = diff;
        }
        return maxDiff;
    }

    /// <summary>
    /// Calculates percentage of pixels matching within tolerance.
    /// </summary>
    public static double CalculateMatchPercent(float[] expected, float[] actual, float tolerance)
    {
        if (expected.Length != actual.Length)
            throw new ArgumentException("Arrays must be same length");

        var matchCount = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) <= tolerance)
                matchCount++;
        }
        return 100.0 * matchCount / expected.Length;
    }

    /// <summary>
    /// Generates a summary report of comparison metrics.
    /// </summary>
    public static ComparisonReport CompareArrays(float[] expected, float[] actual, float maxValue = 65535f)
    {
        return new ComparisonReport
        {
            MSE = CalculateMSE(expected, actual),
            PSNR = CalculatePSNR(expected, actual, maxValue),
            MAE = CalculateMAE(expected, actual),
            MaxDiff = CalculateMaxDiff(expected, actual),
            MatchPercent1Percent = CalculateMatchPercent(expected, actual, maxValue * 0.01f),
            ExpectedMean = expected.Average(),
            ActualMean = actual.Average(),
            ExpectedStdDev = CalculateStdDev(expected),
            ActualStdDev = CalculateStdDev(actual)
        };
    }

    /// <summary>
    /// Calculates RGBA texture statistics per channel.
    /// Uses FftValidator.ComputeRgbaStats.
    /// </summary>
    public static RgbaTextureStats CalculateRgbaStats(float[] rgbaData)
    {
        return FftValidator.ComputeRgbaStats(rgbaData);
    }
}

/// <summary>
/// Report containing various comparison metrics between two images/arrays.
/// </summary>
public record ComparisonReport
{
    public double MSE { get; init; }
    public double PSNR { get; init; }
    public double MAE { get; init; }
    public double MaxDiff { get; init; }
    public double MatchPercent1Percent { get; init; }
    public double ExpectedMean { get; init; }
    public double ActualMean { get; init; }
    public double ExpectedStdDev { get; init; }
    public double ActualStdDev { get; init; }

    public override string ToString()
    {
        return $"""
            Comparison Report:
              MSE:       {MSE:G6}
              PSNR:      {PSNR:F2} dB
              MAE:       {MAE:G6}
              MaxDiff:   {MaxDiff:G6}
              Match (1%): {MatchPercent1Percent:F2}%
              Expected Mean: {ExpectedMean:G6} (StdDev: {ExpectedStdDev:G6})
              Actual Mean:   {ActualMean:G6} (StdDev: {ActualStdDev:G6})
            """;
    }
}
