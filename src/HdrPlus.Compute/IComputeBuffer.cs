namespace HdrPlus.Compute;

/// <summary>
/// Represents a GPU buffer (Metal: MTLBuffer, DX12: ID3D12Resource)
/// </summary>
public interface IComputeBuffer : IDisposable
{
    /// <summary>
    /// Size of the buffer in bytes.
    /// </summary>
    int SizeInBytes { get; }

    /// <summary>
    /// Reads data from GPU to CPU (blocking operation).
    /// </summary>
    void ReadData<T>(Span<T> destination) where T : unmanaged;

    /// <summary>
    /// Writes data from CPU to GPU (blocking operation).
    /// </summary>
    void WriteData<T>(ReadOnlySpan<T> source) where T : unmanaged;

    /// <summary>
    /// Maps the buffer for CPU access (for Upload/Readback buffers).
    /// </summary>
    unsafe void* Map();

    /// <summary>
    /// Unmaps the buffer after CPU access.
    /// </summary>
    void Unmap();
}
