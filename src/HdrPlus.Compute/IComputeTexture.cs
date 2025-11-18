namespace HdrPlus.Compute;

/// <summary>
/// Represents a GPU texture (Metal: MTLTexture, DX12: ID3D12Resource)
/// </summary>
public interface IComputeTexture : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Depth { get; }
    TextureFormat Format { get; }
    string? Label { get; set; }

    /// <summary>
    /// Reads pixel data from GPU to CPU (blocking operation).
    /// </summary>
    void ReadData<T>(Span<T> destination) where T : unmanaged;

    /// <summary>
    /// Writes pixel data from CPU to GPU (blocking operation).
    /// </summary>
    void WriteData<T>(ReadOnlySpan<T> source) where T : unmanaged;
}
