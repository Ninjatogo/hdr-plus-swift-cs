using System;
using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Rendering;
using BurstPhoto.Rendering.Implementations;

namespace BurstPhoto.GUI.Services;

/// <summary>
/// Factory for creating denoise pipelines with configurable GPU selection.
/// </summary>
public class DenoisePipelineFactory
{
    private readonly IRawImageLoader _loader;
    private readonly IRawImageWriter _writer;

    public DenoisePipelineFactory(IRawImageLoader loader, IRawImageWriter writer)
    {
        _loader = loader;
        _writer = writer;
    }

    /// <summary>
    /// Creates a new denoise pipeline with the specified GPU.
    /// </summary>
    /// <param name="gpuIndex">GPU index, or null for auto-detection.</param>
    /// <returns>A new denoise pipeline instance.</returns>
    public IDenoisePipeline Create(int? gpuIndex)
    {
        IComputePipeline computePipeline;

        try
        {
            var ctx = new VulkanContext(gpuIndex);
            computePipeline = new VulkanComputePipeline(ctx);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARNING] Vulkan initialization failed: {ex.Message}");
            computePipeline = new PassthroughComputePipeline();
        }

        return new DenoisePipeline(_loader, _writer, computePipeline);
    }
}
