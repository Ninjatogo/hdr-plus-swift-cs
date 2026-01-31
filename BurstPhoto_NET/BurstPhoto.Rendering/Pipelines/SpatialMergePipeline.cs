using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering.Pipelines;

/// <summary>
/// Handles spatial domain merging of aligned frames.
/// Computes merge weights based on color differences and accumulates weighted pixel values.
/// Supports HDR merging with exposure compensation.
/// </summary>
public unsafe class SpatialMergePipeline
{
    private readonly VulkanContext _ctx;
    private readonly VulkanDescriptorManager _descriptors;
    private readonly VulkanKernelManager _kernelManager;

    // Merge kernels and layouts
    private DescriptorSetLayout _mergeLayout;
    private DescriptorSetLayout _accumLayout;
    private DescriptorSetLayout _accumHighLayout;

    private ComputeKernel? _kernelColorDiff;
    private ComputeKernel? _kernelMergeWeight;
    private ComputeKernel? _kernelAddWeighted;
    private ComputeKernel? _kernelAddWeightOnly;
    private ComputeKernel? _kernelAddExposure;
    private ComputeKernel? _kernelAddHighlights;
    private ComputeKernel? _kernelDivideTextures;

    public SpatialMergePipeline(VulkanContext ctx, VulkanDescriptorManager descriptors, VulkanKernelManager kernelManager)
    {
        _ctx = ctx;
        _descriptors = descriptors;
        _kernelManager = kernelManager;
    }

