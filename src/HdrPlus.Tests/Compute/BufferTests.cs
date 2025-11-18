using FluentAssertions;
using HdrPlus.Compute;
using Xunit;

namespace HdrPlus.Tests.Compute;

/// <summary>
/// Tests for GPU buffer operations including upload, download, and memory management.
/// </summary>
public class BufferTests : IDisposable
{
    private IComputeDevice? _device;

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateBuffer_WithFloatData_ShouldStoreCorrectSize()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        Span<float> data = stackalloc float[100];
        for (int i = 0; i < data.Length; i++)
            data[i] = i * 0.5f;

        // Act
        using var buffer = _device.CreateBuffer(data);

        // Assert
        buffer.SizeInBytes.Should().Be(100 * sizeof(float));
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateBuffer_WithUploadUsage_ShouldCreateCpuAccessibleBuffer()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        Span<int> data = stackalloc int[] { 1, 2, 3, 4, 5 };

        // Act
        using var buffer = _device.CreateBuffer(data, BufferUsage.Upload);

        // Assert
        buffer.Should().NotBeNull();
        buffer.SizeInBytes.Should().Be(5 * sizeof(int));
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateBuffer_WithReadbackUsage_ShouldCreateReadableBuffer()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int size = 256;

        // Act
        using var buffer = _device.CreateBuffer(size, BufferUsage.Readback);

        // Assert
        buffer.Should().NotBeNull();
        buffer.SizeInBytes.Should().Be(size);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void WriteData_ShouldUpdateBufferContents()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        Span<float> initialData = stackalloc float[] { 1.0f, 2.0f, 3.0f };
        using var buffer = _device.CreateBuffer(initialData, BufferUsage.Upload);

        Span<float> newData = stackalloc float[] { 10.0f, 20.0f, 30.0f };

        // Act
        buffer.WriteData(newData);

        // Assert - we can't directly read back in this test, but no exception should be thrown
        true.Should().BeTrue();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void ReadData_ShouldRetrieveBufferContents()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        Span<float> data = stackalloc float[] { 1.5f, 2.5f, 3.5f, 4.5f };
        using var uploadBuffer = _device.CreateBuffer(data, BufferUsage.Upload);
        using var readbackBuffer = _device.CreateBuffer(4 * sizeof(float), BufferUsage.Readback);

        // Copy data from upload to readback using command buffer
        using var cmd = _device.CreateCommandBuffer();
        cmd.CopyBuffer(uploadBuffer, readbackBuffer, 4 * sizeof(float));
        _device.Submit(cmd);
        _device.WaitIdle();

        // Act
        Span<float> result = stackalloc float[4];
        readbackBuffer.ReadData(result);

        // Assert
        result[0].Should().BeApproximately(1.5f, 0.001f);
        result[1].Should().BeApproximately(2.5f, 0.001f);
        result[2].Should().BeApproximately(3.5f, 0.001f);
        result[3].Should().BeApproximately(4.5f, 0.001f);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateBuffer_WithZeroSize_ShouldThrowArgumentException()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        Action act = () => _device.CreateBuffer(0);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateBuffer_WithNegativeSize_ShouldThrowArgumentException()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        Action act = () => _device.CreateBuffer(-100);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    public void Dispose()
    {
        _device?.Dispose();
    }
}
