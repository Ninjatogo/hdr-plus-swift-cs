using BurstPhoto.Core.Models;
using BurstPhoto.Tests.TestHelpers;
using Silk.NET.Vulkan;

namespace BurstPhoto.Tests.Unit.Alignment;

/// <summary>
/// Tests for average pooling operation used in building image pyramids for alignment.
/// Validates mathematical properties like mean preservation and variance reduction.
/// </summary>
[Collection("GPU")]
[Trait("Category", "Unit")]
[Trait("Category", "GPU")]
public class AvgPoolTests : IClassFixture<GpuCollectionFixture>
{
    private readonly GpuCollectionFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AvgPoolTests(GpuCollectionFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Creates a minimal RawImage for testing (needed by ExecuteAvgPool).
    /// </summary>
    private static RawImage CreateTestRawImage(int width, int height)
    {
        return new RawImage
        {
            Width = width,
            Height = height,
            Data = new ushort[width * height],
            IsBayerData = true,
            MosaicPatternWidth = 2,
            WhiteLevel = 65535,
            BlackLevels = new[] { 0, 0, 0, 0 },
            ColorChannelMultipliers = new[] { 1.0f, 1.0f, 1.0f, 1.0f }
        };
    }

    [Fact]
    public void AvgPool_ReducesDimensions()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int inputWidth = 256;
        const int inputHeight = 256;
        const int scale = 2;
        var expectedWidth = inputWidth / scale;
        var expectedHeight = inputHeight / scale;

        using var input = _fixture.Factory!.CreateTexture(
            inputWidth, inputHeight, TestPattern.WhiteNoise, Format.R32Sfloat, track: false);

        using var output = _fixture.Factory.CreateEmptyTexture(
            expectedWidth, expectedHeight, Format.R32Sfloat, track: false);

        var rawInfo = CreateTestRawImage(inputWidth, inputHeight);

        // Act
        var alignmentPipeline = _fixture.CreateAlignmentPipeline();
        alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize: false);

        // Assert
        Assert.Equal((uint)expectedWidth, output.Width);
        Assert.Equal((uint)expectedHeight, output.Height);

