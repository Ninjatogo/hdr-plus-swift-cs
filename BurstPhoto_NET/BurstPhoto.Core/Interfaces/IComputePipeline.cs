using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

public interface IComputePipeline : IDisposable
{
    Task<RawImage> ProcessAsync(RenderingInput input, ProcessingOptions options, ProcessingProgress progress);
}
