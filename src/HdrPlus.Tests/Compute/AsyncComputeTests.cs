using FluentAssertions;
using HdrPlus.Compute;
using Xunit;

namespace HdrPlus.Tests.Compute;

/// <summary>
/// Tests for async GPU submission and synchronization.
/// Validates non-blocking command submission and fence operations.
/// </summary>
public class AsyncComputeTests : IDisposable
{
    private IAsyncComputeDevice? _device;

    [Fact(Skip = "Requires GPU hardware with async support")]
    public async Task SubmitAsync_WithValidCommandBuffer_ShouldCompleteSuccessfully()
    {
        // Arrange
        _device = CreateAsyncDevice();
        using var cmd = _device.CreateCommandBuffer();

        // Act
        var task = _device.SubmitAsync(cmd);
        await task;

        // Assert
        task.IsCompleted.Should().BeTrue();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public async Task SubmitAsync_MultipleCommandBuffers_ShouldExecuteInOrder()
    {
        // Arrange
        _device = CreateAsyncDevice();
        var cmdBuffers = new List<IComputeCommandBuffer>
        {
            _device.CreateCommandBuffer(),
            _device.CreateCommandBuffer(),
            _device.CreateCommandBuffer()
        };

        // Act
        var tasks = cmdBuffers.Select(cmd => _device.SubmitAsync(cmd)).ToArray();
        await Task.WhenAll(tasks);

        // Assert
        tasks.Should().AllSatisfy(t => t.IsCompleted.Should().BeTrue());

        foreach (var cmd in cmdBuffers)
            cmd.Dispose();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public void SubmitAsyncWithCallback_ShouldInvokeCallback()
    {
        // Arrange
        _device = CreateAsyncDevice();
        using var cmd = _device.CreateCommandBuffer();
        bool callbackInvoked = false;

        // Act
        _device.SubmitAsync(cmd, () => callbackInvoked = true);
        _device.WaitIdle();

        // Assert
        callbackInvoked.Should().BeTrue();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public void CreateFence_ShouldReturnValidFence()
    {
        // Arrange
        _device = CreateAsyncDevice();

        // Act
        using var fence = _device.CreateFence();

        // Assert
        fence.Should().NotBeNull();
        fence.IsSignaled.Should().BeFalse();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public void Fence_SignalAndWait_ShouldSynchronizeCorrectly()
    {
        // Arrange
        _device = CreateAsyncDevice();
        using var fence = _device.CreateFence();
        using var cmd = _device.CreateCommandBuffer();

        // Act
        fence.Signal(cmd);
        _device.Submit(cmd);
        fence.Wait();

        // Assert
        fence.IsSignaled.Should().BeTrue();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public void Fence_WaitWithTimeout_ShouldRespectTimeout()
    {
        // Arrange
        _device = CreateAsyncDevice();
        using var fence = _device.CreateFence();

        // Act - wait on unsignaled fence with short timeout
        var result = fence.Wait(timeoutMs: 100);

        // Assert
        result.Should().BeFalse("Fence was never signaled");
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public void Fence_Reset_ShouldResetToUnsignaled()
    {
        // Arrange
        _device = CreateAsyncDevice();
        using var fence = _device.CreateFence();
        using var cmd = _device.CreateCommandBuffer();

        fence.Signal(cmd);
        _device.Submit(cmd);
        fence.Wait();

        // Act
        fence.Reset();

        // Assert
        fence.IsSignaled.Should().BeFalse();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public void IsIdle_WithNoWork_ShouldReturnTrue()
    {
        // Arrange
        _device = CreateAsyncDevice();
        _device.WaitIdle();

        // Act
        var isIdle = _device.IsIdle();

        // Assert
        isIdle.Should().BeTrue();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public void WaitIdle_WithTimeout_ShouldCompleteWithinTimeout()
    {
        // Arrange
        _device = CreateAsyncDevice();
        _device.WaitIdle(); // Ensure no pending work

        // Act
        var result = _device.WaitIdle(timeoutMs: 1000);

        // Assert
        result.Should().BeTrue();
    }

    [Fact(Skip = "Requires GPU hardware with async support")]
    public async Task ParallelSubmission_MultipleThreads_ShouldBeSafe()
    {
        // Arrange
        _device = CreateAsyncDevice();
        const int threadCount = 4;
        const int submissionsPerThread = 10;

        // Act
        var tasks = Enumerable.Range(0, threadCount)
            .Select(async _ =>
            {
                for (int i = 0; i < submissionsPerThread; i++)
                {
                    using var cmd = _device.CreateCommandBuffer();
                    await _device.SubmitAsync(cmd);
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        _device.WaitIdle();
        _device.IsIdle().Should().BeTrue();
    }

    private IAsyncComputeDevice CreateAsyncDevice()
    {
        var device = ComputeDeviceFactory.CreateDefault();
        if (device is not IAsyncComputeDevice asyncDevice)
        {
            throw new NotSupportedException("Device does not support async operations");
        }
        return asyncDevice;
    }

    public void Dispose()
    {
        _device?.Dispose();
    }
}
