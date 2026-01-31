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

        Console.WriteLine("[FrequencyMergePipeline] Frequency domain shaders compiled successfully!");
    }

    /// <summary>
    /// Executes the full frequency domain merge pipeline for one alternate frame.
    /// Computes RMS, mismatch, highlights, forward FFT, and accumulates into pixelAccumFT.
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

        // Transition
        var cmd = _ctx.BeginSingleTimeCommands();
        texDiff.TransitionLayout(ImageLayout.General, cmd);
        texRms.TransitionLayout(ImageLayout.General, cmd);
        texMismatch.TransitionLayout(ImageLayout.General, cmd);
        texHighlights.TransitionLayout(ImageLayout.General, cmd);
        texAlignedFt.TransitionLayout(ImageLayout.General, cmd);
        _ctx.EndSingleTimeCommands(cmd);

        // Helper to dispatch non-FFT kernels (per-pixel dispatch)
        void DispatchPixel(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage? t1 = null, VulkanImage? t2 = null, VulkanImage? t3 = null, VulkanImage? t4 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if (t0 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t1 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t2 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t3 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.MismatchTexture, t3.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t4 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.HighlightsTexture, t4.View, ImageLayout.General, DescriptorType.SampledImage);
            if (u0 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, u0.View, ImageLayout.General, DescriptorType.StorageImage);

            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
            kernel.Dispatch(cmd2, (uint)width, (uint)height, 1);
            _ctx.EndSingleTimeCommands(cmd2);
        }

        // Helper to dispatch FFT kernels (per-tile dispatch)
        void DispatchTile(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage? t1 = null, VulkanImage? t2 = null, VulkanImage? t3 = null, VulkanImage? t4 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if (t0 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t1 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t2 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t3 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.MismatchTexture, t3.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t4 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.HighlightsTexture, t4.View, ImageLayout.General, DescriptorType.SampledImage);
            if (u0 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, u0.View, ImageLayout.General, DescriptorType.StorageImage);

            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

            var nTilesXLocal = (uint)(width / tileSizeMerge);
            var nTilesYLocal = (uint)(height / tileSizeMerge);
            kernel.Dispatch(cmd2, nTilesXLocal, nTilesYLocal, 1);
            _ctx.EndSingleTimeCommands(cmd2);
        }

        // Helper to dispatch tile-grid kernels (for RMS, Mismatch, Highlights)
        void DispatchTileGrid(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage? t1 = null, VulkanImage? t2 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if (t0 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t1 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if (t2 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if (u0 is not null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, u0.View, ImageLayout.General, DescriptorType.StorageImage);

            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
            kernel.Dispatch(cmd2, (uint)nTilesX, (uint)nTilesY, 1);
            _ctx.EndSingleTimeCommands(cmd2);
        }

        // 1. Abs Diff (full image size dispatch)
        DispatchPixel(_kernelAbsDiff!, texDiff, refPyramid0, aligned);

        // 2. RMS (tile grid dispatch)
        DispatchTileGrid(_kernelRms!, texRms, refPyramid0);

        // 3. Mismatch (tile grid dispatch)
        DispatchTileGrid(_kernelMismatch!, texMismatch, texDiff, texRms);

        // 4. Mean Mismatch (CPU Readback)
        var misData = texMismatch.GetData<float>();
        double sum = 0;
        for (var k = 0; k < misData.Length; k += 4)
        {
            sum += misData[k];
        }
        var mean = (float)(sum / (misData.Length / 4));
        if (mean < 1e-6f)
        {
            mean = 1e-6f;
        }

        freqParams.MeanMismatch = mean * 2.0f;
        paramBuffer.SetData([freqParams]);

        // 5. Normalize Mismatch (tile grid dispatch)
        DispatchTileGrid(_kernelNormalizeMismatch!, texMismatch, texMismatch);

        // 5b. Accumulate normalized mismatch into totalMismatchTexture
        if (totalMismatchTexture is not null && totalImageCount > 1)
        {
            var mismatchData = texMismatch.GetData<float>();
            var accumData = totalMismatchTexture.GetData<float>();
            var divisor = (float)totalImageCount;
            for (var i = 0; i < mismatchData.Length; i++)
            {
                accumData[i] += mismatchData[i] / divisor;
            }
            totalMismatchTexture.SetData(accumData);
        }

        // 6. Highlights (tile grid dispatch)
        DispatchTileGrid(_kernelHighlightsNorm!, texHighlights, aligned);

        // 7. Forward FFT Aligned (per-tile FFT dispatch)
        {
            var inputData = aligned.GetData<float>();
            double inputSum = 0;
            var inputSamples = Math.Min(inputData.Length, 1000);
            for (var i = 0; i < inputSamples; i++)
            {
                inputSum += Math.Abs(inputData[i]);
            }
            Console.WriteLine($"[FFT DEBUG] BEFORE FFT: aligned texture sum={inputSum:F2}, mean={inputSum / inputSamples:F4}, samples={inputSamples}, total_size={inputData.Length}");
            Console.WriteLine($"[FFT DEBUG] Input dimensions: {aligned.Width}x{aligned.Height}, format={aligned.Format}");
            Console.WriteLine($"[FFT DEBUG] Output dimensions: {texAlignedFt.Width}x{texAlignedFt.Height}, format={texAlignedFt.Format}");
        }

        DispatchTile(_kernelForwardFft!, texAlignedFt, aligned);

        // DEBUG: Check output from FFT
        {
            var outputData = texAlignedFt.GetData<float>();
            double outputTotal = 0;
            foreach (var t in outputData)
            {
                outputTotal += Math.Abs(t);
            }

            Console.WriteLine($"[FFT DEBUG] AFTER FFT: texAlignedFT TOTAL={outputTotal:F2}, mean={outputTotal / outputData.Length:F4}");
            if (outputTotal < 0.01)
            {
                Console.WriteLine($"[FFT DEBUG] FFT OUTPUT IS ZERO!");
            }
        }

        // 8. Merge Frequency (per-tile dispatch)
        DispatchTile(_kernelMergeFrequency!, pixelAccumFt, refFt, texAlignedFt, texRms, texMismatch, texHighlights);
    }

    /// <summary>
    /// Executes forward FFT on an RGBA texture.
    /// Output is double width for complex storage.
    /// </summary>
    public void ExecuteForwardFft(VulkanImage input, VulkanImage output, int tileSize, int width, int height)
    {
        Console.WriteLine($"[EXEC FFT] *** ExecuteForwardFft CALLED ***");

        try
        {
            EnsureKernels();

            var nTilesX = width / tileSize;
            var nTilesY = height / tileSize;

            Console.WriteLine($"║ [2/9] Configuration:");
            Console.WriteLine($"║       Input texture:  {input.Width}x{input.Height} (format: {input.Format})");
            Console.WriteLine($"║       Output texture: {output.Width}x{output.Height} (format: {output.Format})");
            Console.WriteLine($"║       TileSize: {tileSize}");
            Console.WriteLine($"║       Spatial dimensions: {width}x{height}");
            Console.WriteLine($"║       Tile grid: {nTilesX}x{nTilesY} tiles");

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
            Console.WriteLine($"║ [FFT DEBUG] ExecuteForwardFft EXIT - SUCCESS");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"║ EXCEPTION in ExecuteForwardFft: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
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

        Console.WriteLine($"[ExecuteBackwardFft] InputFT: {inputFt.Width}x{inputFt.Height}, Output: {width}x{height}");
        Console.WriteLine($"[ExecuteBackwardFft] TileSize={tileSize}, NumTextures={numTextures}, Tiles={nTilesX}x{nTilesY}");

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

        Console.WriteLine($"[ExecuteBackwardFft] Dispatching {nTilesX}x{nTilesY} threads");
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

        Console.WriteLine($"[ExecuteCalculateRms] Input: {rgbaInput.Width}x{rgbaInput.Height}, Output: {rmsOutput.Width}x{rmsOutput.Height}, Dispatch: {nTilesX}x{nTilesY}");
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
