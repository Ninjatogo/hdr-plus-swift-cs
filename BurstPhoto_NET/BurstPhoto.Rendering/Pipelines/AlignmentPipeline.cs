using BurstPhoto.Core.Models;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering.Pipelines;

/// <summary>
/// Handles image alignment operations including pyramid building, alignment search, and warping.
/// </summary>
/// <remarks>
/// This pipeline implements a coarse-to-fine alignment strategy:
/// <list type="number">
///   <item><description>Build image pyramids by downsampling with average pooling</description></item>
///   <item><description>Search for best alignment at coarsest level</description></item>
///   <item><description>Refine alignment at finer levels using upsampled coarse results</description></item>
///   <item><description>Apply final alignment vectors to warp comparison frame</description></item>
/// </list>
/// </remarks>
public unsafe class AlignmentPipeline
{
    #region Constants

    /// <summary>
    /// Number of search positions evaluated in each dimension (5x5 = 25 total).
    /// This gives a search range of +/- 2 pixels from the initial estimate.
    /// </summary>
    private const int SearchPositionsPerDimension = 5;

    /// <summary>
    /// Total number of search positions (5x5 grid).
    /// </summary>
    private const int TotalSearchPositions = SearchPositionsPerDimension * SearchPositionsPerDimension;

    /// <summary>
    /// Minimum tile size at the coarsest pyramid level.
    /// </summary>
    private const int MinimumTileSize = 8;

    #endregion

    #region Private Fields

    private readonly VulkanContext _ctx;
    private readonly VulkanDescriptorManager _descriptors;
    private readonly VulkanKernelManager _kernelManager;

    // Cached kernels and layout for alignment operations
    private DescriptorSetLayout _alignmentDescriptorLayout;
    private ComputeKernel? _kernelAveragePool;
    private ComputeKernel? _kernelAveragePoolWithNormalization;
    private ComputeKernel? _kernelComputeTileDifference;
    private ComputeKernel? _kernelComputeTileDifference25Positions;
    private ComputeKernel? _kernelComputeTileDifferenceExposure25Positions;
    private ComputeKernel? _kernelFindBestAlignment;
    private ComputeKernel? _kernelWarpImage;
    private ComputeKernel? _kernelUpsampleAlignmentVectors;
    private ComputeKernel? _kernelCorrectUpsamplingError;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="AlignmentPipeline"/> class.
    /// </summary>
    /// <param name="ctx">Vulkan context providing device and command pool access.</param>
    /// <param name="descriptors">Descriptor manager for allocating descriptor sets.</param>
    /// <param name="kernelManager">Kernel manager for creating and caching compute kernels.</param>
    public AlignmentPipeline(VulkanContext ctx, VulkanDescriptorManager descriptors, VulkanKernelManager kernelManager)
    {
        _ctx = ctx;
        _descriptors = descriptors;
        _kernelManager = kernelManager;
    }

    #endregion

    #region Kernel Initialization

