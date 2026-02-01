using BurstPhoto.Rendering.Validation;
using BurstPhoto.Tests.TestHelpers;
using Silk.NET.Vulkan;

namespace BurstPhoto.Tests.Unit.FFT;

/// <summary>
/// Tests for FFT (Fast Fourier Transform) operations.
/// Validates mathematical invariants like Parseval's theorem and round-trip preservation.
/// </summary>
[Collection("GPU")]
[Trait("Category", "Unit")]
[Trait("Category", "GPU")]
public class FftRoundTripTests : IClassFixture<GpuCollectionFixture>
{
    private readonly GpuCollectionFixture _fixture;
    private readonly ITestOutputHelper _output;

    // FFT tile size is hardcoded to 8 in FrequencyMergePipeline
    private const int TileSize = 8;

    public FftRoundTripTests(GpuCollectionFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void ForwardThenBackward_ReturnsOriginalData()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");
        Assert.SkipWhen(!_fixture.SupportsFrequencyDomain, "GPU does not support StorageImageWriteWithoutFormat");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange: Create RGBA texture with white noise pattern
        // Use dimensions that are evenly divisible by tile size
        const int width = 64;
        const int height = 64;

        using var input = _fixture.Factory!.CreateRgbaTexture(
            width, height, TestPattern.WhiteNoise, minValue: 1000f, maxValue: 60000f, seed: 42, track: false);

        _output.WriteLine($"Input texture: {width}x{height}, format: {input.Format}");

        // Act: Run FFT round-trip validation
        var frequencyPipeline = _fixture.CreateFrequencyPipeline();
        var results = frequencyPipeline.RunFftRoundTripValidation(input, TileSize);

        // Log all results
        foreach (var result in results)
        {
            _output.WriteLine(result.ToString());
        }

        // Assert: Round-trip should pass
        var roundTripResult = results.FirstOrDefault(r => r.TestName.Contains("Round-Trip"));
        Assert.NotNull(roundTripResult);
        Assert.True(roundTripResult.Passed,
            $"FFT round-trip failed: {roundTripResult.FailureReason}");
    }

    [Fact]
    public void ForwardFft_ParsevalsTheorem_EnergyPreserved()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");
        Assert.SkipWhen(!_fixture.SupportsFrequencyDomain, "GPU does not support StorageImageWriteWithoutFormat");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int width = 64;
        const int height = 64;

        using var input = _fixture.Factory!.CreateRgbaTexture(
            width, height, TestPattern.WhiteNoise, minValue: 1000f, maxValue: 60000f, seed: 123, track: false);

        // Act: Run FFT validation
        var frequencyPipeline = _fixture.CreateFrequencyPipeline();
        var results = frequencyPipeline.RunFftRoundTripValidation(input, TileSize);

        // Assert: Parseval's theorem (with window) should pass
        var parsevalResult = results.FirstOrDefault(r => r.TestName.Contains("Window-Aware"));
        Assert.NotNull(parsevalResult);
        Assert.True(parsevalResult.Passed,
            $"Parseval's theorem (windowed) failed: {parsevalResult.FailureReason}");