    private void EnsureKernels()
    {
        if (_kernelColorDiff is not null) return;

        _mergeLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.MergeLayout);
        _accumLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.AccumLayout);
        _accumHighLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.AccumHighLayout);

        _kernelColorDiff = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ColorDiff, _mergeLayout);
        _kernelMergeWeight = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.MergeWeight, _mergeLayout);
        _kernelAddWeighted = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AddWeighted, _accumLayout);
        _kernelAddWeightOnly = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AddWeightOnly, _accumLayout);
        _kernelAddExposure = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AddExposure, _accumLayout);
        _kernelAddHighlights = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AddHighlights, _accumHighLayout);
        _kernelDivideTextures = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.DivideTextures, _accumLayout);
    }

    /// <summary>
    /// Calculates robustness factor from noise reduction setting.
    /// </summary>
    private static float CalculateRobustness(double noiseReduction)
    {
        return (float)noiseReduction;
    }

    /// <summary>
    /// Computes merge weight and performs weighted add: accumulator += warped * weight.
    /// Handles exposure differences (HDR merge) if exposureDiff != 0.
    /// </summary>
    /// <param name="referenceFrame">Reference frame texture</param>
    /// <param name="warpedFrame">Warped alternate frame texture</param>
    /// <param name="weightAccum">Weight accumulator texture</param>
    /// <param name="pixelAccum">Pixel value accumulator texture</param>
    /// <param name="whiteLevel">White level for normalization</param>
    /// <param name="blackLevel">Black level for normalization</param>
    /// <param name="noiseReduction">Noise reduction strength (0-1)</param>
    /// <param name="noiseSd">Estimated noise standard deviation</param>
    /// <param name="exposureDiff">Exposure difference in stops (ref - alt)</param>
    public void ExecuteMerge(
        VulkanImage referenceFrame,
        VulkanImage warpedFrame,
        VulkanImage weightAccum,
        VulkanImage pixelAccum,
        float whiteLevel,
        float blackLevel,
        double noiseReduction,
        float noiseSd,
        float exposureDiff)
    {
        EnsureKernels();

        // 1. Compute color difference (ref - warped) -> diff texture
        using var diffTex = new VulkanImage(_ctx, warpedFrame.Width, warpedFrame.Height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

        // 2. Compute merge weight from diff -> weight texture
        using var weightTex = new VulkanImage(_ctx, warpedFrame.Width, warpedFrame.Height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

        var robustness = CalculateRobustness(noiseReduction);

        var spatialParams = new SpatialParams
        {
            WhiteLevel = whiteLevel,
            BlackLevel = blackLevel,
            Robustness = robustness,
            NoiseSd = noiseSd
        };

        Console.WriteLine($"[SpatialMergePipeline] Merge: Robustness={robustness:F4}, NoiseSd={noiseSd:F4} (NR={noiseReduction})");

        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<SpatialParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([spatialParams]);

        // Command Buffer
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _ctx.CommandPool,
            CommandBufferCount = 1
        };
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

        // Transitions
        referenceFrame.TransitionLayout(ImageLayout.General, cmdBuffer);
        warpedFrame.TransitionLayout(ImageLayout.General, cmdBuffer);
        diffTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        weightTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        weightAccum.TransitionLayout(ImageLayout.General, cmdBuffer);
        pixelAccum.TransitionLayout(ImageLayout.General, cmdBuffer);

        // --- Pass 1: color_difference ---
        using var dummyDiff = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyDiff.TransitionLayout(ImageLayout.General, cmdBuffer);

        var setDiff = _descriptors.Allocate(_mergeLayout);
        _descriptors.UpdateBuffer(setDiff, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<SpatialParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setDiff, 1, referenceFrame.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setDiff, 2, warpedFrame.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setDiff, 3, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setDiff, 10, diffTex.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelColorDiff!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelColorDiff.PipelineLayout, 0, 1, &setDiff, 0, null);
        _kernelColorDiff.Dispatch(cmdBuffer, diffTex.Width, diffTex.Height, 1);

        // Barrier
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // --- Pass 2: compute_merge_weight ---
        var setWeight = _descriptors.Allocate(_mergeLayout);
        _descriptors.UpdateBuffer(setWeight, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<SpatialParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setWeight, 1, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setWeight, 2, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setWeight, 3, diffTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setWeight, 10, weightTex.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelMergeWeight!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelMergeWeight.PipelineLayout, 0, 1, &setWeight, 0, null);
        _kernelMergeWeight.Dispatch(cmdBuffer, weightTex.Width, weightTex.Height, 1);

        // Barriers
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // --- Pass 3: Accumulation (Branching based on Exposure) ---
        // exposureDiff = Ref - Alt
        // If diff > 0.1 => Alt is Darker => Highlight Recovery
        // If diff < -0.1 => Alt is Brighter => Add Exposure
        var isAltUnderexposed = exposureDiff > 0.1f;
        var isAltOverexposed = exposureDiff < -0.1f;

        if (isAltUnderexposed)
        {
            // --- Highlight Recovery (Alt is Darker) ---
            var scaleFactor = (float)Math.Pow(2.0, exposureDiff);

            var tParams = new TextureParams
            {
                WhiteLevel = whiteLevel,
                BlackLevel = blackLevel,
                BlackLevelMean = 0,
                ScaleFactor = scaleFactor,
                ExposureDiff = (int)(exposureDiff * 100)
            };

            using var tParamBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(),
                BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
            tParamBuffer.SetData([tParams]);

            var setAccumHigh = _descriptors.Allocate(_accumHighLayout);
            _descriptors.UpdateBuffer(setAccumHigh, 0, tParamBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setAccumHigh, 1, warpedFrame.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setAccumHigh, 4, weightTex.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setAccumHigh, 10, pixelAccum.View, ImageLayout.General, DescriptorType.StorageImage);
            _descriptors.UpdateImage(setAccumHigh, 13, weightAccum.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelAddHighlights!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddHighlights.PipelineLayout, 0, 1, &setAccumHigh, 0, null);
            _kernelAddHighlights.Dispatch(cmdBuffer, pixelAccum.Width, pixelAccum.Height, 1);
        }
        else
        {
            // Standard (diff ~ 0) or Brighter (diff < 0) Path
            var scaleFactor = 1.0f;
            if (isAltOverexposed) scaleFactor = (float)Math.Pow(2.0, exposureDiff);

            var tParams = new TextureParams
            {
                WhiteLevel = whiteLevel,
                BlackLevel = blackLevel,
                BlackLevelMean = 0,
                ScaleFactor = scaleFactor
            };
            using var tParamBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(),
                BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
            tParamBuffer.SetData([tParams]);

            var setPixelAccum = _descriptors.Allocate(_accumLayout);
            _descriptors.UpdateBuffer(setPixelAccum, 0, tParamBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setPixelAccum, 1, warpedFrame.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setPixelAccum, 4, weightTex.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setPixelAccum, 10, pixelAccum.View, ImageLayout.General, DescriptorType.StorageImage);

            if (isAltOverexposed)
            {
                _kernelAddExposure!.BindPipeline(cmdBuffer);
                _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddExposure.PipelineLayout, 0, 1, &setPixelAccum, 0, null);
                _kernelAddExposure.Dispatch(cmdBuffer, pixelAccum.Width, pixelAccum.Height, 1);
                Console.WriteLine($"[SpatialMergePipeline] Merge: Add Exposure (Diff={exposureDiff:F2})");
            }
            else
            {
                _kernelAddWeighted!.BindPipeline(cmdBuffer);
                _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddWeighted.PipelineLayout, 0, 1, &setPixelAccum, 0, null);
                _kernelAddWeighted.Dispatch(cmdBuffer, pixelAccum.Width, pixelAccum.Height, 1);
            }

            // Barrier
            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

            // --- Pass 3b: GPU Weight Accumulation ---
            var setWeightAccum = _descriptors.Allocate(_accumLayout);
            _descriptors.UpdateBuffer(setWeightAccum, 0, tParamBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setWeightAccum, 1, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setWeightAccum, 4, weightTex.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setWeightAccum, 10, weightAccum.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelAddWeightOnly!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddWeightOnly.PipelineLayout, 0, 1, &setWeightAccum, 0, null);
            _kernelAddWeightOnly.Dispatch(cmdBuffer, weightAccum.Width, weightAccum.Height, 1);
        }

        _ctx.Vk.EndCommandBuffer(cmdBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };
        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);
        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
    }

    /// <summary>
    /// GPU-based normalization: output = pixelAccum / weightAccum.
    /// This replaces the CPU-based loop that downloads both textures, divides, and re-uploads.
    /// Eliminates two GetData() calls and improves performance.
    /// </summary>
    /// <param name="pixelAccum">Pixel accumulator texture</param>
    /// <param name="weightAccum">Weight accumulator texture</param>
    /// <param name="output">Output texture for normalized result</param>
    public void NormalizeAccumulators(VulkanImage pixelAccum, VulkanImage weightAccum, VulkanImage output)
    {
        EnsureKernels();

        var cmd = _ctx.BeginSingleTimeCommands();

        pixelAccum.TransitionLayout(ImageLayout.General, cmd);
        weightAccum.TransitionLayout(ImageLayout.General, cmd);
        output.TransitionLayout(ImageLayout.General, cmd);

        // Create a dummy param buffer (divide_textures doesn't need params, but layout requires binding 0)
        var dummyParams = new TextureParams();
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData([dummyParams]);

        var set = _descriptors.Allocate(_accumLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, pixelAccum.View, ImageLayout.General, DescriptorType.SampledImage);  // t0 = InTextureFloat
        _descriptors.UpdateImage(set, 4, weightAccum.View, ImageLayout.General, DescriptorType.SampledImage); // t3 = AuxTextureFloat
        _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage);     // u10 = OutTextureFloat

        _kernelDivideTextures!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelDivideTextures.PipelineLayout, 0, 1, &set, 0, null);
        _kernelDivideTextures.Dispatch(cmd, output.Width, output.Height, 1);

        _ctx.EndSingleTimeCommands(cmd);
    }
}
