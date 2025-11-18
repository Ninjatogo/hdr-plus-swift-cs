using FluentAssertions;
using HdrPlus.Compute;
using Xunit;

namespace HdrPlus.Tests.Compute;

/// <summary>
/// Tests for GPU resource pooling and memory optimization.
/// Validates buffer/texture reuse and memory efficiency.
/// </summary>
public class ResourcePoolTests : IDisposable
{
    private IComputeDevice? _device;
    private ResourcePool? _pool;

    [Fact(Skip = "Requires GPU hardware")]
    public void CreatePool_WithValidDevice_ShouldSucceed()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();

        // Act
        _pool = new ResourcePool(_device);

        // Assert
        _pool.Should().NotBeNull();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void GetBuffer_FirstTime_ShouldCreateNewBuffer()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        // Act
        using var buffer = _pool.GetBuffer(1024);

        // Assert
        buffer.Should().NotBeNull();
        buffer.SizeInBytes.Should().Be(1024);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void GetBuffer_SameSize_ShouldReuseBuffer()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        // Act
        var buffer1 = _pool.GetBuffer(2048);
        buffer1.Dispose(); // Return to pool

        var stats1 = _pool.GetStatistics();
        var buffer2 = _pool.GetBuffer(2048); // Should reuse

        // Assert
        stats1.AvailableBuffers.Should().BeGreaterThan(0);
        buffer2.Should().NotBeNull();
        buffer2.SizeInBytes.Should().Be(2048);

        buffer2.Dispose();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void GetTexture_FirstTime_ShouldCreateNewTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        // Act
        using var texture = _pool.GetTexture2D(512, 512, TextureFormat.R16_Float);

        // Assert
        texture.Should().NotBeNull();
        texture.Width.Should().Be(512);
        texture.Height.Should().Be(512);
        texture.Format.Should().Be(TextureFormat.R16_Float);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void GetTexture_SameParameters_ShouldReuseTexture()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        // Act
        var texture1 = _pool.GetTexture2D(1024, 1024, TextureFormat.RGBA16_Float);
        texture1.Dispose(); // Return to pool

        var stats1 = _pool.GetStatistics();
        var texture2 = _pool.GetTexture2D(1024, 1024, TextureFormat.RGBA16_Float);

        // Assert
        stats1.AvailableTextures.Should().BeGreaterThan(0);
        texture2.Should().NotBeNull();

        texture2.Dispose();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void GetStatistics_AfterOperations_ShouldReflectPoolState()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        // Act
        var buffer1 = _pool.GetBuffer(1024);
        var buffer2 = _pool.GetBuffer(2048);
        var texture1 = _pool.GetTexture2D(256, 256, TextureFormat.R32_Float);

        var stats = _pool.GetStatistics();

        // Assert
        stats.TotalBuffers.Should().BeGreaterOrEqualTo(2);
        stats.TotalTextures.Should().BeGreaterOrEqualTo(1);
        stats.BuffersInUse.Should().BeGreaterOrEqualTo(2);
        stats.TexturesInUse.Should().BeGreaterOrEqualTo(1);

        buffer1.Dispose();
        buffer2.Dispose();
        texture1.Dispose();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Trim_WithUnusedResources_ShouldReducePoolSize()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        var buffer1 = _pool.GetBuffer(1024);
        var buffer2 = _pool.GetBuffer(2048);
        buffer1.Dispose();
        buffer2.Dispose();

        var statsBefore = _pool.GetStatistics();

        // Act
        _pool.Trim();
        var statsAfter = _pool.GetStatistics();

        // Assert
        statsBefore.TotalBuffers.Should().BeGreaterThan(0);
        statsAfter.TotalBuffers.Should().Be(0, "All unused buffers should be released");
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pool_ConcurrentAccess_ShouldBeThreadSafe()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);
        const int threadCount = 4;
        const int operationsPerThread = 100;

        // Act
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < operationsPerThread; i++)
                {
                    using var buffer = _pool.GetBuffer(1024);
                    using var texture = _pool.GetTexture2D(256, 256, TextureFormat.R16_Float);
                    // Resources automatically returned to pool on dispose
                }
            }))
            .ToArray();

        Task.WaitAll(tasks);

        // Assert
        var stats = _pool.GetStatistics();
        stats.BuffersInUse.Should().Be(0, "All buffers should be returned to pool");
        stats.TexturesInUse.Should().Be(0, "All textures should be returned to pool");
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pool_LargeNumberOfAllocations_ShouldHandleEfficiently()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);
        const int allocationCount = 1000;

        // Act
        for (int i = 0; i < allocationCount; i++)
        {
            using var buffer = _pool.GetBuffer(1024);
            // Immediately returned to pool
        }

        var stats = _pool.GetStatistics();

        // Assert
        stats.AvailableBuffers.Should().BeGreaterThan(0);
        stats.BuffersInUse.Should().Be(0);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pool_DifferentSizes_ShouldMaintainSeparatePools()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        // Act
        using var buffer1k = _pool.GetBuffer(1024);
        using var buffer2k = _pool.GetBuffer(2048);
        using var buffer4k = _pool.GetBuffer(4096);

        var stats = _pool.GetStatistics();

        // Assert
        stats.TotalBuffers.Should().BeGreaterOrEqualTo(3);
        buffer1k.SizeInBytes.Should().Be(1024);
        buffer2k.SizeInBytes.Should().Be(2048);
        buffer4k.SizeInBytes.Should().Be(4096);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pool_MixedTextureFormats_ShouldHandleCorrectly()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        _pool = new ResourcePool(_device);

        // Act
        using var r16 = _pool.GetTexture2D(512, 512, TextureFormat.R16_Float);
        using var r32 = _pool.GetTexture2D(512, 512, TextureFormat.R32_Float);
        using var rgba16 = _pool.GetTexture2D(512, 512, TextureFormat.RGBA16_Float);

        var stats = _pool.GetStatistics();

        // Assert
        stats.TotalTextures.Should().BeGreaterOrEqualTo(3);
        r16.Format.Should().Be(TextureFormat.R16_Float);
        r32.Format.Should().Be(TextureFormat.R32_Float);
        rgba16.Format.Should().Be(TextureFormat.RGBA16_Float);
    }

    public void Dispose()
    {
        _pool?.Dispose();
        _device?.Dispose();
    }
}
