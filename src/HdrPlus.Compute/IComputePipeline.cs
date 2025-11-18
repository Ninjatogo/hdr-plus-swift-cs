namespace HdrPlus.Compute;

/// <summary>
/// Represents a compiled compute shader pipeline (Metal: MTLComputePipelineState, DX12: ID3D12PipelineState)
/// </summary>
public interface IComputePipeline : IDisposable
{
    string Name { get; }
    string EntryPoint { get; }

    /// <summary>
    /// Gets the optimal thread group size for this pipeline.
    /// </summary>
    (int X, int Y, int Z) GetThreadGroupSize();
}