    /// <summary>
    /// Ensures all compute kernels are initialized (lazy initialization).
    /// </summary>
    private void EnsureKernelsInitialized()
    {
        if (_kernelAveragePool is not null) return;

        _alignmentDescriptorLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.AlignLayout);
        _kernelAveragePool = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AvgPool, _alignmentDescriptorLayout);
        _kernelAveragePoolWithNormalization = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AvgPoolNormalization, _alignmentDescriptorLayout);
        _kernelComputeTileDifference = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.TileDiff, _alignmentDescriptorLayout);
        _kernelComputeTileDifference25Positions = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.TileDiff25, _alignmentDescriptorLayout);
        _kernelComputeTileDifferenceExposure25Positions = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.TileDiffExposure25, _alignmentDescriptorLayout);
        _kernelFindBestAlignment = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.FindBest, _alignmentDescriptorLayout);
        _kernelWarpImage = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.Warp, _alignmentDescriptorLayout);
        _kernelUpsampleAlignmentVectors = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.UpsampleAlignment, _alignmentDescriptorLayout);
        _kernelCorrectUpsamplingError = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.CorrectUpsamplingError, _alignmentDescriptorLayout);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Builds a downsampled pyramid level using average pooling.
    /// </summary>
    /// <param name="input">Source texture</param>
    /// <param name="output">Destination texture (should be half the size)</param>
    /// <param name="scale">Downscale factor (typically 2)</param>
    /// <param name="rawInfo">Raw image metadata for color factor normalization</param>
    /// <param name="normalize">If true, applies color factor normalization (used for level 0 only)</param>
    public void ExecuteAvgPool(VulkanImage input, VulkanImage output, int scale, RawImage rawInfo, bool normalize = false)
    {
        EnsureKernelsInitialized();

        // Compute color factors and black level for normalization (Swift: build_pyramid level 0)
        float factorRed = 1.0f, factorGreen = 1.0f, factorBlue = 1.0f;
        var blackLevelMean = 0.0f;
        if (normalize && rawInfo.ColorChannelMultipliers is not null && rawInfo.ColorChannelMultipliers.Length >= 3)
        {
            if (rawInfo.ColorChannelMultipliers.Length >= 4)
            {
                factorRed = rawInfo.ColorChannelMultipliers[0];
                factorGreen = (rawInfo.ColorChannelMultipliers[1] + rawInfo.ColorChannelMultipliers[2]) / 2.0f;
                factorBlue = rawInfo.ColorChannelMultipliers[3];
            }
            else
            {
                factorRed = rawInfo.ColorChannelMultipliers[0];
                factorGreen = rawInfo.ColorChannelMultipliers[1];
                factorBlue = rawInfo.ColorChannelMultipliers[2];
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

        // Create dummy textures for unused shader bindings
        using var placeholderTexture = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        placeholderTexture.SetData(new float[] { 0 });

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
        placeholderTexture.TransitionLayout(ImageLayout.General, cmdBuffer);

        var descriptorSet = _descriptors.Allocate(_alignmentDescriptorLayout);

        _descriptors.UpdateBuffer(descriptorSet, ShaderBindings.Alignment.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(descriptorSet, ShaderBindings.Alignment.InTexture, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(descriptorSet, ShaderBindings.Alignment.CompTexture, placeholderTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(descriptorSet, ShaderBindings.Alignment.AlignmentVectors, placeholderTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(descriptorSet, ShaderBindings.Alignment.Output, output.View, ImageLayout.General, DescriptorType.StorageImage);

        var averagePoolKernel = normalize ? _kernelAveragePoolWithNormalization! : _kernelAveragePool!;
        averagePoolKernel.BindPipeline(cmdBuffer);

        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, averagePoolKernel.PipelineLayout, 0, 1, &descriptorSet, 0, null);

        averagePoolKernel.Dispatch(cmdBuffer, output.Width, output.Height, 1);

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
    /// <param name="uniformExposure">Whether images have uniform exposure (affects difference calculation)</param>
    public void ExecuteAlignmentSearch(List<VulkanImage> referencePyramid, List<VulkanImage> comparisonPyramid, VulkanImage alignmentOutput, TileInfo baseTileInfo, int scale, bool uniformExposure = true)
    {
        EnsureKernelsInitialized();

        var pyramidLevelCount = Math.Min(referencePyramid.Count, comparisonPyramid.Count);

        Console.WriteLine($"[Align] ExecuteAlignmentSearch: Levels={pyramidLevelCount}, BaseTileSize={baseTileInfo.TileSize}");

        // Calculate tile sizes for each pyramid level (halving at each level, minimum 8)
        var tileSizesPerLevel = new int[pyramidLevelCount];
        tileSizesPerLevel[0] = baseTileInfo.TileSize;
        for (var levelIndex = 1; levelIndex < pyramidLevelCount; levelIndex++)
        {
            tileSizesPerLevel[levelIndex] = Math.Max(tileSizesPerLevel[levelIndex - 1] / 2, MinimumTileSize);
        }

        VulkanImage? previousLevelAlignment = null;

        // Process from coarsest to finest level (coarse-to-fine refinement)
        for (var currentLevel = pyramidLevelCount - 1; currentLevel >= 0; currentLevel--)
        {
            var referenceLayerImage = referencePyramid[currentLevel];
            var comparisonLayerImage = comparisonPyramid[currentLevel];
            var tileSizeForLevel = tileSizesPerLevel[currentLevel];

            // Calculate tile grid dimensions for this level
            // Tiles overlap by 50% (stride = tileSize/2)
            var tileCountX = (int)referenceLayerImage.Width / (tileSizeForLevel / 2) - 1;
            var tileCountY = (int)referenceLayerImage.Height / (tileSizeForLevel / 2) - 1;

            if (tileCountX < 1) tileCountX = 1;
            if (tileCountY < 1) tileCountY = 1;

            Console.WriteLine($"[Align] Level {currentLevel}: {referenceLayerImage.Width}x{referenceLayerImage.Height}, TileSize={tileSizeForLevel}, Grid={tileCountX}x{tileCountY}");

            VulkanImage currentLevelAlignment;
            var isFinestLevel = (currentLevel == 0);

            // Use final output for finest level, otherwise create temporary storage
            currentLevelAlignment = isFinestLevel ? alignmentOutput :
                new VulkanImage(_ctx, (uint)tileCountX, (uint)tileCountY, Format.R16G16B16A16Sint, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

            var commandBufferAllocInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _ctx.CommandPool, CommandBufferCount = 1 };
            _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in commandBufferAllocInfo, out var cmdBuffer);
            var commandBeginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _ctx.Vk.BeginCommandBuffer(cmdBuffer, in commandBeginInfo);

            var levelResources = new List<IDisposable>();

            // Step 1: Prepare initial alignment estimate from previous (coarser) level
            VulkanImage initialAlignmentEstimate;

            if (previousLevelAlignment is null)
            {
                // At coarsest level: start with zero alignment (no motion estimate)
                var zeroAlignmentTexture = new VulkanImage(_ctx, (uint)tileCountX, (uint)tileCountY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                levelResources.Add(zeroAlignmentTexture);

                var totalTileElements = tileCountX * tileCountY * 4; // 4 components per tile (RGBA16)
                var zeroAlignmentData = new short[totalTileElements];
                zeroAlignmentTexture.SetData(zeroAlignmentData);
                zeroAlignmentTexture.TransitionLayout(ImageLayout.General, cmdBuffer);

                initialAlignmentEstimate = zeroAlignmentTexture;
            }
            else
            {
                // Upsample alignment from previous (coarser) level to current grid
                var upsampledAlignment = new VulkanImage(_ctx, (uint)tileCountX, (uint)tileCountY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                levelResources.Add(upsampledAlignment);

                previousLevelAlignment.TransitionLayout(ImageLayout.General, cmdBuffer);
                upsampledAlignment.TransitionLayout(ImageLayout.General, cmdBuffer);

                var upsampleDescriptorSet = _descriptors.Allocate(_alignmentDescriptorLayout);
                var emptyParams = new AlignParams();
                using var upsampleParamBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
                upsampleParamBuffer.SetData([emptyParams]);

                using var placeholderTexture = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
                placeholderTexture.TransitionLayout(ImageLayout.General, cmdBuffer);

                _descriptors.UpdateBuffer(upsampleDescriptorSet, 0, upsampleParamBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
                _descriptors.UpdateImage(upsampleDescriptorSet, 1, placeholderTexture.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(upsampleDescriptorSet, 2, placeholderTexture.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(upsampleDescriptorSet, 3, previousLevelAlignment.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(upsampleDescriptorSet, 10, upsampledAlignment.View, ImageLayout.General, DescriptorType.StorageImage);

                _kernelUpsampleAlignmentVectors!.BindPipeline(cmdBuffer);
                _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelUpsampleAlignmentVectors.PipelineLayout, 0, 1, &upsampleDescriptorSet, 0, null);

                _kernelUpsampleAlignmentVectors.Dispatch(cmdBuffer, (uint)tileCountX, (uint)tileCountY, 1);

                var upsampleBarrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
                _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &upsampleBarrier, 0, null, 0, null);

                initialAlignmentEstimate = upsampledAlignment;
            }

            // Step 2: Correct upsampling error by refining initial estimate
            var correctedAlignment = new VulkanImage(_ctx, (uint)tileCountX, (uint)tileCountY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
            levelResources.Add(correctedAlignment);
            correctedAlignment.TransitionLayout(ImageLayout.General, cmdBuffer);

            var alignmentParams = new AlignParams
            {
                TileSize = tileSizeForLevel,
                DownscaleFactor = 2,
                NumTilesX = tileCountX,
                NumTilesY = tileCountY,
                UniformExposure = uniformExposure ? 1 : 0,
            };

            using var alignmentParamBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
            alignmentParamBuffer.SetData([alignmentParams]);

            referenceLayerImage.TransitionLayout(ImageLayout.General, cmdBuffer);
            comparisonLayerImage.TransitionLayout(ImageLayout.General, cmdBuffer);

            var correctionDescriptorSet = _descriptors.Allocate(_alignmentDescriptorLayout);
            _descriptors.UpdateBuffer(correctionDescriptorSet, 0, alignmentParamBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(correctionDescriptorSet, 1, referenceLayerImage.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(correctionDescriptorSet, 2, comparisonLayerImage.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(correctionDescriptorSet, 3, initialAlignmentEstimate.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(correctionDescriptorSet, 10, correctedAlignment.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelCorrectUpsamplingError!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelCorrectUpsamplingError.PipelineLayout, 0, 1, &correctionDescriptorSet, 0, null);

            _kernelCorrectUpsamplingError.Dispatch(cmdBuffer, (uint)tileCountX, (uint)tileCountY, 1);

            var correctionBarrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &correctionBarrier, 0, null, 0, null);

            // Step 3: Compute tile differences for all search positions
            // Creates a 3D texture: [searchPositions x tileCountX x tileCountY]
            var tileDifferenceVolume = new VulkanImage(_ctx, (uint)TotalSearchPositions, (uint)tileCountX, (uint)tileCountY, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit, ImageViewType.Type3D);
            levelResources.Add(tileDifferenceVolume);
            tileDifferenceVolume.TransitionLayout(ImageLayout.General, cmdBuffer);

            alignmentParams.SearchDist = 2; // Search +/- 2 pixels in each direction
            alignmentParams.WeightSSD = (currentLevel == 0) ? 0 : 1; // Use SSD weighting for coarser levels
            alignmentParamBuffer.SetData([alignmentParams]);

            var differenceDescriptorSet = _descriptors.Allocate(_alignmentDescriptorLayout);
            _descriptors.UpdateBuffer(differenceDescriptorSet, 0, alignmentParamBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(differenceDescriptorSet, 1, referenceLayerImage.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(differenceDescriptorSet, 2, comparisonLayerImage.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(differenceDescriptorSet, 3, correctedAlignment.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(differenceDescriptorSet, 10, tileDifferenceVolume.View, ImageLayout.General, DescriptorType.StorageImage);

            var tileDifferenceKernel = uniformExposure ? _kernelComputeTileDifference25Positions! : _kernelComputeTileDifferenceExposure25Positions!;
            tileDifferenceKernel.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, tileDifferenceKernel.PipelineLayout, 0, 1, &differenceDescriptorSet, 0, null);

            tileDifferenceKernel.Dispatch(cmdBuffer, (uint)tileCountX, (uint)tileCountY, 1);

            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &correctionBarrier, 0, null, 0, null);

            // Step 4: Find best alignment by selecting minimum difference position
            if (isFinestLevel)
            {
                alignmentOutput.TransitionLayout(ImageLayout.General, cmdBuffer);
            }
            else
            {
                currentLevelAlignment.TransitionLayout(ImageLayout.General, cmdBuffer);
            }

            var findBestDescriptorSet = _descriptors.Allocate(_alignmentDescriptorLayout);
            _descriptors.UpdateBuffer(findBestDescriptorSet, 0, alignmentParamBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(findBestDescriptorSet, 1, tileDifferenceVolume.View, ImageLayout.General, DescriptorType.SampledImage);

            using var unusedPlaceholder = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
            unusedPlaceholder.TransitionLayout(ImageLayout.General, cmdBuffer);
            _descriptors.UpdateImage(findBestDescriptorSet, 2, unusedPlaceholder.View, ImageLayout.General, DescriptorType.SampledImage);

            _descriptors.UpdateImage(findBestDescriptorSet, 3, correctedAlignment.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(findBestDescriptorSet, 10, currentLevelAlignment.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelFindBestAlignment!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelFindBestAlignment.PipelineLayout, 0, 1, &findBestDescriptorSet, 0, null);

            _kernelFindBestAlignment.Dispatch(cmdBuffer, (uint)tileCountX, (uint)tileCountY, 1);

            _ctx.Vk.EndCommandBuffer(cmdBuffer);

            var queueSubmitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmdBuffer };
            _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in queueSubmitInfo, default);
            _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);

            _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);

            // Cleanup level-specific resources
            foreach (var resource in levelResources)
            {
                resource.Dispose();
            }

            // Dispose previous level alignment (unless it's the final output)
            if (previousLevelAlignment is not null && previousLevelAlignment != alignmentOutput)
            {
                previousLevelAlignment.Dispose();
            }
            previousLevelAlignment = currentLevelAlignment;
        }
    }

    #endregion

    #region Warp Operations

    /// <summary>
    /// Warps the input texture using alignment vectors.
    /// </summary>
    /// <param name="altImage">Input texture to warp</param>
    /// <param name="output">Output warped texture</param>
    /// <param name="alignment">Alignment vectors texture</param>
    /// <param name="tileInfo">Tile configuration</param>
    /// <param name="padLeft">Left padding offset for coordinate clamping</param>
    /// <param name="paddingTop">Top padding offset for coordinate clamping</param>
    public void ExecuteWarp(VulkanImage sourceImage, VulkanImage outputImage, VulkanImage alignmentVectors, TileInfo tileInfo, int paddingLeft = 0, int paddingTop = 0)
    {
        Console.WriteLine($"[WARP] ExecuteWarp: {sourceImage.Width}x{sourceImage.Height} -> {outputImage.Width}x{outputImage.Height}");
        Console.WriteLine($"[WARP] TileInfo: TileSize={tileInfo.TileSize}, TileCountX={tileInfo.TileCountX}, TileCountY={tileInfo.TileCountY}");
        Console.WriteLine($"[WARP] Padding: paddingLeft={paddingLeft}, paddingTop={paddingTop}");

        EnsureKernelsInitialized();

        // For Bayer images (mosaic_pattern_width=2), use downscale factor of 2
        const int bayerDownscaleFactor = 2;
        var halfTileSizeForWarp = (bayerDownscaleFactor == 2 ? 1 : bayerDownscaleFactor) * tileInfo.TileSize;

        var warpParams = new AlignParams
        {
            Scale = 1,
            BlackLevel = 0.0f,
            FactorRed = 1.0f, FactorGreen = 1.0f, FactorBlue = 1.0f,
            DownscaleFactor = bayerDownscaleFactor,
            TileSize = tileInfo.TileSize,
            SearchDist = 0, WeightSSD = 0,
            HalfTileSize = halfTileSizeForWarp,
            NumTilesX = tileInfo.TileCountX,
            NumTilesY = tileInfo.TileCountY,
            UniformExposure = 0,
            PadLeft = paddingLeft,
            PadTop = paddingTop,
            ImageWidth = (int)sourceImage.Width,
            ImageHeight = (int)sourceImage.Height
        };

        using var warpParamBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        warpParamBuffer.SetData([warpParams]);

        var commandBufferAllocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _ctx.CommandPool,
            CommandBufferCount = 1
        };
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in commandBufferAllocInfo, out var cmdBuffer);

        var commandBeginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in commandBeginInfo);

        sourceImage.TransitionLayout(ImageLayout.General, cmdBuffer);
        outputImage.TransitionLayout(ImageLayout.General, cmdBuffer);
        alignmentVectors.TransitionLayout(ImageLayout.General, cmdBuffer);

        var warpDescriptorSet = _descriptors.Allocate(_alignmentDescriptorLayout);
        _descriptors.UpdateBuffer(warpDescriptorSet, ShaderBindings.Alignment.Params, warpParamBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(warpDescriptorSet, ShaderBindings.Alignment.InTexture, sourceImage.View, ImageLayout.General, DescriptorType.SampledImage);

        using var placeholderComparison = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        placeholderComparison.TransitionLayout(ImageLayout.General, cmdBuffer);
        _descriptors.UpdateImage(warpDescriptorSet, ShaderBindings.Alignment.CompTexture, placeholderComparison.View, ImageLayout.General, DescriptorType.SampledImage);

        _descriptors.UpdateImage(warpDescriptorSet, ShaderBindings.Alignment.AlignmentVectors, alignmentVectors.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(warpDescriptorSet, ShaderBindings.Alignment.Output, outputImage.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelWarpImage!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelWarpImage.PipelineLayout, 0, 1, &warpDescriptorSet, 0, null);

        _kernelWarpImage.Dispatch(cmdBuffer, outputImage.Width, outputImage.Height, 1);

        var warpBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit,
            0, 1, &warpBarrier, 0, null, 0, null);

        _ctx.Vk.EndCommandBuffer(cmdBuffer);

        var queueSubmitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };
        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in queueSubmitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);

        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
    }

    #endregion
}
