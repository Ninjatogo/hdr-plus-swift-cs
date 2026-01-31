using BurstPhoto.Core.Models;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering.Pipelines;

/// <summary>
/// Handles exposure correction, noise estimation, and related blur operations.
/// Extracted from VulkanComputePipeline for better code organization.
/// </summary>
public unsafe class ExposurePipeline(
    VulkanContext ctx,
    VulkanDescriptorManager descriptors,
    VulkanKernelManager kernelManager)
{
    // Noise estimation kernels and layout
    private DescriptorSetLayout _noiseEstLayout;
    private ComputeKernel? _kernelBlurMosaic;
    private ComputeKernel? _kernelColorDiffSuperpixel;
    private ComputeKernel? _kernelSumColumns;
    private ComputeKernel? _kernelSumRows;

    // Exposure correction kernels and layout
    private DescriptorSetLayout _exposureLayout;
    private ComputeKernel? _kernelCorrectExposure;
    private ComputeKernel? _kernelCorrectExposureLinear;
    private ComputeKernel? _kernelMaxY;
    private ComputeKernel? _kernelMaxX;

    private void EnsureNoiseEstKernels()
    {
        if (_kernelBlurMosaic is not null) return;

        _noiseEstLayout = kernelManager.GetOrCreateLayout(PipelineKernelSpecs.NoiseEstLayout);
        _kernelBlurMosaic = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.BlurMosaic, _noiseEstLayout);
        _kernelColorDiffSuperpixel = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ColorDiffSuperpixel, _noiseEstLayout);
        _kernelSumColumns = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.SumColumns, _noiseEstLayout);
        _kernelSumRows = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.SumRows, _noiseEstLayout);
    }

    private void EnsureExposureKernels()
    {
        if (_kernelCorrectExposure is not null) return;

        _exposureLayout = kernelManager.GetOrCreateLayout(PipelineKernelSpecs.ExposureLayout);
        _kernelCorrectExposure = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.CorrectExposure, _exposureLayout);
        _kernelCorrectExposureLinear = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.CorrectExposureLinear, _exposureLayout);
        _kernelMaxY = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.MaxY, _exposureLayout);
        _kernelMaxX = kernelManager.GetOrCreateKernel(PipelineKernelSpecs.MaxX, _exposureLayout);
    }

    /// <summary>
    /// Executes a Gaussian blur on the input texture using separable passes.
    /// Can be used for noise estimation or exposure control.
    /// </summary>
    /// <param name="input">Input texture to blur</param>
    /// <param name="output">Output blurred texture</param>
    /// <param name="kernelSize">Size of the blur kernel</param>
    /// <param name="mosaicPatternWidth">Mosaic pattern width (2 for Bayer)</param>
    /// <param name="intermediate">Optional intermediate texture to reuse</param>
    public void ExecuteBlur(VulkanImage input, VulkanImage output, int kernelSize, int mosaicPatternWidth, VulkanImage? intermediate = null)
    {
        EnsureNoiseEstKernels();

        var width = input.Width;
        var height = input.Height;

        // Intermediate texture (X-blurred)
        var ownInter = false;
        if (intermediate is null)
        {
            intermediate = new VulkanImage(ctx, width, height, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
            ownInter = true;
        }

        using var dummyTex = new VulkanImage(ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyTex.SetData(new float[] { 0 });

        var texParamsBlurX = new TextureParams
        {
            KernelSize = kernelSize,
            MosaicPatternWidth = mosaicPatternWidth,
            TextureSize = (int)width,
            Direction = 0,
            Width = (int)width,
            Height = (int)height
        };

        var texParamsBlurY = texParamsBlurX;
        texParamsBlurY.Direction = 1;
        texParamsBlurY.TextureSize = (int)height;

        using var paramBufferBlurX = new VulkanBuffer(ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBufferBlurX.SetData([texParamsBlurX]);

        using var paramBufferBlurY = new VulkanBuffer(ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBufferBlurY.SetData([texParamsBlurY]);

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = ctx.CommandPool,
            CommandBufferCount = 1
        };
        ctx.Vk.AllocateCommandBuffers(ctx.Device, in allocInfo, out var cmdBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer);
        intermediate.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);

        // PASS 1: X Blur
        var setBlurX = descriptors.Allocate(_noiseEstLayout);
        descriptors.UpdateBuffer(setBlurX, 0, paramBufferBlurX.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        descriptors.UpdateImage(setBlurX, 1, input.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setBlurX, 4, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setBlurX, 10, intermediate.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelBlurMosaic!.BindPipeline(cmdBuffer);
        ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelBlurMosaic.PipelineLayout, 0, 1, &setBlurX, 0, null);
        _kernelBlurMosaic.Dispatch(cmdBuffer, width, height, 1);

        // Barrier
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // PASS 2: Y Blur
        var setBlurY = descriptors.Allocate(_noiseEstLayout);
        descriptors.UpdateBuffer(setBlurY, 0, paramBufferBlurY.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        descriptors.UpdateImage(setBlurY, 1, intermediate.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setBlurY, 4, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setBlurY, 10, output.View, ImageLayout.General, DescriptorType.StorageImage);

        ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelBlurMosaic.PipelineLayout, 0, 1, &setBlurY, 0, null);
        _kernelBlurMosaic.Dispatch(cmdBuffer, width, height, 1);

        ctx.Vk.EndCommandBuffer(cmdBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };
        ctx.Vk.QueueSubmit(ctx.ComputeQueue, 1, in submitInfo, default);
        ctx.Vk.QueueWaitIdle(ctx.ComputeQueue);

        ctx.Vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, in cmdBuffer);

        if (ownInter) intermediate.Dispose();
    }

    /// <summary>
    /// Performs GPU max reduction to find the maximum value in a texture.
    /// </summary>
    /// <param name="input">Input texture</param>
    /// <param name="outBuffer">Output buffer to store the max value</param>
    /// <param name="mosaicPatternWidth">Mosaic pattern width</param>
    public void ExecuteMaxReduction(VulkanImage input, VulkanBuffer outBuffer, int mosaicPatternWidth)
    {
        EnsureExposureKernels();

        // 1. Max Y -> 1D texture
        using var maxYTex = new VulkanImage(ctx, input.Width, 1, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

        var exParams = new ExposureParams
        {
            TextureWidth = (int)input.Width
        };

        using var paramBuffer = new VulkanBuffer(ctx, (ulong)Marshal.SizeOf<ExposureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData([exParams]);

        using var dummyTex = new VulkanImage(ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        using var dummyBuff = new VulkanBuffer(ctx, 4, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit);

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = ctx.CommandPool,
            CommandBufferCount = 1
        };
        ctx.Vk.AllocateCommandBuffers(ctx.Device, in allocInfo, out var cmdBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        maxYTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);

        // Max Y
        var setMaxY = descriptors.Allocate(_exposureLayout);
        descriptors.UpdateBuffer(setMaxY, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<ExposureParams>(), DescriptorType.UniformBuffer);
        descriptors.UpdateImage(setMaxY, 1, input.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setMaxY, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateBuffer(setMaxY, 3, dummyBuff.Handle, 4, DescriptorType.StorageBuffer);
        descriptors.UpdateBuffer(setMaxY, 4, dummyBuff.Handle, 4, DescriptorType.StorageBuffer);
        descriptors.UpdateImage(setMaxY, 10, maxYTex.View, ImageLayout.General, DescriptorType.StorageImage);
        descriptors.UpdateBuffer(setMaxY, 11, dummyBuff.Handle, 4, DescriptorType.StorageBuffer);

        _kernelMaxY!.BindPipeline(cmdBuffer);
        ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelMaxY.PipelineLayout, 0, 1, &setMaxY, 0, null);
        ctx.Vk.CmdDispatch(cmdBuffer, input.Width, 1, 1);

        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // Max X
        var setMaxX = descriptors.Allocate(_exposureLayout);
        descriptors.UpdateBuffer(setMaxX, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<ExposureParams>(), DescriptorType.UniformBuffer);
        descriptors.UpdateImage(setMaxX, 1, maxYTex.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setMaxX, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateBuffer(setMaxX, 3, dummyBuff.Handle, 4, DescriptorType.StorageBuffer);
        descriptors.UpdateBuffer(setMaxX, 4, dummyBuff.Handle, 4, DescriptorType.StorageBuffer);
        descriptors.UpdateImage(setMaxX, 10, dummyTex.View, ImageLayout.General, DescriptorType.StorageImage);
        descriptors.UpdateBuffer(setMaxX, 11, outBuffer.Handle, 4, DescriptorType.StorageBuffer);

        _kernelMaxX!.BindPipeline(cmdBuffer);
        ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelMaxX.PipelineLayout, 0, 1, &setMaxX, 0, null);
        ctx.Vk.CmdDispatch(cmdBuffer, 1, 1, 1);

        ctx.Vk.EndCommandBuffer(cmdBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };
        ctx.Vk.QueueSubmit(ctx.ComputeQueue, 1, in submitInfo, default);
        ctx.Vk.QueueWaitIdle(ctx.ComputeQueue);
        ctx.Vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, in cmdBuffer);
    }

    /// <summary>
    /// Applies exposure correction to the image in-place.
    /// </summary>
    /// <param name="image">Image to correct (modified in place)</param>
    /// <param name="option">Exposure control option</param>
    /// <param name="metadata">Raw image metadata</param>
    public void ExecuteExposureCorrection(VulkanImage image, ExposureControlOption option, RawImage metadata)
    {
        if (option == ExposureControlOption.Off) return;

        Console.WriteLine($"[ExposurePipeline] Executing Exposure Correction: {option}");
        EnsureExposureKernels();

        var isCurve = option is ExposureControlOption.Curve0Ev or ExposureControlOption.Curve1Ev;
        var linearGain = option switch
        {
            ExposureControlOption.LinearFullRange => -1.0f,
            ExposureControlOption.Linear1Ev => 2.0f,
            ExposureControlOption.Curve0Ev => 1.0f,
            ExposureControlOption.Curve1Ev => 2.0f,
            _ => -1.0f
        };

        VulkanImage? blurredTex = null;
        using var maxBuffer = new VulkanBuffer(ctx, 4, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit);

        // 1. Prepare Data
        if (isCurve)
        {
            var kSize = (metadata.MosaicPatternWidth == 2) ? 1 : 2;
            blurredTex = new VulkanImage(ctx, image.Width, image.Height, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
            ExecuteBlur(image, blurredTex, kSize, metadata.MosaicPatternWidth);
            ExecuteMaxReduction(blurredTex, maxBuffer, metadata.MosaicPatternWidth);
        }
        else
        {
            ExecuteMaxReduction(image, maxBuffer, metadata.MosaicPatternWidth);
        }

        // 2. Prepare Params
        var colorMean = 1.0f;
        if (metadata.ColorFactors.Length >= 3)
        {
            colorMean = (float)((metadata.ColorFactors[0] + metadata.ColorFactors[1] + metadata.ColorFactors[2]) / 3.0);
        }

        var blArray = new float[4];
        float blMean = 0;
        var blMin = float.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            var v = (i < metadata.BlackLevel.Length) ? metadata.BlackLevel[i] : (float)metadata.BlackLevel[0];
            blArray[i] = v;
            blMean += v;
            if (v < blMin) blMin = v;
        }
        blMean /= 4.0f;

        using var blParams = new VulkanBuffer(ctx, 4 * sizeof(float), BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit);
        blParams.SetData(blArray);

        var exParams = new ExposureParams
        {
            WhiteLevel = metadata.WhiteLevel,
            LinearGain = linearGain,
            ColorFactorMean = colorMean,
            BlackLevelMean = blMean,
            BlackLevelMin = blMin,
            ExposureBias = metadata.ExposureBias,
            TargetExposure = option.ToString().Contains("1EV") ? metadata.ExposureBias + 100 : metadata.ExposureBias,
            MosaicPatternWidth = metadata.MosaicPatternWidth,
            TextureWidth = (int)image.Width
        };

        using var paramBuffer = new VulkanBuffer(ctx, (ulong)Marshal.SizeOf<ExposureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData([exParams]);

        // 3. Dispatch
        using var dummyBuff = new VulkanBuffer(ctx, 4, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit);

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = ctx.CommandPool,
            CommandBufferCount = 1
        };
        ctx.Vk.AllocateCommandBuffers(ctx.Device, in allocInfo, out var cmdBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

        image.TransitionLayout(ImageLayout.General, cmdBuffer);
        blurredTex?.TransitionLayout(ImageLayout.General, cmdBuffer);

        var set = descriptors.Allocate(_exposureLayout);
        descriptors.UpdateBuffer(set, ShaderBindings.Exposure.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<ExposureParams>(), DescriptorType.UniformBuffer);
        descriptors.UpdateImage(set, ShaderBindings.Exposure.InputTexture, image.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(set, ShaderBindings.Exposure.BlurredTexture, blurredTex?.View ?? image.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateBuffer(set, ShaderBindings.Exposure.BlackLevelsBuffer, blParams.Handle, 4 * sizeof(float), DescriptorType.StorageBuffer);
        descriptors.UpdateBuffer(set, ShaderBindings.Exposure.MaxBuffer, maxBuffer.Handle, 4, DescriptorType.StorageBuffer);
        descriptors.UpdateImage(set, ShaderBindings.Exposure.OutputTexture, image.View, ImageLayout.General, DescriptorType.StorageImage);
        descriptors.UpdateBuffer(set, ShaderBindings.Exposure.OutputBuffer, dummyBuff.Handle, 4, DescriptorType.StorageBuffer);

        var kernel = isCurve ? _kernelCorrectExposure : _kernelCorrectExposureLinear;
        kernel!.BindPipeline(cmdBuffer);
        ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
        ctx.Vk.CmdDispatch(cmdBuffer, image.Width, image.Height, 1);

        ctx.Vk.EndCommandBuffer(cmdBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };
        ctx.Vk.QueueSubmit(ctx.ComputeQueue, 1, in submitInfo, default);
        ctx.Vk.QueueWaitIdle(ctx.ComputeQueue);
        ctx.Vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, in cmdBuffer);

        blurredTex?.Dispose();
    }

    /// <summary>
    /// GPU-based noise estimation using blur -> difference -> reduction pipeline.
    /// </summary>
    /// <param name="inputTexture">Input texture to estimate noise from</param>
    /// <param name="mosaicPatternWidth">Mosaic pattern width (2 for Bayer)</param>
    /// <returns>Estimated noise standard deviation</returns>
    public float ExecuteNoiseEstimationGpu(VulkanImage inputTexture, int mosaicPatternWidth)
    {
        EnsureNoiseEstKernels();

        var width = inputTexture.Width;
        var height = inputTexture.Height;
        var kernelSize = 4;

        // Allocate intermediate textures
        using var blurredXy = new VulkanImage(ctx, width, height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

        ExecuteBlur(inputTexture, blurredXy, kernelSize, mosaicPatternWidth);

        // diffTexture is (width/mosaic, height/mosaic) - one value per superpixel
        var diffW = width / (uint)mosaicPatternWidth;
        var diffH = height / (uint)mosaicPatternWidth;
        using var diffTexture = new VulkanImage(ctx, diffW, diffH, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

        var texParams = new TextureParams
        {
            MosaicPatternWidth = mosaicPatternWidth,
            Width = (int)width,
            Height = (int)height
        };

        using var paramBufferDiff = new VulkanBuffer(ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBufferDiff.SetData([texParams]);

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = ctx.CommandPool,
            CommandBufferCount = 1
        };
        ctx.Vk.AllocateCommandBuffers(ctx.Device, in allocInfo, out var cmdBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

        inputTexture.TransitionLayout(ImageLayout.General, cmdBuffer);
        blurredXy.TransitionLayout(ImageLayout.General, cmdBuffer);
        diffTexture.TransitionLayout(ImageLayout.General, cmdBuffer);

        // Color Difference
        var setDiff = descriptors.Allocate(_noiseEstLayout);
        descriptors.UpdateBuffer(setDiff, 0, paramBufferDiff.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        descriptors.UpdateImage(setDiff, 1, inputTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setDiff, 4, blurredXy.View, ImageLayout.General, DescriptorType.SampledImage);
        descriptors.UpdateImage(setDiff, 10, diffTexture.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelColorDiffSuperpixel!.BindPipeline(cmdBuffer);
        ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelColorDiffSuperpixel.PipelineLayout, 0, 1, &setDiff, 0, null);
        _kernelColorDiffSuperpixel.Dispatch(cmdBuffer, diffW, diffH, 1);

        ctx.Vk.EndCommandBuffer(cmdBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };
        ctx.Vk.QueueSubmit(ctx.ComputeQueue, 1, in submitInfo, default);
        ctx.Vk.QueueWaitIdle(ctx.ComputeQueue);
        ctx.Vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, in cmdBuffer);

        // Read back diff and sum
        var diffData = diffTexture.GetData<float>();
        double totalDiff = 0;
        foreach (var t in diffData)
        {
            totalDiff += t;
        }

        var meanDiff = (float)(totalDiff / (width * height));
        var noiseSd = meanDiff * mosaicPatternWidth * mosaicPatternWidth;

        Console.WriteLine($"[ExposurePipeline] GPU Noise Estimation: totalDiff={totalDiff:F2}, noiseSd={noiseSd:F2}");
        return Math.Max(noiseSd, 1.0f);
    }
}
