using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.Threading.Tasks;

namespace BurstPhoto.Rendering.Implementations;

public class VulkanComputePipeline : IComputePipeline
{
    private readonly VulkanContext _ctx;

    public VulkanComputePipeline(VulkanContext ctx)
    {
        _ctx = ctx;
    }

    public Task<RawImage> ProcessAsync(RawImage input, ProcessingProgress progress)
    {
        // TODO: Implement actual compute shader execution.
        // For Vertical Slice, we just return the input to prove the CLI and Loader/Writer are connected.
        // The Vulkan Context is initialized in the constructor, so we are verifying Vulkan initialization.
        return Task.FromResult(input);
    }

    public void Dispose()
    {
        _ctx.Dispose();
    }
}
