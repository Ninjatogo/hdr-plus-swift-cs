using BurstPhoto.Core.Models;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering.Pipelines;

/// <summary>
/// Handles image alignment operations including pyramid building, alignment search, and warping.
/// Extracted from VulkanComputePipeline for better code organization.
/// </summary>
public unsafe class AlignmentPipeline
{
    private readonly VulkanContext _ctx;
    private readonly VulkanDescriptorManager _descriptors;
    private readonly VulkanKernelManager _kernelManager;

    // Cached kernels and layout
    private DescriptorSetLayout _alignLayout;
    private ComputeKernel? _kernelAvgPool;
    private ComputeKernel? _kernelAvgPoolNormalization;
    private ComputeKernel? _kernelTileDiff;
    private ComputeKernel? _kernelTileDiff25;
    private ComputeKernel? _kernelTileDiffExposure25;
    private ComputeKernel? _kernelFindBest;
    private ComputeKernel? _kernelWarp;
    private ComputeKernel? _kernelUpsampleAlignment;
    private ComputeKernel? _kernelCorrectUpsamplingError;

    public AlignmentPipeline(VulkanContext ctx, VulkanDescriptorManager descriptors, VulkanKernelManager kernelManager)
    {
        _ctx = ctx;
        _descriptors = descriptors;
        _kernelManager = kernelManager;
    }