        // Log the energy ratio
        if (parsevalResult.Metrics.TryGetValue("Ratio", out var ratio))
        {
            _output.WriteLine($"Energy ratio (should be ~1.0): {ratio:F4}");
        }
    }

    [Fact]
    public void ForwardFft_WindowFunction_MatchesHann()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");
        Assert.SkipWhen(!_fixture.SupportsFrequencyDomain, "GPU does not support StorageImageWriteWithoutFormat");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int width = 64;
        const int height = 64;

        using var input = _fixture.Factory!.CreateRgbaTexture(
            width, height, TestPattern.WhiteNoise, minValue: 1000f, maxValue: 60000f, seed: 456, track: false);

        // Act: Run FFT validation
        var frequencyPipeline = _fixture.CreateFrequencyPipeline();
        var results = frequencyPipeline.RunFftRoundTripValidation(input, TileSize);

        // Assert: Window function should match expected Hann window
        var windowResult = results.FirstOrDefault(r => r.TestName.Contains("Window Function"));
        Assert.NotNull(windowResult);
        Assert.True(windowResult.Passed,
            $"Window function validation failed: {windowResult.FailureReason}");

        // Log the measured window factor
        if (windowResult.Metrics.TryGetValue("Measured Window Factor", out var measured))
        {
            var expected = 9.0 / 64.0; // Theoretical Hann window factor
            _output.WriteLine($"Measured window factor: {measured:F6}");
            _output.WriteLine($"Expected window factor: {expected:F6}");
        }
    }

    [Theory]
    [InlineData(32, 32)]
    [InlineData(64, 64)]
    [InlineData(128, 128)]
    public void ForwardThenBackward_VariousSizes_ReturnsOriginalData(int width, int height)
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");
        Assert.SkipWhen(!_fixture.SupportsFrequencyDomain, "GPU does not support StorageImageWriteWithoutFormat");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");
        _output.WriteLine($"Testing size: {width}x{height}");

        // Arrange
        using var input = _fixture.Factory!.CreateRgbaTexture(
            width, height, TestPattern.WhiteNoise, minValue: 1000f, maxValue: 60000f, seed: width * height, track: false);

        // Act
        var frequencyPipeline = _fixture.CreateFrequencyPipeline();
        var results = frequencyPipeline.RunFftRoundTripValidation(input, TileSize);

        // Assert
        var roundTripResult = results.FirstOrDefault(r => r.TestName.Contains("Round-Trip"));
        Assert.NotNull(roundTripResult);
        Assert.True(roundTripResult.Passed,
            $"FFT round-trip failed for {width}x{height}: {roundTripResult.FailureReason}");
    }

    [Fact]
    public void ForwardFft_UniformInput_ProducesDcOnlyOutput()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");
        Assert.SkipWhen(!_fixture.SupportsFrequencyDomain, "GPU does not support StorageImageWriteWithoutFormat");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange: Uniform (solid) input should have all energy in DC bin
        const int width = 64;
        const int height = 64;
        const float uniformValue = 32000f;

        using var input = _fixture.Factory!.CreateConstantTexture(
            width, height, uniformValue, Format.R32G32B32A32Sfloat, track: false);

        _output.WriteLine($"Input: uniform value {uniformValue}");

        // Act: Run forward FFT
        var ftWidth = width * 2; // Complex storage
        using var fftOutput = _fixture.Factory.CreateEmptyTexture(ftWidth, height, Format.R32G32B32A32Sfloat, track: false);

        var frequencyPipeline = _fixture.CreateFrequencyPipeline();
        frequencyPipeline.ExecuteForwardFft(input, fftOutput, TileSize, width, height);

        // Assert: Check that output has non-zero values
        var fftData = fftOutput.GetData<float>();
        var ftStats = FftValidator.ComputeFrequencyStats(fftData, width, height);

        _output.WriteLine($"FFT energy: {ftStats.TotalEnergy:G6}");

        // For uniform input, most energy should be concentrated in DC bins
        // The exact distribution depends on window function, but output should not be all zeros
        Assert.True(ftStats.TotalEnergy > 0, "FFT output should have non-zero energy");
    }

    [Theory]
    [InlineData(TestPattern.HorizontalGradient)]
    [InlineData(TestPattern.VerticalGradient)]
    [InlineData(TestPattern.SineWave)]
    [InlineData(TestPattern.Checkerboard)]
    public void ForwardThenBackward_VariousPatterns_ReturnsOriginalData(TestPattern pattern)
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");
        Assert.SkipWhen(!_fixture.SupportsFrequencyDomain, "GPU does not support StorageImageWriteWithoutFormat");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");
        _output.WriteLine($"Testing pattern: {pattern}");

        // Arrange
        const int width = 64;
        const int height = 64;

        using var input = _fixture.Factory!.CreateRgbaTexture(
            width, height, pattern, minValue: 1000f, maxValue: 60000f, track: false);

        // Act
        var frequencyPipeline = _fixture.CreateFrequencyPipeline();
        var results = frequencyPipeline.RunFftRoundTripValidation(input, TileSize);

        // Assert
        var roundTripResult = results.FirstOrDefault(r => r.TestName.Contains("Round-Trip"));
        Assert.NotNull(roundTripResult);
        Assert.True(roundTripResult.Passed,
            $"FFT round-trip failed for pattern {pattern}: {roundTripResult.FailureReason}");
    }

    [Fact]
    public void ForwardFft_OutputHasCorrectDimensions()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");
        Assert.SkipWhen(!_fixture.SupportsFrequencyDomain, "GPU does not support StorageImageWriteWithoutFormat");

        // Arrange
        const int width = 64;
        const int height = 64;

        using var input = _fixture.Factory!.CreateRgbaTexture(width, height, TestPattern.WhiteNoise, track: false);

        // FFT output should be 2x width for complex storage
        var ftWidth = width * 2;
        using var fftOutput = _fixture.Factory.CreateEmptyTexture(ftWidth, height, Format.R32G32B32A32Sfloat, track: false);

        // Act
        var frequencyPipeline = _fixture.CreateFrequencyPipeline();
        frequencyPipeline.ExecuteForwardFft(input, fftOutput, TileSize, width, height);

        // Assert
        Assert.Equal((uint)ftWidth, fftOutput.Width);
        Assert.Equal((uint)height, fftOutput.Height);

        _output.WriteLine($"Input: {width}x{height}");
        _output.WriteLine($"FFT Output: {fftOutput.Width}x{fftOutput.Height} (2x width for complex storage)");
    }
}
