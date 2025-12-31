using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

public interface IComputePipeline : IDisposable
{
    Task<RawImage> ProcessAsync(RawImage input, ProcessingProgress progress);
}