    private void EnsureKernels()
    {
        if (_kernelAvgPool is not null) return;

        _alignLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.AlignLayout);
        _kernelAvgPool = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AvgPool, _alignLayout);
        _kernelAvgPoolNormalization = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AvgPoolNormalization, _alignLayout);
        _kernelTileDiff = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.TileDiff, _alignLayout);
        _kernelTileDiff25 = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.TileDiff25, _alignLayout);
        _kernelTileDiffExposure25 = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.TileDiffExposure25, _alignLayout);
        _kernelFindBest = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.FindBest, _alignLayout);
        _kernelWarp = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.Warp, _alignLayout);
        _kernelUpsampleAlignment = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.UpsampleAlignment, _alignLayout);
        _kernelCorrectUpsamplingError = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.CorrectUpsamplingError, _alignLayout);
    }

    /// <summary>
    /// Builds a downsampled pyramid level using average pooling.
    /// </summary>
    /// <param name="input">Source texture</param>
    /// <param name="output">Destination texture (should be half the size)</param>
    /// <param name="scale">Downscale factor (typically 2)</param>
    /// <param name="rawInfo">Raw image metadata for color factor normalization</param>
    /// <param name="normalize">If true, applies color factor normalization (for level 0)</param>
    public void ExecuteAvgPool(VulkanImage input, VulkanImage output, int scale, RawImage rawInfo, bool normalize = false)
    {
        EnsureKernels();

        // Compute color factors and black level for normalization (Swift: build_pyramid level 0)
        float factorRed = 1.0f, factorGreen = 1.0f, factorBlue = 1.0f;
        var blackLevelMean = 0.0f;
        if (normalize && rawInfo.ColorFactors is not null && rawInfo.ColorFactors.Length >= 3)
        {
            if (rawInfo.ColorFactors.Length >= 4)
            {
                factorRed = rawInfo.ColorFactors[0];
                factorGreen = (rawInfo.ColorFactors[1] + rawInfo.ColorFactors[2]) / 2.0f;
                factorBlue = rawInfo.ColorFactors[3];
            }
            else
            {
                factorRed = rawInfo.ColorFactors[0];
                factorGreen = rawInfo.ColorFactors[1];
                factorBlue = rawInfo.ColorFactors[2];
            }
        }

        var alignParams = new AlignParams
        {
            Scale = scale,
            BlackLevel = blackLevelMean,
            FactorRed = factorRed, FactorGreen = factorGreen, FactorBlue = factorBlue,
            DownscaleFactor = 0, TileSize = 0, SearchDist = 0, WeightSSD = 0,
            HalfTileSize = 0, NumTilesX = 0, NumTilesY = 0, UniformExposure = 0
        };

        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([alignParams]);

        using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyTex.SetData(new float[] { 0 });

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

        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);

        var set = _descriptors.Allocate(_alignLayout);

        _descriptors.UpdateBuffer(set, ShaderBindings.Alignment.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.InTexture, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.CompTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.AlignmentVectors, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.Output, output.View, ImageLayout.General, DescriptorType.StorageImage);

        var kernel = normalize ? _kernelAvgPoolNormalization! : _kernelAvgPool!;
        kernel.BindPipeline(cmdBuffer);

        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

        kernel.Dispatch(cmdBuffer, output.Width, output.Height, 1);

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
    /// Performs multi-level pyramid alignment search.
    /// </summary>
    /// <param name="refPyramid">Reference image pyramid (finest to coarsest)</param>
    /// <param name="compPyramid">Comparison image pyramid</param>
    /// <param name="alignmentOut">Output alignment vectors texture</param>
    /// <param name="baseTileInfo">Tile configuration at finest level</param>
    /// <param name="scale">Scale factor</param>
    /// <param name="uniformExposure">Whether images have uniform exposure</param>
    public void ExecuteAlignmentSearch(List<VulkanImage> refPyramid, List<VulkanImage> compPyramid, VulkanImage alignmentOut, TileInfo baseTileInfo, int scale, bool uniformExposure = true)
    {
        EnsureKernels();

        var numLevels = Math.Min(refPyramid.Count, compPyramid.Count);

        Console.WriteLine($"[Align] ExecuteAlignmentSearch: Levels={numLevels}, BaseTileSize={baseTileInfo.TileSize}");

        // Calculate tile sizes for each level
        var tileSizes = new int[numLevels];
        tileSizes[0] = baseTileInfo.TileSize;
        for (var i = 1; i < numLevels; i++)
        {
            tileSizes[i] = Math.Max(tileSizes[i - 1] / 2, 8);
        }

        VulkanImage? prevAlignment = null;

        // Loop from coarsest to finest
        for (var level = numLevels - 1; level >= 0; level--)
        {
            var refLayer = refPyramid[level];
            var compLayer = compPyramid[level];
            var tileSize = tileSizes[level];

            // Calculate tile grid dimensions for this level
            var nTilesX = (int)refLayer.Width / (tileSize / 2) - 1;
            var nTilesY = (int)refLayer.Height / (tileSize / 2) - 1;

            if (nTilesX < 1) nTilesX = 1;
            if (nTilesY < 1) nTilesY = 1;

            Console.WriteLine($"[Align] Level {level}: {refLayer.Width}x{refLayer.Height}, TileSize={tileSize}, Grid={nTilesX}x{nTilesY}");

            VulkanImage currentAlignment;
            var isLastLevel = (level == 0);

            currentAlignment = isLastLevel ? alignmentOut :
                new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

            var allocInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _ctx.CommandPool, CommandBufferCount = 1 };
            _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);
            var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

            var levelDisposables = new List<IDisposable>();

            // 1. Prepare "Previous Alignment" for this level
            VulkanImage prevAlignmentForStep;

            if (prevAlignment is null)
            {
                // Coarsest Level: Create Zeros
                var zeroAlign = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                levelDisposables.Add(zeroAlign);

                var totalTiles = nTilesX * nTilesY;
                var zeros = new short[totalTiles * 4];
                zeroAlign.SetData(zeros);
                zeroAlign.TransitionLayout(ImageLayout.General, cmdBuffer);

                prevAlignmentForStep = zeroAlign;
            }
            else
            {
                // Upsample from previous coarser level
                var upsampled = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                levelDisposables.Add(upsampled);

                prevAlignment.TransitionLayout(ImageLayout.General, cmdBuffer);
                upsampled.TransitionLayout(ImageLayout.General, cmdBuffer);

                var setUp = _descriptors.Allocate(_alignLayout);
                var dummyParams = new AlignParams();
                using var pBuff = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
                pBuff.SetData([dummyParams]);

                using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
                dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);

                _descriptors.UpdateBuffer(setUp, 0, pBuff.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
                _descriptors.UpdateImage(setUp, 1, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(setUp, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(setUp, 3, prevAlignment.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(setUp, 10, upsampled.View, ImageLayout.General, DescriptorType.StorageImage);

                _kernelUpsampleAlignment!.BindPipeline(cmdBuffer);
                _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelUpsampleAlignment.PipelineLayout, 0, 1, &setUp, 0, null);

                _kernelUpsampleAlignment.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);

                var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
                _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

                prevAlignmentForStep = upsampled;
            }

            // 2. Correct Upsampling Error
            var corrected = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
            levelDisposables.Add(corrected);
            corrected.TransitionLayout(ImageLayout.General, cmdBuffer);

            var alignParams = new AlignParams
            {
                TileSize = tileSize,
                DownscaleFactor = 2,
                NumTilesX = nTilesX,
                NumTilesY = nTilesY,
                UniformExposure = uniformExposure ? 1 : 0,
            };

            using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
            paramBuffer.SetData([alignParams]);

            refLayer.TransitionLayout(ImageLayout.General, cmdBuffer);
            compLayer.TransitionLayout(ImageLayout.General, cmdBuffer);

            var setCorrect = _descriptors.Allocate(_alignLayout);
            _descriptors.UpdateBuffer(setCorrect, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setCorrect, 1, refLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setCorrect, 2, compLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setCorrect, 3, prevAlignmentForStep.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setCorrect, 10, corrected.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelCorrectUpsamplingError!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelCorrectUpsamplingError.PipelineLayout, 0, 1, &setCorrect, 0, null);

            _kernelCorrectUpsamplingError.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);

            var barrier2 = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier2, 0, null, 0, null);

            // 3. Compute Tile Differences
            var nPos2D = 25;
            var tileDiff = new VulkanImage(_ctx, (uint)nPos2D, (uint)nTilesX, (uint)nTilesY, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit, ImageViewType.Type3D);
            levelDisposables.Add(tileDiff);
            tileDiff.TransitionLayout(ImageLayout.General, cmdBuffer);

            alignParams.SearchDist = 2;
            alignParams.WeightSSD = (level == 0) ? 0 : 1;
            paramBuffer.SetData([alignParams]);

            var setDiff = _descriptors.Allocate(_alignLayout);
            _descriptors.UpdateBuffer(setDiff, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setDiff, 1, refLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setDiff, 2, compLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setDiff, 3, corrected.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setDiff, 10, tileDiff.View, ImageLayout.General, DescriptorType.StorageImage);

            var kernelDiff = uniformExposure ? _kernelTileDiff25! : _kernelTileDiffExposure25!;
            kernelDiff.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernelDiff.PipelineLayout, 0, 1, &setDiff, 0, null);

            kernelDiff.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);

            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier2, 0, null, 0, null);

            // 4. Find Best Alignment
            if (isLastLevel)
            {
                alignmentOut.TransitionLayout(ImageLayout.General, cmdBuffer);
            }
            else
            {
                currentAlignment.TransitionLayout(ImageLayout.General, cmdBuffer);
            }

            var setFind = _descriptors.Allocate(_alignLayout);
            _descriptors.UpdateBuffer(setFind, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setFind, 1, tileDiff.View, ImageLayout.General, DescriptorType.SampledImage);

            using var dummyComp2 = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
            dummyComp2.TransitionLayout(ImageLayout.General, cmdBuffer);
            _descriptors.UpdateImage(setFind, 2, dummyComp2.View, ImageLayout.General, DescriptorType.SampledImage);

            _descriptors.UpdateImage(setFind, 3, corrected.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setFind, 10, currentAlignment.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelFindBest!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelFindBest.PipelineLayout, 0, 1, &setFind, 0, null);

            _kernelFindBest.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);

            _ctx.Vk.EndCommandBuffer(cmdBuffer);

            var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmdBuffer };
            _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
            _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);

            _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);

            foreach (var d in levelDisposables)
            {
                d.Dispose();
            }

            if (prevAlignment is not null && prevAlignment != alignmentOut)
            {
                prevAlignment.Dispose();
            }
            prevAlignment = currentAlignment;
        }
    }

    /// <summary>
    /// Warps the input texture using alignment vectors.
    /// </summary>
    /// <param name="altImage">Input texture to warp</param>
    /// <param name="output">Output warped texture</param>
    /// <param name="alignment">Alignment vectors texture</param>
    /// <param name="tileInfo">Tile configuration</param>
    /// <param name="padLeft">Left padding offset for coordinate clamping</param>
    /// <param name="padTop">Top padding offset for coordinate clamping</param>
    public void ExecuteWarp(VulkanImage altImage, VulkanImage output, VulkanImage alignment, TileInfo tileInfo, int padLeft = 0, int padTop = 0)
    {
        Console.WriteLine($"[WARP] ExecuteWarp: {altImage.Width}x{altImage.Height} -> {output.Width}x{output.Height}");
        Console.WriteLine($"[WARP] TileInfo: TileSize={tileInfo.TileSize}, NTilesX={tileInfo.NTilesX}, NTilesY={tileInfo.NTilesY}");
        Console.WriteLine($"[WARP] Padding: padLeft={padLeft}, padTop={padTop}");

        EnsureKernels();

        // For Bayer images (mosaic_pattern_width=2), DownscaleFactor = 2
        var downscaleFactor = 2;
        var halfTileSizeForWarp = (downscaleFactor == 2 ? 1 : downscaleFactor) * tileInfo.TileSize;

        var alignParams = new AlignParams
        {
            Scale = 1,
            BlackLevel = 0.0f,
            FactorRed = 1.0f, FactorGreen = 1.0f, FactorBlue = 1.0f,
            DownscaleFactor = downscaleFactor,
            TileSize = tileInfo.TileSize,
            SearchDist = 0, WeightSSD = 0,
            HalfTileSize = halfTileSizeForWarp,
            NumTilesX = tileInfo.NTilesX,
            NumTilesY = tileInfo.NTilesY,
            UniformExposure = 0,
            PadLeft = padLeft,
            PadTop = padTop,
            ImageWidth = (int)altImage.Width,
            ImageHeight = (int)altImage.Height
        };

        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([alignParams]);

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _ctx.CommandPool,
            CommandBufferCount = 1
        };
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);

        altImage.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer);
        alignment.TransitionLayout(ImageLayout.General, cmdBuffer);

        var set = _descriptors.Allocate(_alignLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.Alignment.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.InTexture, altImage.View, ImageLayout.General, DescriptorType.SampledImage);

        using var dummyComp = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyComp.TransitionLayout(ImageLayout.General, cmdBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.CompTexture, dummyComp.View, ImageLayout.General, DescriptorType.SampledImage);

        _descriptors.UpdateImage(set, ShaderBindings.Alignment.AlignmentVectors, alignment.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.Output, output.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelWarp!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelWarp.PipelineLayout, 0, 1, &set, 0, null);

        _kernelWarp.Dispatch(cmdBuffer, output.Width, output.Height, 1);

        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit,
            0, 1, &barrier, 0, null, 0, null);

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
}
