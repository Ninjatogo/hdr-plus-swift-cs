using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering.Utilities;

/// <summary>
/// Utility methods for texture operations.
/// Extracted from VulkanComputePipeline for better code organization.
/// </summary>
public unsafe class TextureUtilities
{
    private readonly VulkanContext _ctx;
    private readonly VulkanDescriptorManager? _descriptors;
    private readonly VulkanKernelManager? _kernelManager;

    // Lazy-initialized accumulator blit kernel
    private ComputeKernel? _kernelAccumulateCropped;
    private DescriptorSetLayout _accumulateLayout;

    public TextureUtilities(VulkanContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Constructor with kernel manager support for GPU-accelerated operations.
    /// </summary>
    public TextureUtilities(VulkanContext ctx, VulkanDescriptorManager descriptors, VulkanKernelManager kernelManager)
    {
        _ctx = ctx;
        _descriptors = descriptors;
        _kernelManager = kernelManager;
    }

    private void EnsureAccumulateKernel()
    {
        if (_kernelAccumulateCropped is not null || _kernelManager is null) return;

        _accumulateLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.ConversionLayout);
        _kernelAccumulateCropped = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AccumulateCroppedRegion, _accumulateLayout);
    }

    /// <summary>
    /// GPU-based cropped region accumulation. Copies a cropped region from source and adds to accumulator.
    /// This eliminates the expensive CPU round-trip (GetData -> loop -> SetData).
    /// </summary>
    /// <param name="source">Source texture to read from</param>
    /// <param name="accumulator">Accumulator texture to add to (read-write)</param>
    /// <param name="cropLeft">Left crop offset in source</param>
    /// <param name="cropTop">Top crop offset in source</param>
    /// <param name="destPadX">X offset in accumulator</param>
    /// <param name="destPadY">Y offset in accumulator</param>
    /// <param name="width">Width of region to copy</param>
    /// <param name="height">Height of region to copy</param>
    public void AccumulateCroppedRegionGpu(
        VulkanImage source,
        VulkanImage accumulator,
        int cropLeft, int cropTop,
        int destPadX, int destPadY,
        int width, int height)
    {
        if (_kernelManager is null || _descriptors is null)
        {
            // Fallback to CPU if no kernel manager
            AccumulateCroppedRegionCpu(source, accumulator, cropLeft, cropTop, destPadX, destPadY, width, height);
            return;
        }

        EnsureAccumulateKernel();

        // Create params buffer with crop/offset info
        var texParams = new TextureBlitParams
        {
            OffsetX = cropLeft,
            OffsetY = cropTop,
            PadLeft = destPadX,
            PadTop = destPadY,
            Width = width,
            Height = height
        };

        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureBlitParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([texParams]);

        var cmd = _ctx.BeginSingleTimeCommands();

        source.TransitionLayout(ImageLayout.General, cmd);
        accumulator.TransitionLayout(ImageLayout.General, cmd);

        var set = _descriptors.Allocate(_accumulateLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureBlitParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, source.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 10, accumulator.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelAccumulateCropped!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelAccumulateCropped.PipelineLayout, 0, 1, &set, 0, null);
        _kernelAccumulateCropped.Dispatch(cmd, (uint)width, (uint)height, 1);

        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// CPU fallback for cropped region accumulation (used when kernel manager not available).
    /// </summary>
    private void AccumulateCroppedRegionCpu(
        VulkanImage source,
        VulkanImage accumulator,
        int cropLeft, int cropTop,
        int destPadX, int destPadY,
        int width, int height)
    {
        var srcData = source.GetData<float>();
        var accData = accumulator.GetData<float>();

        var srcWidth = (int)source.Width;
        var accWidth = (int)accumulator.Width;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var srcIdx = (cropTop + y) * srcWidth + (cropLeft + x);
                var dstIdx = (destPadY + y) * accWidth + (destPadX + x);

                if (srcIdx < srcData.Length && dstIdx < accData.Length)
                {
                    accData[dstIdx] += srcData[srcIdx];
                }
            }
        }

        accumulator.SetData(accData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureBlitParams
    {
        public float WhiteLevel;      // Unused, padding for layout compatibility
        public float BlackLevel;      // Unused
        public float BlackLevelMean;  // Unused
        public float ScaleFactor;     // Unused
        public int CfaPattern;        // Unused
        public int Width;
        public int Height;
        public int OffsetX;           // cropLeft (source)
        public int OffsetY;           // cropTop (source)
        public int InputWidth;        // Unused
        public int InputHeight;       // Unused
        public int PadLeft;           // destPadX
        public int PadTop;            // destPadY
    }

    /// <summary>
    /// Fills a texture with zeros.
    /// </summary>
    public void FillWithZeros(VulkanImage texture)
    {
        var channels = texture.Format == Format.R32Sfloat ? 1 : 4; // R32 or RGBA32
        var size = (int)(texture.Width * texture.Height * channels);
        texture.SetData(new float[size]);
    }

    /// <summary>
    /// Adds source texture to accumulator with optional weight (CPU-based).
    /// </summary>
    public void AddTexture(VulkanImage source, VulkanImage accumulator, float weight = 1.0f)
    {
        var srcData = source.GetData<float>();
        var accData = accumulator.GetData<float>();
        for (var i = 0; i < srcData.Length; i++)
        {
            accData[i] += srcData[i] * weight;
        }
        accumulator.SetData(accData);
    }

    /// <summary>
    /// Calculates mean value of texture (uses first channel only for multi-channel textures).
    /// Used for mismatch normalization.
    /// </summary>
    public float TextureMean(VulkanImage texture)
    {
        var data = texture.GetData<float>();
        double sum = 0;
        var channels = texture.Format == Format.R32Sfloat ? 1 : 4;
        for (var i = 0; i < data.Length; i += channels)
        {
            sum += data[i]; // Use first channel only
        }

        return (float)(sum / (data.Length / channels));
    }

    /// <summary>
    /// GPU-based image copy using Vulkan's vkCmdCopyImage.
    /// </summary>
    public void CopyImage(VulkanImage src, VulkanImage dst, int width, int height)
    {
        var copyCmd = _ctx.BeginSingleTimeCommands();
        dst.TransitionLayout(ImageLayout.TransferDstOptimal, copyCmd);
        src.TransitionLayout(ImageLayout.TransferSrcOptimal, copyCmd);

        var copyRegion = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D((uint)width, (uint)height, 1)
        };
        _ctx.Vk.CmdCopyImage(copyCmd, src.Handle, ImageLayout.TransferSrcOptimal, dst.Handle, ImageLayout.TransferDstOptimal, 1, &copyRegion);

        dst.TransitionLayout(ImageLayout.General, copyCmd);
        src.TransitionLayout(ImageLayout.General, copyCmd);
        _ctx.EndSingleTimeCommands(copyCmd);
    }
}
