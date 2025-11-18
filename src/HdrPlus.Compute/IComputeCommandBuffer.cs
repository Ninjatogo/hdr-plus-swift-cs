namespace HdrPlus.Compute;

/// <summary>
/// Records GPU commands for execution (Metal: MTLCommandBuffer + MTLComputeCommandEncoder, DX12: ID3D12GraphicsCommandList)
/// </summary>
public interface IComputeCommandBuffer : IDisposable
{
    string? Label { get; set; }

    /// <summary>
    /// Begins a compute pass.
    /// </summary>
    void BeginCompute(string? label = null);

    /// <summary>
    /// Sets the active compute pipeline.
    /// </summary>
    void SetPipeline(IComputePipeline pipeline);

    /// <summary>
    /// Binds a buffer to a shader slot.
    /// </summary>
    void SetBuffer(IComputeBuffer buffer, int slot);

    /// <summary>
    /// Binds a texture to a shader slot.
    /// </summary>
    void SetTexture(IComputeTexture texture, int slot);

    /// <summary>
    /// Binds constant data directly (for small data like parameters).
    /// </summary>
    void SetBytes<T>(T data, int slot) where T : unmanaged;

    /// <summary>
    /// Dispatches compute work.
    /// </summary>
    void Dispatch(int threadGroupsX, int threadGroupsY, int threadGroupsZ);

    /// <summary>
    /// Dispatches compute work with exact thread counts (auto-calculates thread groups).
    /// </summary>
    void DispatchThreads(int threadsX, int threadsY, int threadsZ);

    /// <summary>
    /// Ends the compute pass.
    /// </summary>
    void EndCompute();

    /// <summary>
    /// Copies data from one buffer to another.
    /// </summary>
    void CopyBuffer(IComputeBuffer source, IComputeBuffer destination, int size);

    /// <summary>
    /// Copies a texture region.
    /// </summary>
    void CopyTexture(IComputeTexture source, IComputeTexture destination);
}
