using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using BurstPhoto.Rendering.Validation;

namespace BurstPhoto.Rendering.Pipelines;

/// <summary>
/// Handles frequency domain merging using FFT-based processing.
/// Includes forward/backward FFT, RMS calculation, mismatch normalization,
/// frequency domain merge, deconvolution, and artifact reduction.
/// </summary>
public unsafe class FrequencyMergePipeline
{
    private readonly VulkanContext _ctx;
    private readonly VulkanDescriptorManager _descriptors;
    private readonly VulkanKernelManager _kernelManager;

    // Frequency domain kernels
    private DescriptorSetLayout _frequencyLayout;
    private ComputeKernel? _kernelAbsDiff;
    private ComputeKernel? _kernelRms;
    private ComputeKernel? _kernelMismatch;
    private ComputeKernel? _kernelHighlightsNorm;
    private ComputeKernel? _kernelNormalizeMismatch;
    private ComputeKernel? _kernelForwardFft;
    private ComputeKernel? _kernelBackwardFft;
    private ComputeKernel? _kernelMergeFrequency;
    private ComputeKernel? _kernelDeconvoluteFrequency;
    private ComputeKernel? _kernelArtifactsTileBorder;

    // GPU reduction/accumulation kernels (eliminates CPU roundtrips)
    private ComputeKernel? _kernelReduceMeanColumns;
    private ComputeKernel? _kernelReduceMeanFinal;
    private ComputeKernel? _kernelAccumulateMismatchRgba;

    public FrequencyMergePipeline(VulkanContext ctx, VulkanDescriptorManager descriptors, VulkanKernelManager kernelManager)
    {
        _ctx = ctx;
        _descriptors = descriptors;
        _kernelManager = kernelManager;
    }

    private void EnsureKernels()
    {
        if (_kernelMergeFrequency is not null) return;

        // Check if the required Vulkan feature is supported
        if (!_ctx.SupportsStorageImageWriteWithoutFormat)
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════");
            Console.WriteLine("║ FREQUENCY DOMAIN PIPELINE UNAVAILABLE");
            Console.WriteLine("╠════════════════════════════════════════════════════════════════");
            Console.WriteLine("║ Cannot initialize HigherQuality algorithm:");
            Console.WriteLine("║ ");
            Console.WriteLine("║ Required Vulkan feature NOT supported:");
            Console.WriteLine("║   ShaderStorageImageWriteWithoutFormat = false");
            Console.WriteLine("║");
            Console.WriteLine("║ This feature is required for:");
            Console.WriteLine("║   - RWTexture2D<float4> writes in compute shaders");
            Console.WriteLine("║   - FFT-based frequency domain processing");
            Console.WriteLine("║");
            Console.WriteLine("║ Solution:");
            Console.WriteLine("║   Use --algorithm Fast instead of HigherQuality");
            Console.WriteLine("║");
            Console.WriteLine("║ Or try:");
            Console.WriteLine("║   1. Update GPU drivers");
            Console.WriteLine("║   2. Use --list-gpus and --gpu <index> to try another GPU");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════\n");

            throw new NotSupportedException(
                "HigherQuality algorithm requires ShaderStorageImageWriteWithoutFormat, which is not supported by this GPU. " +
                "Use --algorithm Fast instead, or try a different GPU with --gpu <index>.");
        }

        Console.WriteLine("[FrequencyMergePipeline] Initializing frequency domain shaders...");

