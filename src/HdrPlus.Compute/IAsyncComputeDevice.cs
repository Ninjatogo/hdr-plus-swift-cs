namespace HdrPlus.Compute;

/// <summary>
/// Extended compute device interface with async GPU submission support.
/// Enables non-blocking command submission and GPU work overlap.
/// </summary>
public interface IAsyncComputeDevice : IComputeDevice
{
    /// <summary>
    /// Submits a command buffer asynchronously to the GPU.
    /// Returns immediately without blocking the CPU thread.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to submit</param>
    /// <returns>A task that completes when GPU work finishes</returns>
    Task SubmitAsync(IComputeCommandBuffer commandBuffer);

    /// <summary>
    /// Submits a command buffer with a completion callback.
    /// Useful for pipelining multiple GPU operations.
    /// </summary>
    /// <param name="commandBuffer">The command buffer to submit</param>
    /// <param name="onComplete">Callback invoked when GPU work completes</param>
    void SubmitAsync(IComputeCommandBuffer commandBuffer, Action onComplete);

    /// <summary>
    /// Creates a fence for explicit GPU synchronization.
    /// </summary>
    /// <returns>A fence object for GPU-CPU synchronization</returns>
    IComputeFence CreateFence();

    /// <summary>
    /// Checks if all GPU work has completed without blocking.
    /// </summary>
    /// <returns>True if GPU is idle, false if work is pending</returns>
    bool IsIdle();

    /// <summary>
    /// Waits for all GPU work to complete with a timeout.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    /// <returns>True if completed, false if timed out</returns>
    bool WaitIdle(int timeoutMs);
}

/// <summary>
/// GPU fence for explicit synchronization between CPU and GPU.
/// </summary>
public interface IComputeFence : IDisposable
{
    /// <summary>
    /// Signals the fence from GPU side.
    /// </summary>
    void Signal(IComputeCommandBuffer commandBuffer);

    /// <summary>
    /// Waits on CPU for the fence to be signaled.
    /// </summary>
    void Wait();

    /// <summary>
    /// Waits on CPU for the fence with a timeout.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    /// <returns>True if signaled, false if timed out</returns>
    bool Wait(int timeoutMs);

    /// <summary>
    /// Checks if the fence has been signaled without blocking.
    /// </summary>
    bool IsSignaled { get; }

    /// <summary>
    /// Resets the fence to unsignaled state.
    /// </summary>
    void Reset();
}
