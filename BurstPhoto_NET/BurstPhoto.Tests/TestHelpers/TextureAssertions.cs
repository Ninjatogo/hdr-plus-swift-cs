using BurstPhoto.Rendering;
using BurstPhoto.Rendering.Validation;

namespace BurstPhoto.Tests.TestHelpers;

/// <summary>
/// Custom assertions for comparing VulkanImage textures in unit tests.
/// Uses xUnit Assert internally.
/// </summary>
public static class TextureAssertions
{
    /// <summary>
    /// Asserts that two textures are approximately equal within tolerance.
    /// </summary>
    /// <param name="expected">Expected texture</param>
    /// <param name="actual">Actual texture</param>
    /// <param name="tolerance">Maximum allowed difference per element</param>
    /// <param name="message">Optional message for failure</param>
    public static void AssertTexturesEqual(
        VulkanImage expected,
        VulkanImage actual,
        float tolerance = 1e-4f,
        string message = "")
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Format, actual.Format);

        var expectedData = expected.GetData<float>();
        var actualData = actual.GetData<float>();

        AssertArraysEqual(expectedData, actualData, tolerance, message);
    }

    /// <summary>
    /// Asserts that two float arrays are approximately equal within tolerance.
    /// </summary>
    public static void AssertArraysEqual(
        float[] expected,
        float[] actual,
        float tolerance = 1e-4f,
        string message = "")
    {
        Assert.Equal(expected.Length, actual.Length);

        var maxDiff = 0f;
        var maxDiffIndex = -1;
        var diffCount = 0;

        for (var i = 0; i < expected.Length; i++)
        {
            var diff = Math.Abs(expected[i] - actual[i]);
            if (diff > tolerance)
            {
                diffCount++;
                if (diff > maxDiff)
                {
                    maxDiff = diff;
                    maxDiffIndex = i;
                }
            }
        }

        if (diffCount > 0)
        {
            var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";
            Assert.Fail($"{prefix}Arrays differ at {diffCount} elements. " +
                        $"Max diff: {maxDiff} at index {maxDiffIndex}, " +
                        $"expected {expected[maxDiffIndex]}, got {actual[maxDiffIndex]}. " +
                        $"Tolerance: {tolerance}");
        }
    }

    /// <summary>
    /// Asserts that texture values are within expected range.
    /// </summary>
    public static void AssertTextureInRange(
        VulkanImage texture,
        float minExpected,
        float maxExpected,
        string message = "")
    {
        var data = texture.GetData<float>();
        AssertArrayInRange(data, minExpected, maxExpected, message);
    }

    /// <summary>
    /// Asserts that array values are within expected range.
    /// </summary>
    public static void AssertArrayInRange(
        float[] data,
        float minExpected,
        float maxExpected,
        string message = "")
    {
        var actualMin = data.Min();
        var actualMax = data.Max();

        var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";

        Assert.True(actualMin >= minExpected,
            $"{prefix}Min value {actualMin} below expected {minExpected}");
        Assert.True(actualMax <= maxExpected,
            $"{prefix}Max value {actualMax} above expected {maxExpected}");
    }

    /// <summary>
    /// Asserts that the mean of the texture is approximately the expected value.
    /// </summary>
    public static void AssertTextureMean(
        VulkanImage texture,
        float expectedMean,
        float tolerance = 1e-2f,
        string message = "")
    {
        var data = texture.GetData<float>();
        var actualMean = (float)data.Average();

        var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";

        Assert.True(Math.Abs(actualMean - expectedMean) <= tolerance,
            $"{prefix}Mean {actualMean:F4} differs from expected {expectedMean:F4} by more than {tolerance}");
    }

    /// <summary>
    /// Asserts that the total energy (sum of squares) is preserved within tolerance.
    /// Useful for testing FFT operations (Parseval's theorem).
    /// </summary>
    public static void AssertEnergyPreserved(
        VulkanImage before,
        VulkanImage after,
        float tolerancePercent = 1f,
        string message = "")
    {
        var beforeData = before.GetData<float>();
        var afterData = after.GetData<float>();

        var beforeStats = FftValidator.ComputeStats(beforeData);
        var afterStats = FftValidator.ComputeStats(afterData);

        var percentDiff = Math.Abs(beforeStats.Energy - afterStats.Energy) / beforeStats.Energy * 100;

        var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";

        Assert.True(percentDiff < tolerancePercent,
            $"{prefix}Energy changed by {percentDiff:F2}%. " +
            $"Before: {beforeStats.Energy:G6}, After: {afterStats.Energy:G6}");
    }

    /// <summary>
    /// Asserts that FFT round-trip preserves data using FftValidator.
    /// </summary>
    public static void AssertFftRoundTrip(
        float[] original,
        float[] afterRoundTrip,
        double valueRange,
        string stageName = "FFT")
    {
        var result = FftValidator.ValidateRoundTrip(original, afterRoundTrip, valueRange, stageName);

        Assert.True(result.Passed,
            $"FFT round-trip failed: {result.FailureReason}. " +
            $"Max diff: {result.Metrics.GetValueOrDefault("MaxDiff"):F4}, " +
            $"Tolerance: {result.Metrics.GetValueOrDefault("Tolerance"):F4}");
    }

    /// <summary>
    /// Asserts that Parseval's theorem holds for FFT (with windowing).
    /// </summary>
    public static void AssertParsevalWithWindow(
        double spatialEnergy,
        double frequencyEnergy,
        int tileSize,
        string stageName = "FFT")
    {
        var result = FftValidator.ValidateParsevalWithWindow(spatialEnergy, frequencyEnergy, tileSize, stageName);

        Assert.True(result.Passed,
            $"Parseval's theorem (windowed) failed: {result.FailureReason}. " +
            $"Ratio: {result.Metrics.GetValueOrDefault("Ratio"):F4}");
    }

    /// <summary>
    /// Asserts that alignment vectors are approximately the expected values.
    /// Alignment texture stores int4 per tile: (dx, dy, cost, unused) as 16-bit signed integers.
    /// </summary>
    public static void AssertAlignmentApproximates(
        VulkanImage alignment,
        int expectedDx,
        int expectedDy,
        int tolerance = 1,
        float minMatchPercent = 80f)
    {
        var data = alignment.GetData<short>();
        var numTiles = data.Length / 4; // int4 per tile

        var matchCount = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            var dx = data[i];
            var dy = data[i + 1];

            if (Math.Abs(dx - expectedDx) <= tolerance &&
                Math.Abs(dy - expectedDy) <= tolerance)
            {
                matchCount++;
            }
        }

        var matchPercent = 100f * matchCount / numTiles;
        Assert.True(matchPercent >= minMatchPercent,
            $"Only {matchPercent:F1}% of tiles matched expected displacement ({expectedDx}, {expectedDy}). " +
            $"Need at least {minMatchPercent}%");
    }

    /// <summary>
    /// Asserts that all alignment vectors are zero (for identical image test).
    /// </summary>
    public static void AssertAlignmentIsZero(VulkanImage alignment, string message = "")
    {
        var data = alignment.GetData<short>();

        for (var i = 0; i < data.Length; i += 4)
        {
            var dx = data[i];
            var dy = data[i + 1];

            if (dx != 0 || dy != 0)
            {
                var tileIdx = i / 4;
                var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";
                Assert.Fail($"{prefix}Non-zero displacement at tile {tileIdx}: ({dx}, {dy})");
            }
        }
    }

    /// <summary>
    /// Asserts that a texture is not all zeros (useful for checking shader output).
    /// </summary>
    public static void AssertTextureNotAllZeros(VulkanImage texture, string message = "")
    {
        var data = texture.GetData<float>();
        var nonZeroCount = data.Count(x => Math.Abs(x) > 1e-10f);

        var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";

        Assert.True(nonZeroCount > 0,
            $"{prefix}Texture is all zeros (or near-zero). Total elements: {data.Length}");
    }

    /// <summary>
    /// Asserts that a texture has the expected dimensions.
    /// </summary>
    public static void AssertTextureDimensions(
        VulkanImage texture,
        int expectedWidth,
        int expectedHeight,
        string message = "")
    {
        var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";

        Assert.True(texture.Width == expectedWidth,
            $"{prefix}Width mismatch: expected {expectedWidth}, got {texture.Width}");
        Assert.True(texture.Height == expectedHeight,
            $"{prefix}Height mismatch: expected {expectedHeight}, got {texture.Height}");
    }

    /// <summary>
    /// Asserts that variance decreased (useful for blur tests).
    /// </summary>
    public static void AssertVarianceDecreased(
        VulkanImage before,
        VulkanImage after,
        string message = "")
    {
        var beforeData = before.GetData<float>();
        var afterData = after.GetData<float>();

        var beforeVar = MetricsCalculator.CalculateVariance(beforeData);
        var afterVar = MetricsCalculator.CalculateVariance(afterData);

        var prefix = string.IsNullOrEmpty(message) ? "" : $"{message} ";

        Assert.True(afterVar < beforeVar,
            $"{prefix}Variance did not decrease. Before: {beforeVar:G6}, After: {afterVar:G6}");
    }
}