        _output.WriteLine($"Input: {inputWidth}x{inputHeight}");
        _output.WriteLine($"Output: {output.Width}x{output.Height}");
    }

    [Fact]
    public void AvgPool_PreservesMean()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int inputWidth = 256;
        const int inputHeight = 256;
        const int scale = 2;

        using var input = _fixture.Factory!.CreateTexture(
            inputWidth, inputHeight, TestPattern.WhiteNoise,
            Format.R32Sfloat, minValue: 1000f, maxValue: 60000f, track: false);

        var inputData = input.GetData<float>();
        var inputMean = inputData.Average();

        _output.WriteLine($"Input mean: {inputMean:F2}");

        using var output = _fixture.Factory.CreateEmptyTexture(
            inputWidth / scale, inputHeight / scale, Format.R32Sfloat, track: false);

        var rawInfo = CreateTestRawImage(inputWidth, inputHeight);

        // Act
        var alignmentPipeline = _fixture.CreateAlignmentPipeline();
        alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize: false);

        // Assert
        var outputData = output.GetData<float>();
        var outputMean = outputData.Average();

        _output.WriteLine($"Output mean: {outputMean:F2}");
        _output.WriteLine($"Difference: {Math.Abs(inputMean - outputMean):F2}");

        // Average pooling should preserve mean (within tolerance for floating-point)
        var tolerance = inputMean * 0.01f; // 1% tolerance
        Assert.True(Math.Abs(inputMean - outputMean) < tolerance,
            $"Mean changed from {inputMean:F2} to {outputMean:F2}, diff {Math.Abs(inputMean - outputMean):F2} > tolerance {tolerance:F2}");
    }

    [Fact]
    public void AvgPool_ReducesVariance()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int inputWidth = 256;
        const int inputHeight = 256;
        const int scale = 2;

        using var input = _fixture.Factory!.CreateTexture(
            inputWidth, inputHeight, TestPattern.WhiteNoise,
            Format.R32Sfloat, minValue: 1000f, maxValue: 60000f, track: false);

        var inputData = input.GetData<float>();
        var inputVariance = MetricsCalculator.CalculateVariance(inputData);

        _output.WriteLine($"Input variance: {inputVariance:G6}");

        using var output = _fixture.Factory.CreateEmptyTexture(
            inputWidth / scale, inputHeight / scale, Format.R32Sfloat, track: false);

        var rawInfo = CreateTestRawImage(inputWidth, inputHeight);

        // Act
        var alignmentPipeline = _fixture.CreateAlignmentPipeline();
        alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize: false);

        // Assert
        var outputData = output.GetData<float>();
        var outputVariance = MetricsCalculator.CalculateVariance(outputData);

        _output.WriteLine($"Output variance: {outputVariance:G6}");
        _output.WriteLine($"Variance reduction: {(1 - outputVariance / inputVariance) * 100:F1}%");

        // Averaging should reduce variance
        Assert.True(outputVariance < inputVariance,
            $"Variance did not decrease: {outputVariance:G6} >= {inputVariance:G6}");
    }

    [Fact]
    public void AvgPool_UniformInput_StaysUniform()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int inputWidth = 256;
        const int inputHeight = 256;
        const int scale = 2;
        const float uniformValue = 32000f;

        using var input = _fixture.Factory!.CreateConstantTexture(
            inputWidth, inputHeight, uniformValue, Format.R32Sfloat, track: false);

        using var output = _fixture.Factory.CreateEmptyTexture(
            inputWidth / scale, inputHeight / scale, Format.R32Sfloat, track: false);

        var rawInfo = CreateTestRawImage(inputWidth, inputHeight);

        // Act
        var alignmentPipeline = _fixture.CreateAlignmentPipeline();
        alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize: false);

        // Assert
        var outputData = output.GetData<float>();
        var outputMean = outputData.Average();
        var outputVariance = MetricsCalculator.CalculateVariance(outputData);

        _output.WriteLine($"Expected value: {uniformValue}");
        _output.WriteLine($"Output mean: {outputMean:F2}");
        _output.WriteLine($"Output variance: {outputVariance:G6}");

        // Mean should match uniform value
        Assert.True(Math.Abs(outputMean - uniformValue) < uniformValue * 0.001f,
            $"Mean {outputMean:F2} differs from expected {uniformValue}");

        // Variance should be very small (essentially zero)
        Assert.True(outputVariance < 1.0,
            $"Uniform input should have near-zero variance, got {outputVariance:G6}");
    }

    [Fact]
    public void AvgPool_OutputNotAllZeros()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int inputWidth = 256;
        const int inputHeight = 256;
        const int scale = 2;

        using var input = _fixture.Factory!.CreateTexture(
            inputWidth, inputHeight, TestPattern.WhiteNoise,
            Format.R32Sfloat, minValue: 1000f, maxValue: 60000f, track: false);

        using var output = _fixture.Factory.CreateEmptyTexture(
            inputWidth / scale, inputHeight / scale, Format.R32Sfloat, track: false);

        var rawInfo = CreateTestRawImage(inputWidth, inputHeight);

        // Act
        var alignmentPipeline = _fixture.CreateAlignmentPipeline();
        alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize: false);

        // Assert
        TextureAssertions.AssertTextureNotAllZeros(output, "AvgPool output");
    }

    [Theory]
    [InlineData(128, 128, 2)]
    [InlineData(256, 256, 2)]
    [InlineData(256, 256, 4)]
    public void AvgPool_VariousSizes_ProducesCorrectOutput(int inputWidth, int inputHeight, int scale)
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");
        _output.WriteLine($"Testing: {inputWidth}x{inputHeight}, scale={scale}");

        // Arrange
        using var input = _fixture.Factory!.CreateTexture(
            inputWidth, inputHeight, TestPattern.WhiteNoise,
            Format.R32Sfloat, minValue: 1000f, maxValue: 60000f, track: false);

        var expectedWidth = inputWidth / scale;
        var expectedHeight = inputHeight / scale;

        using var output = _fixture.Factory.CreateEmptyTexture(
            expectedWidth, expectedHeight, Format.R32Sfloat, track: false);

        var rawInfo = CreateTestRawImage(inputWidth, inputHeight);

        // Act
        var alignmentPipeline = _fixture.CreateAlignmentPipeline();
        alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize: false);

        // Assert
        Assert.Equal((uint)expectedWidth, output.Width);
        Assert.Equal((uint)expectedHeight, output.Height);

        var inputData = input.GetData<float>();
        var outputData = output.GetData<float>();

        var inputMean = inputData.Average();
        var outputMean = outputData.Average();

        _output.WriteLine($"Input mean: {inputMean:F2}, Output mean: {outputMean:F2}");

        // Mean should be preserved (with some tolerance)
        Assert.True(Math.Abs(inputMean - outputMean) < inputMean * 0.05f,
            $"Mean changed too much: {inputMean:F2} -> {outputMean:F2}");
    }

    [Fact]
    public void AvgPool_GradientInput_PreservesGradientDirection()
    {
        Assert.SkipWhen(!_fixture.IsGpuAvailable, $"GPU not available: {_fixture.GpuError}");

        _output.WriteLine($"GPU: {_fixture.GetGpuName()}");

        // Arrange
        const int inputWidth = 256;
        const int inputHeight = 256;
        const int scale = 2;

        using var input = _fixture.Factory!.CreateTexture(
            inputWidth, inputHeight, TestPattern.HorizontalGradient,
            Format.R32Sfloat, minValue: 1000f, maxValue: 60000f, track: false);

        using var output = _fixture.Factory.CreateEmptyTexture(
            inputWidth / scale, inputHeight / scale, Format.R32Sfloat, track: false);

        var rawInfo = CreateTestRawImage(inputWidth, inputHeight);

        // Act
        var alignmentPipeline = _fixture.CreateAlignmentPipeline();
        alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize: false);

        // Assert: Output should still have a gradient (left side darker than right)
        var outputData = output.GetData<float>();
        var outWidth = inputWidth / scale;
        var outHeight = inputHeight / scale;

        // Sample left column vs right column
        double leftSum = 0, rightSum = 0;
        for (var y = 0; y < outHeight; y++)
        {
            leftSum += outputData[y * outWidth + 0];           // leftmost column
            rightSum += outputData[y * outWidth + outWidth - 1]; // rightmost column
        }

        var leftAvg = leftSum / outHeight;
        var rightAvg = rightSum / outHeight;

        _output.WriteLine($"Left column avg: {leftAvg:F2}");
        _output.WriteLine($"Right column avg: {rightAvg:F2}");

        Assert.True(leftAvg < rightAvg,
            $"Horizontal gradient not preserved: left avg {leftAvg:F2} >= right avg {rightAvg:F2}");
    }
}
