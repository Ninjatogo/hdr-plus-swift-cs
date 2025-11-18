using FluentAssertions;
using HdrPlus.Compute;
using Xunit;

namespace HdrPlus.Tests.Compute;

/// <summary>
/// Tests for shader compilation, loading, and execution validation.
/// Validates that all 6 critical alignment shaders work correctly.
/// </summary>
public class ShaderValidationTests : IDisposable
{
    private IComputeDevice? _device;

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_AvgPool_ShouldLoadSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var pipeline = _device.CreatePipeline("avg_pool");

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_AvgPoolNormalization_ShouldLoadSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var pipeline = _device.CreatePipeline("avg_pool_normalization");

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_ComputeTileDifferences_ShouldLoadSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var pipeline = _device.CreatePipeline("compute_tile_differences");

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_FindBestTileAlignment_ShouldLoadSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var pipeline = _device.CreatePipeline("find_best_tile_alignment");

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_WarpTextureBayer_ShouldLoadSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var pipeline = _device.CreatePipeline("warp_texture_bayer");

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_CorrectUpsamplingError_ShouldLoadSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var pipeline = _device.CreatePipeline("correct_upsampling_error");

        // Assert
        pipeline.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void ExecuteAvgPool_OnTestTexture_ShouldProduceValidOutput()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int inputWidth = 64;
        const int inputHeight = 64;
        const int outputWidth = 32;
        const int outputHeight = 32;

        using var inputTexture = _device.CreateTexture2D(inputWidth, inputHeight, TextureFormat.R16_Float);
        using var outputTexture = _device.CreateTexture2D(outputWidth, outputHeight, TextureFormat.R16_Float, TextureUsage.ShaderWrite);
        using var pipeline = _device.CreatePipeline("avg_pool");

        // Create test pattern
        var inputData = new ushort[inputWidth * inputHeight];
        for (int i = 0; i < inputData.Length; i++)
            inputData[i] = (ushort)((i % 256) * 256);

        inputTexture.WriteData(inputData.AsSpan());

        // Act
        using var cmd = _device.CreateCommandBuffer();
        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inputTexture, 0);
        cmd.SetTexture(outputTexture, 1);
        cmd.DispatchThreads(outputWidth, outputHeight, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitIdle();

        // Assert
        var outputData = new ushort[outputWidth * outputHeight];
        outputTexture.ReadData(outputData.AsSpan());

        // Verify that output is not all zeros (shader executed)
        outputData.Should().Contain(x => x != 0);
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_WithInvalidShaderName_ShouldThrowException()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        Action act = () => _device.CreatePipeline("nonexistent_shader");

        // Assert
        act.Should().Throw<Exception>();
    }

    [Fact(Skip = "Requires GPU hardware and compiled shaders")]
    public void CreatePipeline_WithCustomEntryPoint_ShouldLoadSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act - assuming some shaders might have custom entry points
        using var pipeline = _device.CreatePipeline("avg_pool", "main");

        // Assert
        pipeline.Should().NotBeNull();
    }

    public void Dispose()
    {
        _device?.Dispose();
    }
}
