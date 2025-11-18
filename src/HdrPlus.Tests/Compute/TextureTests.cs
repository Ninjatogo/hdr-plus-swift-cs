using FluentAssertions;
using HdrPlus.Compute;
using Xunit;

namespace HdrPlus.Tests.Compute;

/// <summary>
/// Tests for GPU texture creation, upload/download, and format support.
/// </summary>
public class TextureTests : IDisposable
{
    private IComputeDevice? _device;

    [Theory(Skip = "Requires GPU hardware")]
    [InlineData(256, 256, TextureFormat.R16_Float)]
    [InlineData(512, 512, TextureFormat.R32_Float)]
    [InlineData(1024, 1024, TextureFormat.RGBA16_Float)]
    [InlineData(2048, 2048, TextureFormat.RGBA32_Float)]
    public void CreateTexture2D_WithVariousFormats_ShouldCreateValidTexture(int width, int height, TextureFormat format)
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var texture = _device.CreateTexture2D(width, height, format);

        // Assert
        texture.Should().NotBeNull();
        texture.Width.Should().Be(width);
        texture.Height.Should().Be(height);
        texture.Format.Should().Be(format);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateTexture2D_WithShaderReadUsage_ShouldCreateSamplerTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var texture = _device.CreateTexture2D(512, 512, TextureFormat.R16_Float, TextureUsage.ShaderRead);

        // Assert
        texture.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateTexture2D_WithShaderWriteUsage_ShouldCreateWritableTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var texture = _device.CreateTexture2D(512, 512, TextureFormat.R16_Float, TextureUsage.ShaderWrite);

        // Assert
        texture.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateTexture2D_WithShaderReadWriteUsage_ShouldCreateRWTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var texture = _device.CreateTexture2D(512, 512, TextureFormat.RGBA16_Float, TextureUsage.ShaderReadWrite);

        // Assert
        texture.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateTexture3D_WithValidDimensions_ShouldCreateValidTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var texture = _device.CreateTexture3D(128, 128, 64, TextureFormat.R32_Float);

        // Assert
        texture.Should().NotBeNull();
        texture.Width.Should().Be(128);
        texture.Height.Should().Be(128);
        texture.Depth.Should().Be(64);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void WriteData_ToTexture_ShouldUploadData()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int width = 16;
        const int height = 16;
        using var texture = _device.CreateTexture2D(width, height, TextureFormat.R16_Float);

        var data = new ushort[width * height];
        for (int i = 0; i < data.Length; i++)
            data[i] = (ushort)(i * 100);

        // Act
        Action act = () => texture.WriteData(data.AsSpan());

        // Assert
        act.Should().NotThrow();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void ReadData_FromTexture_ShouldDownloadData()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int width = 16;
        const int height = 16;
        using var texture = _device.CreateTexture2D(width, height, TextureFormat.R16_Float);

        var inputData = new ushort[width * height];
        for (int i = 0; i < inputData.Length; i++)
            inputData[i] = (ushort)(i * 10);

        texture.WriteData(inputData.AsSpan());
        _device.WaitIdle();

        // Act
        var outputData = new ushort[width * height];
        texture.ReadData(outputData.AsSpan());

        // Assert
        outputData.Should().BeEquivalentTo(inputData);
    }

    [Theory(Skip = "Requires GPU hardware")]
    [InlineData(0, 256)]
    [InlineData(256, 0)]
    [InlineData(-1, 256)]
    [InlineData(256, -1)]
    public void CreateTexture2D_WithInvalidDimensions_ShouldThrowArgumentException(int width, int height)
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        Action act = () => _device.CreateTexture2D(width, height, TextureFormat.R16_Float);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateTexture2D_WithRG16SInt_ShouldSupportAlignmentVectors()
    {
        // Arrange - RG16_SInt is used for alignment vectors (dx, dy)
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var texture = _device.CreateTexture2D(512, 512, TextureFormat.RG16_SInt);

        // Assert
        texture.Should().NotBeNull();
        texture.Format.Should().Be(TextureFormat.RG16_SInt);
    }

    public void Dispose()
    {
        _device?.Dispose();
    }
}