        _frequencyLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.FrequencyLayout);

        _kernelAbsDiff = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AbsDiff, _frequencyLayout);
        _kernelRms = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.Rms, _frequencyLayout);
        _kernelMismatch = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.Mismatch, _frequencyLayout);
        _kernelHighlightsNorm = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.HighlightsNorm, _frequencyLayout);
        _kernelNormalizeMismatch = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.NormalizeMismatch, _frequencyLayout);
        _kernelArtifactsTileBorder = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ArtifactsTileBorder, _frequencyLayout);
        _kernelForwardFft = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ForwardFft, _frequencyLayout);
        _kernelBackwardFft = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.BackwardFft, _frequencyLayout);
        _kernelMergeFrequency = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.MergeFrequencyDomain, _frequencyLayout);
        _kernelDeconvoluteFrequency = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.DeconvoluteFrequency, _frequencyLayout);

        // GPU reduction/accumulation kernels
        _kernelReduceMeanColumns = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ReduceMeanColumns, _frequencyLayout);
        _kernelReduceMeanFinal = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ReduceMeanFinal, _frequencyLayout);
        _kernelAccumulateMismatchRgba = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.AccumulateMismatchRgba, _frequencyLayout);

        Console.WriteLine("[FrequencyMergePipeline] Frequency domain shaders compiled successfully!");
    }

    /// <summary>
    /// Executes the full frequency domain merge pipeline for one alternate frame.
    /// Computes RMS, mismatch, highlights, forward FFT, and accumulates into pixelAccumFT.
    ///
    /// OPTIMIZED: Uses batched command buffers with pipeline barriers instead of
    /// individual GPU syncs. Reduces sync points from ~11 to 3 (Phase1, Mean readback, Phase2).
    /// </summary>
    public void ExecuteMergeFrequency(
        VulkanImage refFt,
        VulkanImage refPyramid0,
        VulkanImage aligned,
        VulkanImage weightAccum,
        VulkanImage pixelAccumFt,
        float whiteLevel,
        float blackLevel,
        double noiseReduction,
        float noiseSd,
        float exposureDiff,
        int tileSize,
        int mosaicPatternWidth,
        int uniformExposure,
        VulkanImage? totalMismatchTexture = null,
        int totalImageCount = 1,
        double exposureCorrRatio = 1.0)
    {
        EnsureKernels();

        var width = (int)refPyramid0.Width;
        var height = (int)refPyramid0.Height;

        // CRITICAL FIX: Swift hardcodes tile_size_merge = 8 for FFT merging
        const int tileSizeMerge = 8;

        // Calculate tile grid dimensions
        var nTilesX = width / tileSizeMerge;
        var nTilesY = height / tileSizeMerge;

        // CRITICAL FIX: Use Swift's robustness formula
        var isUniformExposure = (uniformExposure == 1);
        var robustnessRev = 0.5 * ((isUniformExposure ? 26.5 : 28.5) - Math.Round(noiseReduction));
        var robustnessNorm = exposureCorrRatio * Math.Pow(2.0, -robustnessRev + 7.5);
        var readNoise = Math.Pow(Math.Pow(2.0, -robustnessRev + 10.0), 1.6);
        var maxMotionNorm = Math.Max(1.0, Math.Pow(1.3, 11.0 - robustnessRev));

        // exposureDiff is in centistops — must divide by 100 to get EV
        var exposureFactor = (float)Math.Pow(2.0, exposureDiff / 100.0);

        // Signal-dependent noise model: σ² = α·signal + β
        // ShotNoiseCoef (α) captures photon shot noise, which scales linearly with signal
        // For cameras, α is approximately proportional to analog gain (ISO setting)
        // If DNG NoiseProfile is available, use those values directly.
        // Otherwise, estimate from noiseSd: α ≈ noiseSd² / (whiteLevel * 0.18) for mid-gray
        // A reasonable default is based on the noise profile being dominated by shot noise
        // at mid-to-high signal levels.
        var shotNoiseCoef = noiseSd > 0
            ? (float)(noiseSd * noiseSd / Math.Max(whiteLevel * 0.18, 1.0))
            : 0.001f;  // Default for when noise info is unavailable

        var freqParams = new FrequencyParams
        {
            RobustnessNorm = (float)robustnessNorm,
            ReadNoise = (float)readNoise,
            MaxMotionNorm = (float)maxMotionNorm,
            TileSize = tileSizeMerge,
            UniformExposure = uniformExposure,
            NumTextures = 1,
            ExposureFactor = exposureFactor,
            WhiteLevel = whiteLevel,
            BlackLevelMean = blackLevel,
            MeanMismatch = 0.01f, // Initial placeholder
            ShotNoiseCoef = shotNoiseCoef,
            BlackLevel0 = 0, BlackLevel1 = 0, BlackLevel2 = 0, BlackLevel3 = 0
        };

        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([freqParams]);

        // CRITICAL FIX: RMS, Mismatch, Highlights textures are at TILE GRID size
        using var texDiff = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
        using var texRms = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
        using var texMismatch = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
        using var texHighlights = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

        // AlignedFT needs 2x width for complex storage
        var ftWidth = width * 2;
        using var texAlignedFt = new VulkanImage(_ctx, (uint)ftWidth, (uint)height, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

        // Create reduction buffer for GPU mean computation
        using var reductionBuffer = new VulkanBuffer(_ctx, (ulong)(nTilesX * sizeof(float)),
            BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        // ============================================================================
        // PHASE 1: All operations up to mean computation (batched into single cmd buffer)
        // This includes: layout transitions, AbsDiff, RMS, Mismatch, MeanReduction
        // ============================================================================
        var cmdPhase1 = _ctx.BeginSingleTimeCommands();

        // 0. Layout transitions for all temporary textures
        texDiff.TransitionLayout(ImageLayout.General, cmdPhase1);
        texRms.TransitionLayout(ImageLayout.General, cmdPhase1);
        texMismatch.TransitionLayout(ImageLayout.General, cmdPhase1);
        texHighlights.TransitionLayout(ImageLayout.General, cmdPhase1);
        texAlignedFt.TransitionLayout(ImageLayout.General, cmdPhase1);

        // Pre-allocate descriptor sets for phase 1
        var setAbsDiff = _descriptors.Allocate(_frequencyLayout);
        var setRms = _descriptors.Allocate(_frequencyLayout);
        var setMismatch = _descriptors.Allocate(_frequencyLayout);
        var setReduceCol = _descriptors.Allocate(_frequencyLayout);
        var setReduceFinal = _descriptors.Allocate(_frequencyLayout);

        // Update descriptor sets
        // AbsDiff
        _descriptors.UpdateBuffer(setAbsDiff, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setAbsDiff, ShaderBindings.FrequencyDomain.RefTexture, refPyramid0.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setAbsDiff, ShaderBindings.FrequencyDomain.AlignedTexture, aligned.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setAbsDiff, ShaderBindings.FrequencyDomain.OutputTexture, texDiff.View, ImageLayout.General, DescriptorType.StorageImage);

        // RMS
        _descriptors.UpdateBuffer(setRms, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setRms, ShaderBindings.FrequencyDomain.RefTexture, refPyramid0.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setRms, ShaderBindings.FrequencyDomain.OutputTexture, texRms.View, ImageLayout.General, DescriptorType.StorageImage);

        // Mismatch
        _descriptors.UpdateBuffer(setMismatch, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setMismatch, ShaderBindings.FrequencyDomain.RefTexture, texDiff.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setMismatch, ShaderBindings.FrequencyDomain.RmsTexture, texRms.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setMismatch, ShaderBindings.FrequencyDomain.OutputTexture, texMismatch.View, ImageLayout.General, DescriptorType.StorageImage);

        // Reduction passes
        _descriptors.UpdateBuffer(setReduceCol, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setReduceCol, ShaderBindings.FrequencyDomain.RefTexture, texMismatch.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateBuffer(setReduceCol, 11, reductionBuffer.Handle, (ulong)(nTilesX * sizeof(float)), DescriptorType.StorageBuffer);

        _descriptors.UpdateBuffer(setReduceFinal, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setReduceFinal, ShaderBindings.FrequencyDomain.RefTexture, texMismatch.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateBuffer(setReduceFinal, 11, reductionBuffer.Handle, (ulong)(nTilesX * sizeof(float)), DescriptorType.StorageBuffer);

        // 1. AbsDiff (full image size dispatch)
        _kernelAbsDiff!.BindPipeline(cmdPhase1);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase1, PipelineBindPoint.Compute, _kernelAbsDiff.PipelineLayout, 0, 1, &setAbsDiff, 0, null);
        _kernelAbsDiff.Dispatch(cmdPhase1, (uint)width, (uint)height, 1);

        // Barrier: AbsDiff output -> Mismatch input
        AddComputeBarrier(cmdPhase1);

        // 2. RMS (tile grid dispatch) - can run in parallel with AbsDiff since it reads refPyramid0
        _kernelRms!.BindPipeline(cmdPhase1);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase1, PipelineBindPoint.Compute, _kernelRms.PipelineLayout, 0, 1, &setRms, 0, null);
        _kernelRms.Dispatch(cmdPhase1, (uint)nTilesX, (uint)nTilesY, 1);

        // Barrier: RMS output -> Mismatch input (combined with AbsDiff barrier above)
        AddComputeBarrier(cmdPhase1);

        // 3. Mismatch (tile grid dispatch) - depends on texDiff and texRms
        _kernelMismatch!.BindPipeline(cmdPhase1);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase1, PipelineBindPoint.Compute, _kernelMismatch.PipelineLayout, 0, 1, &setMismatch, 0, null);
        _kernelMismatch.Dispatch(cmdPhase1, (uint)nTilesX, (uint)nTilesY, 1);

        // Barrier: Mismatch output -> Reduction input
        AddComputeBarrier(cmdPhase1);

        // 4. Mean Mismatch Reduction Pass 1: Reduce columns
        _kernelReduceMeanColumns!.BindPipeline(cmdPhase1);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase1, PipelineBindPoint.Compute, _kernelReduceMeanColumns.PipelineLayout, 0, 1, &setReduceCol, 0, null);
        _kernelReduceMeanColumns.Dispatch(cmdPhase1, (uint)((nTilesX + 255) / 256), 1, 1);

        // Barrier: Column reduction -> Final reduction
        AddComputeBarrier(cmdPhase1);

        // 5. Mean Mismatch Reduction Pass 2: Final reduction
        _kernelReduceMeanFinal!.BindPipeline(cmdPhase1);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase1, PipelineBindPoint.Compute, _kernelReduceMeanFinal.PipelineLayout, 0, 1, &setReduceFinal, 0, null);
        _kernelReduceMeanFinal.Dispatch(cmdPhase1, 1, 1, 1);

        // Submit phase 1 and wait - we need the mean value on CPU
        _ctx.EndSingleTimeCommands(cmdPhase1);

        // Read back single float (tiny transfer, not a full texture read)
        var meanResult = reductionBuffer.GetData<float>(1);
        var mean = meanResult[0];

        freqParams.MeanMismatch = mean * 2.0f;
        paramBuffer.SetData([freqParams]);

        // ============================================================================
        // PHASE 2: Normalize, Accumulate, Highlights, Forward FFT, Merge
        // All batched into a single command buffer
        // ============================================================================
        var cmdPhase2 = _ctx.BeginSingleTimeCommands();

        // Pre-allocate descriptor sets for phase 2
        var setNormalize = _descriptors.Allocate(_frequencyLayout);
        var setHighlights = _descriptors.Allocate(_frequencyLayout);
        var setForwardFft = _descriptors.Allocate(_frequencyLayout);
        var setMerge = _descriptors.Allocate(_frequencyLayout);

        // Update descriptor sets
        // NormalizeMismatch (in-place)
        _descriptors.UpdateBuffer(setNormalize, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setNormalize, ShaderBindings.FrequencyDomain.RefTexture, texMismatch.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setNormalize, ShaderBindings.FrequencyDomain.OutputTexture, texMismatch.View, ImageLayout.General, DescriptorType.StorageImage);

        // Highlights
        _descriptors.UpdateBuffer(setHighlights, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setHighlights, ShaderBindings.FrequencyDomain.RefTexture, aligned.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setHighlights, ShaderBindings.FrequencyDomain.OutputTexture, texHighlights.View, ImageLayout.General, DescriptorType.StorageImage);

        // Forward FFT
        _descriptors.UpdateBuffer(setForwardFft, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setForwardFft, ShaderBindings.FrequencyDomain.RefTexture, aligned.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setForwardFft, ShaderBindings.FrequencyDomain.OutputTexture, texAlignedFt.View, ImageLayout.General, DescriptorType.StorageImage);

        // MergeFrequency
        _descriptors.UpdateBuffer(setMerge, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setMerge, ShaderBindings.FrequencyDomain.RefTexture, refFt.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setMerge, ShaderBindings.FrequencyDomain.AlignedTexture, texAlignedFt.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setMerge, ShaderBindings.FrequencyDomain.RmsTexture, texRms.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setMerge, ShaderBindings.FrequencyDomain.MismatchTexture, texMismatch.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setMerge, ShaderBindings.FrequencyDomain.HighlightsTexture, texHighlights.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setMerge, ShaderBindings.FrequencyDomain.OutputTexture, pixelAccumFt.View, ImageLayout.General, DescriptorType.StorageImage);

        // 6. Normalize Mismatch (tile grid dispatch)
        _kernelNormalizeMismatch!.BindPipeline(cmdPhase2);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase2, PipelineBindPoint.Compute, _kernelNormalizeMismatch.PipelineLayout, 0, 1, &setNormalize, 0, null);
        _kernelNormalizeMismatch.Dispatch(cmdPhase2, (uint)nTilesX, (uint)nTilesY, 1);

        // Barrier: NormalizeMismatch -> Accumulate/Merge
        AddComputeBarrier(cmdPhase2);

        // 6b. Accumulate normalized mismatch into totalMismatchTexture (GPU)
        if (totalMismatchTexture is not null && totalImageCount > 1)
        {
            // Need to update params with NumTextures
            freqParams.NumTextures = totalImageCount;
            paramBuffer.SetData([freqParams]);

            totalMismatchTexture.TransitionLayout(ImageLayout.General, cmdPhase2);

            var setAccum = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(setAccum, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setAccum, ShaderBindings.FrequencyDomain.RefTexture, texMismatch.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setAccum, ShaderBindings.FrequencyDomain.OutputTexture, totalMismatchTexture.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelAccumulateMismatchRgba!.BindPipeline(cmdPhase2);
            _ctx.Vk.CmdBindDescriptorSets(cmdPhase2, PipelineBindPoint.Compute, _kernelAccumulateMismatchRgba.PipelineLayout, 0, 1, &setAccum, 0, null);
            _kernelAccumulateMismatchRgba.Dispatch(cmdPhase2, (uint)nTilesX, (uint)nTilesY, 1);

            // Restore NumTextures
            freqParams.NumTextures = 1;
            paramBuffer.SetData([freqParams]);

            AddComputeBarrier(cmdPhase2);
        }

        // 7. Highlights (tile grid dispatch) - independent, can run before FFT
        _kernelHighlightsNorm!.BindPipeline(cmdPhase2);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase2, PipelineBindPoint.Compute, _kernelHighlightsNorm.PipelineLayout, 0, 1, &setHighlights, 0, null);
        _kernelHighlightsNorm.Dispatch(cmdPhase2, (uint)nTilesX, (uint)nTilesY, 1);

        // 8. Forward FFT Aligned (per-tile FFT dispatch) - independent of highlights
        _kernelForwardFft!.BindPipeline(cmdPhase2);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase2, PipelineBindPoint.Compute, _kernelForwardFft.PipelineLayout, 0, 1, &setForwardFft, 0, null);
        _kernelForwardFft.Dispatch(cmdPhase2, (uint)nTilesX, (uint)nTilesY, 1);

        // Barrier: Forward FFT and Highlights -> Merge
        AddComputeBarrier(cmdPhase2);

        // 9. Merge Frequency (per-tile dispatch)
        _kernelMergeFrequency!.BindPipeline(cmdPhase2);
        _ctx.Vk.CmdBindDescriptorSets(cmdPhase2, PipelineBindPoint.Compute, _kernelMergeFrequency.PipelineLayout, 0, 1, &setMerge, 0, null);
        _kernelMergeFrequency.Dispatch(cmdPhase2, (uint)nTilesX, (uint)nTilesY, 1);

        // Submit phase 2
        _ctx.EndSingleTimeCommands(cmdPhase2);
    }

    /// <summary>
    /// Adds a compute shader memory barrier to ensure writes complete before reads.
    /// </summary>
    private void AddComputeBarrier(CommandBuffer cmd)
    {
        var memoryBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };

        _ctx.Vk.CmdPipelineBarrier(
            cmd,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit,
            0,
            1, &memoryBarrier,
            0, null,
            0, null);
    }

    /// <summary>
    /// Executes forward FFT on an RGBA texture.
    /// Output is double width for complex storage.
    /// </summary>
    public void ExecuteForwardFft(VulkanImage input, VulkanImage output, int tileSize, int width, int height)
    {
        EnsureKernels();

        var nTilesX = width / tileSize;
        var nTilesY = height / tileSize;

        var freqParams = new FrequencyParams { TileSize = tileSize };
        using var pb = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        pb.SetData([freqParams]);

        var cmd = _ctx.BeginSingleTimeCommands();

        input.TransitionLayout(ImageLayout.General, cmd);
        output.TransitionLayout(ImageLayout.General, cmd);

        // Create dummy images for unused descriptor bindings
        using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        dummyTex.TransitionLayout(ImageLayout.General, cmd);

        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, pb.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.MismatchTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.HighlightsTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, output.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelForwardFft!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelForwardFft.PipelineLayout, 0, 1, &set, 0, null);
        _kernelForwardFft.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);

        // Add memory barrier
        var imageBarrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = output.Handle,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmd,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit,
            0, 0, null, 0, null, 1, &imageBarrier);

        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Executes backward FFT to convert frequency domain back to spatial domain.
    /// </summary>
    public void ExecuteBackwardFft(VulkanImage inputFt, VulkanImage outputSpatial, int numTextures, int tileSize)
    {
        EnsureKernels();

        var width = (int)outputSpatial.Width;
        var height = (int)outputSpatial.Height;
        var nTilesX = width / tileSize;
        var nTilesY = height / tileSize;

        var freqParams = new FrequencyParams
        {
            TileSize = tileSize,
            NumTextures = numTextures
        };

        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([freqParams]);

        var cmd = _ctx.BeginSingleTimeCommands();

        inputFt.TransitionLayout(ImageLayout.General, cmd);
        outputSpatial.TransitionLayout(ImageLayout.General, cmd);

        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, inputFt.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, outputSpatial.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelBackwardFft!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelBackwardFft.PipelineLayout, 0, 1, &set, 0, null);

        _kernelBackwardFft.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Calculates RMS (root mean square) values per tile from the RGBA reference texture.
    /// </summary>
    public void ExecuteCalculateRms(VulkanImage rgbaInput, VulkanImage rmsOutput, int nTilesX, int nTilesY, int tileSize)
    {
        EnsureKernels();

        var freqParams = new FrequencyParams { TileSize = tileSize };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([freqParams]);

        var cmd = _ctx.BeginSingleTimeCommands();
        rgbaInput.TransitionLayout(ImageLayout.General, cmd);
        rmsOutput.TransitionLayout(ImageLayout.General, cmd);

        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, rgbaInput.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, rmsOutput.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelRms!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelRms.PipelineLayout, 0, 1, &set, 0, null);

        _kernelRms.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);

        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Performs deconvolution in frequency domain.
    /// </summary>
    public void ExecuteDeconvoluteFrequency(VulkanImage finalTextureFt, VulkanImage mismatchTexture, int nTilesX, int nTilesY, int tileSize)
    {
        EnsureKernels();

        var freqParams = new FrequencyParams { TileSize = tileSize };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([freqParams]);

        var cmd = _ctx.BeginSingleTimeCommands();
        finalTextureFt.TransitionLayout(ImageLayout.General, cmd);
        mismatchTexture.TransitionLayout(ImageLayout.General, cmd);

        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, mismatchTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, finalTextureFt.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelDeconvoluteFrequency!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelDeconvoluteFrequency.PipelineLayout, 0, 1, &set, 0, null);

        _kernelDeconvoluteFrequency.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Reduces tile border artifacts after backward FFT.
    /// </summary>
    public void ExecuteReduceArtifacts(VulkanImage outputTexture, VulkanImage refTexture, int nTilesX, int nTilesY, int tileSize, int[] blackLevel)
    {
        EnsureKernels();

        var freqParams = new FrequencyParams
        {
            TileSize = tileSize,
            BlackLevelMean = (blackLevel[0] + blackLevel[1] + blackLevel[2] + blackLevel[3]) / 4.0f,
            BlackLevel0 = blackLevel[0],
            BlackLevel1 = blackLevel[1],
            BlackLevel2 = blackLevel[2],
            BlackLevel3 = blackLevel[3]
        };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([freqParams]);

        var cmd = _ctx.BeginSingleTimeCommands();
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, refTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, outputTexture.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelArtifactsTileBorder!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelArtifactsTileBorder.PipelineLayout, 0, 1, &set, 0, null);

        _kernelArtifactsTileBorder.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// OPTIMIZED: Executes deconvolution, backward FFT, and artifact reduction in a single batched command buffer.
    /// Reduces sync points from 3 to 1.
    /// </summary>
    public void ExecutePostProcessingBatched(
        VulkanImage finalTextureFt,
        VulkanImage mismatchTexture,
        VulkanImage outputSpatial,
        VulkanImage refTextureForArtifacts,
        int nTilesX,
        int nTilesY,
        int tileSize,
        int numTextures,
        int[] blackLevel,
        bool skipReduceArtifacts = false)
    {
        EnsureKernels();

        // Create param buffer with all needed values
        var freqParams = new FrequencyParams
        {
            TileSize = tileSize,
            NumTextures = numTextures,
            BlackLevelMean = (blackLevel[0] + blackLevel[1] + blackLevel[2] + blackLevel[3]) / 4.0f,
            BlackLevel0 = blackLevel[0],
            BlackLevel1 = blackLevel[1],
            BlackLevel2 = blackLevel[2],
            BlackLevel3 = blackLevel[3]
        };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData([freqParams]);

        var cmd = _ctx.BeginSingleTimeCommands();

        // Layout transitions
        finalTextureFt.TransitionLayout(ImageLayout.General, cmd);
        mismatchTexture.TransitionLayout(ImageLayout.General, cmd);
        outputSpatial.TransitionLayout(ImageLayout.General, cmd);

        // Pre-allocate descriptor sets
        var setDeconvolute = _descriptors.Allocate(_frequencyLayout);
        var setBackwardFft = _descriptors.Allocate(_frequencyLayout);

        // Deconvolute
        _descriptors.UpdateBuffer(setDeconvolute, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setDeconvolute, ShaderBindings.FrequencyDomain.RefTexture, mismatchTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setDeconvolute, ShaderBindings.FrequencyDomain.OutputTexture, finalTextureFt.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelDeconvoluteFrequency!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelDeconvoluteFrequency.PipelineLayout, 0, 1, &setDeconvolute, 0, null);
        _kernelDeconvoluteFrequency.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);

        // Barrier: Deconvolute -> BackwardFFT
        AddComputeBarrier(cmd);

        // Backward FFT
        _descriptors.UpdateBuffer(setBackwardFft, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setBackwardFft, ShaderBindings.FrequencyDomain.RefTexture, finalTextureFt.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setBackwardFft, ShaderBindings.FrequencyDomain.OutputTexture, outputSpatial.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelBackwardFft!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelBackwardFft.PipelineLayout, 0, 1, &setBackwardFft, 0, null);
        _kernelBackwardFft.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);

        // Barrier: BackwardFFT -> ReduceArtifacts
        AddComputeBarrier(cmd);

        // Reduce artifacts (unless skipped)
        if (!skipReduceArtifacts)
        {
            var setArtifacts = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(setArtifacts, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setArtifacts, ShaderBindings.FrequencyDomain.RefTexture, refTextureForArtifacts.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setArtifacts, ShaderBindings.FrequencyDomain.OutputTexture, outputSpatial.View, ImageLayout.General, DescriptorType.StorageImage);

            _kernelArtifactsTileBorder!.BindPipeline(cmd);
            _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelArtifactsTileBorder.PipelineLayout, 0, 1, &setArtifacts, 0, null);
            _kernelArtifactsTileBorder.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
        }

        _ctx.EndSingleTimeCommands(cmd);
    }

    /// <summary>
    /// Runs FFT round-trip validation: Forward FFT -> Backward FFT should return the original data.
    /// </summary>
    public List<ValidationResult> RunFftRoundTripValidation(VulkanImage rgbaInput, int tileSize)
    {
        var results = new List<ValidationResult>();

        Console.WriteLine("\n=== FFT Round-Trip Validation ===\n");

        // 1. Capture original RGBA data
        var originalData = rgbaInput.GetData<float>();
        var originalStats = FftValidator.ComputeRgbaStats(originalData);

        Console.WriteLine($"[Validation] Original RGBA texture: {rgbaInput.Width}x{rgbaInput.Height}");
        Console.WriteLine($"[Validation] Original stats: sum={originalStats.TotalSum:G6}, energy={originalStats.TotalEnergy:G6}");

        // 2. Forward FFT
        var spatialWidth = (int)rgbaInput.Width;
        var spatialHeight = (int)rgbaInput.Height;
        var ftWidth = spatialWidth * 2; // Complex storage

        using var tempFt = new VulkanImage(_ctx, (uint)ftWidth, (uint)spatialHeight, Format.R32G32B32A32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

        Console.WriteLine($"[Validation] Running Forward FFT...");
        ExecuteForwardFft(rgbaInput, tempFt, tileSize, spatialWidth, spatialHeight);

        // 2b. Validate Forward FFT - check Parseval's theorem
        var ftData = tempFt.GetData<float>();
        var ftStats = FftValidator.ComputeFrequencyStats(ftData, spatialWidth, spatialHeight);

        Console.WriteLine($"[Validation] FFT output: width={ftWidth}, FT energy={ftStats.TotalEnergy:G6}");

        var parsevalResultOld = FftValidator.ValidateParseval(
            originalStats.TotalEnergy,
            ftStats.TotalEnergy,
            tileSize,
            "Forward FFT");
        results.Add(parsevalResultOld);
        Console.WriteLine(parsevalResultOld);

        var parsevalResultWindowed = FftValidator.ValidateParsevalWithWindow(
            originalStats.TotalEnergy,
            ftStats.TotalEnergy,
            tileSize,
            "Forward FFT");
        results.Add(parsevalResultWindowed);
        Console.WriteLine(parsevalResultWindowed);

        // Measure actual window factor
        var actualWindowFactor = WindowDiagnostics.MeasureActualWindowFactor(
            originalData, ftData, spatialWidth, spatialHeight, tileSize);
        Console.WriteLine($"\n[DIAGNOSTIC] Measured Window Factor: {actualWindowFactor:F6}");
        Console.WriteLine($"[DIAGNOSTIC] Expected Hann Window:   {9.0 / 64.0:F6} (0.140625)");
        Console.WriteLine($"[DIAGNOSTIC] Ratio: {actualWindowFactor / (9.0 / 64.0):F4}");

        var windowDiagnostic = WindowDiagnostics.ValidateWindowFunction(
            actualWindowFactor, tileSize, "Forward FFT Window");
        results.Add(windowDiagnostic);
        Console.WriteLine(windowDiagnostic);

        // 3. Backward FFT (using numTextures=1 for simple round-trip)
        using var roundTripOutput = new VulkanImage(_ctx, (uint)spatialWidth, (uint)spatialHeight, Format.R32G32B32A32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

        Console.WriteLine($"[Validation] Running Backward FFT...");
        ExecuteBackwardFft(tempFt, roundTripOutput, 1, tileSize);

        // 4. Compare original vs round-trip output
        var afterRoundTrip = roundTripOutput.GetData<float>();
        var afterStats = FftValidator.ComputeRgbaStats(afterRoundTrip);

        Console.WriteLine($"[Validation] After round-trip: sum={afterStats.TotalSum:G6}, energy={afterStats.TotalEnergy:G6}");

        // Calculate range for tolerance
        double range = 0;
        foreach (var t in originalData)
        {
            if (Math.Abs(t) > range)
            {
                range = Math.Abs(t);
            }
        }

        var roundTripResult = FftValidator.ValidateRoundTrip(originalData, afterRoundTrip, range, "FFT Round-Trip");
        results.Add(roundTripResult);
        Console.WriteLine(roundTripResult);

        // 5. DC component check on backward FFT
        var dcBinEstimate = originalStats.TotalSum;
        var normFactor = tileSize * tileSize * 1;

        var dcResult = FftValidator.ValidateDcComponent(
            dcBinEstimate,
            afterStats.MeanPerChannel * 4 * afterStats.PixelCount,
            normFactor,
            "Backward FFT DC");
        results.Add(dcResult);
        Console.WriteLine(dcResult);

        // 6. Summary diagnosis
        Console.WriteLine("\n--- Validation Summary ---");
        if (!roundTripResult.Passed)
        {
            var outputRatio = afterStats.TotalSum / originalStats.TotalSum;
            Console.WriteLine($"[DIAGNOSIS] Output/Input ratio: {outputRatio:F4}");

            switch (outputRatio)
            {
                case < 0.2:
                    Console.WriteLine("[DIAGNOSIS] Output is <20% of input -> Backward FFT is severely broken");
                    break;
                case > 5:
                    Console.WriteLine("[DIAGNOSIS] Output is >5x input -> Normalization issue or Forward FFT bug");
                    break;
            }

            Console.WriteLine(parsevalResultWindowed.Passed
                ? "[DIAGNOSIS] Forward FFT passed Parseval's theorem -> Bug is in backward_fft.hlsl"
                : "[DIAGNOSIS] Forward FFT failed Parseval's theorem -> Bug may be in forward FFT too");
        }
        else
        {
            Console.WriteLine("[DIAGNOSIS] Round-trip PASSED -> FFT shaders are working correctly");
            Console.WriteLine("[DIAGNOSIS] If output is still wrong, the bug is in the merge/pipeline, not FFT");
        }
        Console.WriteLine();

        return results;
    }
}
