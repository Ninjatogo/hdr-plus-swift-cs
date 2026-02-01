using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Implementations;

/// <summary>
/// A mock compute pipeline that passes through input unchanged.
/// Used for testing the pipeline logic without GPU dependencies.
/// </summary>
public class PassthroughComputePipeline : IComputePipeline
{
    public Task<RawImage> ProcessAsync(
        RenderingInput input,
        ProcessingOptions options,
        ProcessingProgress progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine("[PassthroughComputePipeline] Using mock compute (no GPU processing)");
        progress.Update(85_000_000, "Processing (passthrough)...");

        return Task.FromResult(input.Images[input.ReferenceFrameIndex]);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
