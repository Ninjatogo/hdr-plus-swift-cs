using FluentAssertions;
using HdrPlus.Compute;
using Xunit;

namespace HdrPlus.Tests.Compute;

/// <summary>
/// Tests for GPU compute device abstraction layer.
/// Validates cross-platform device creation and basic operations.
/// </summary>
public class ComputeDeviceTests : IDisposable
{
    private IComputeDevice? _device;

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateDefault_ShouldReturnValidDevice()
    {
        // Arrange & Act
        _device = ComputeDeviceFactory.CreateDefault();

        // Assert
        _device.Should().NotBeNull();
        _device.DeviceName.Should().NotBeNullOrEmpty();
        _device.Backend.Should().BeOneOf(ComputeBackend.DirectX12, ComputeBackend.Vulkan, ComputeBackend.Metal);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void DeviceName_ShouldContainGpuInfo()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        var deviceName = _device.DeviceName;

        // Assert
        deviceName.Should().NotBeNullOrEmpty();
        deviceName.Length.Should().BeGreaterThan(3);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void WaitIdle_ShouldCompleteWithoutError()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        Action act = () => _device.WaitIdle();

        // Assert
        act.Should().NotThrow();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateBuffer_WithData_ShouldCreateValidBuffer()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        Span<float> data = stackalloc float[] { 1.0f, 2.0f, 3.0f, 4.0f };

        // Act
        using var buffer = _device.CreateBuffer(data);

        // Assert
        buffer.Should().NotBeNull();
        buffer.SizeInBytes.Should().Be(4 * sizeof(float));
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateBuffer_Empty_ShouldCreateValidBuffer()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int size = 1024;

        // Act
        using var buffer = _device.CreateBuffer(size);

        // Assert
        buffer.Should().NotBeNull();
        buffer.SizeInBytes.Should().Be(size);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateTexture2D_ShouldCreateValidTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int width = 256;
        const int height = 256;

        // Act
        using var texture = _device.CreateTexture2D(width, height, TextureFormat.R16_Float);

        // Assert
        texture.Should().NotBeNull();
        texture.Width.Should().Be(width);
        texture.Height.Should().Be(height);
        texture.Format.Should().Be(TextureFormat.R16_Float);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateTexture3D_ShouldCreateValidTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int width = 128;
        const int height = 128;
        const int depth = 64;

        // Act
        using var texture = _device.CreateTexture3D(width, height, depth, TextureFormat.RGBA16_Float);

        // Assert
        texture.Should().NotBeNull();
        texture.Width.Should().Be(width);
        texture.Height.Should().Be(height);
        texture.Depth.Should().Be(depth);
        texture.Format.Should().Be(TextureFormat.RGBA16_Float);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateCommandBuffer_ShouldReturnValidCommandBuffer()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        using var cmdBuffer = _device.CreateCommandBuffer();

        // Assert
        cmdBuffer.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Submit_WithEmptyCommandBuffer_ShouldNotThrow()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        using var cmdBuffer = _device.CreateCommandBuffer();

        // Act
        Action act = () => _device.Submit(cmdBuffer);

        // Assert
        act.Should().NotThrow();
    }

    public void Dispose()
    {
        _device?.Dispose();
    }
}
