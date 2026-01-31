using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using BurstPhoto.Rendering.Debug;
using BurstPhoto.Rendering.Pipelines;
using BurstPhoto.Rendering.Utilities;
using BurstPhoto.Rendering.Validation;
using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering.Implementations;

public unsafe class VulkanComputePipeline : IComputePipeline
{
    private readonly VulkanContext _ctx;
    private readonly VulkanShaderCompiler _compiler;
    private readonly VulkanDescriptorManager _descriptors;

    // Shader Cache - only kernels still directly used by VulkanComputePipeline
    private ComputeKernel? _kernelPrepareBayer;

    // Bayer <-> RGBA Conversion Kernels (for FFT pipeline)
    private ComputeKernel? _kernelConvertToRgba;
    private ComputeKernel? _kernelConvertToBayer;
    private DescriptorSetLayout _conversionLayout;
    private DescriptorSetLayout _prepareLayout;

    // Debug helper for dumping intermediate textures
    private readonly PipelineDebugHelper _debugHelper;

    // Debug inspector for inline texture sampling (expensive, off by default)
    private readonly PipelineDebugInspector _debugInspector;

    // Texture utilities for common operations
    private readonly TextureUtilities _textureUtils;

    // Texture conversion helper (Bayer <-> RGBA, Prepare)
    private readonly TextureConversionHelper _conversionHelper;

    // Kernel manager for centralized kernel creation and caching
    private readonly VulkanKernelManager _kernelManager;

    // Alignment pipeline for pyramid-based image alignment
    private readonly AlignmentPipeline _alignmentPipeline;

    // Exposure pipeline for exposure correction and noise estimation
    private readonly ExposurePipeline _exposurePipeline;

    // Spatial merge pipeline for weighted frame merging
    private readonly SpatialMergePipeline _spatialMergePipeline;

    // Frequency merge pipeline for FFT-based merging
    private readonly FrequencyMergePipeline _frequencyMergePipeline;

    // FFT Validation: Set to true to run mathematical validation tests
    public bool EnableFftValidation { get; set; }
    private List<ValidationResult> _validationResults = [];

    // Flag to track if pipelines are initialized
    private bool _pipelinesInitialized;

    public VulkanComputePipeline(VulkanContext ctx)
    {
        _ctx = ctx;
        _compiler = new VulkanShaderCompiler();
        // Increase descriptor pool size for 4-iteration frequency domain merge
        // Each iteration creates ~20-30 descriptor sets (pyramids, textures, etc.)
        _descriptors = new VulkanDescriptorManager(_ctx, maxSets: 500);
        _kernelManager = new VulkanKernelManager(_ctx, _descriptors);
        _alignmentPipeline = new AlignmentPipeline(_ctx, _descriptors, _kernelManager);
        _exposurePipeline = new ExposurePipeline(_ctx, _descriptors, _kernelManager);
        _spatialMergePipeline = new SpatialMergePipeline(_ctx, _descriptors, _kernelManager);
        _frequencyMergePipeline = new FrequencyMergePipeline(_ctx, _descriptors, _kernelManager);
        _debugHelper = new PipelineDebugHelper();
        _debugInspector = new PipelineDebugInspector();
        _textureUtils = new TextureUtilities(_ctx, _descriptors, _kernelManager);
        _conversionHelper = new TextureConversionHelper(_ctx, _descriptors, _textureUtils);
    }

    /// <summary>
    /// Initializes all pipelines upfront. Call this once before processing to avoid
    /// per-execution Ensure* overhead.
    /// </summary>
    private void InitializePipelines(bool isFrequencyMode)
    {
        if (_pipelinesInitialized) return;

        EnsurePreparePipeline();

        if (isFrequencyMode)
        {
            EnsureConversionPipeline();
        }

        _pipelinesInitialized = true;
    }

    public async Task<RawImage> ProcessAsync(RenderingInput input, ProcessingOptions options, ProcessingProgress progress)
    {
        Console.WriteLine("[VulkanComputePipeline] Starting processing...");

        var isFrequency = options.Merging == MergingAlgorithm.HigherQuality;

        // Initialize all pipelines upfront
        InitializePipelines(isFrequency);

        // Enable debug dump if requested
        _debugHelper.Enabled = options.EnableDebugDump;
        _debugInspector.Enabled = options.EnableDebugDump;

        if (_debugHelper.Enabled)
        {
            Console.WriteLine("[VulkanComputePipeline] DEBUG DUMP ENABLED - intermediate DNGs will be saved to DebugOutput/");
        }

        // Enable FFT validation if requested (from options, not hardcoded)
        EnableFftValidation = options.EnableFftValidation;
        _validationResults.Clear();

        if (EnableFftValidation)
        {
            Console.WriteLine("[VulkanComputePipeline] FFT VALIDATION ENABLED - mathematical tests will be run on each stage");
        }

        // 2. Setup Reference Frame
        var refImage = input.Images[input.ReferenceFrameIndex];
        var width = refImage.Width;
        var height = refImage.Height;

        // Calculate Padded Dimensions for Alignment
        var tileSize = ProcessingOptions.GetTileSizePixels(options.TileSize);
        int pad;
        int outWidth;
        int outHeight;

        if (isFrequency)
        {
            pad = 0;
            // Ensure multiple of TileSize for FFT alignment
            outWidth = ((width + tileSize - 1) / tileSize) * tileSize;
            outHeight = ((height + tileSize - 1) / tileSize) * tileSize;
        }
        else
        {
            // Spatial padding (Center alignment)
            pad = tileSize / 2;
            outWidth = width + tileSize;
            outHeight = height + tileSize;

            if (outWidth % 2 != 0) outWidth++;
            if (outHeight % 2 != 0) outHeight++;
        }

        Console.WriteLine($"[VulkanComputePipeline] Input: {width}x{height}, Padded: {outWidth}x{outHeight}, Mode: {options.Merging}");

        // 3. Allocate Resources
        using var rawTexture = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);

        // Padded/Float Texture (Buffer for Processing)
        using var preparedTexture = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

        // 4. Upload Reference Frame (once, before iteration loop)
        Console.WriteLine("[VulkanComputePipeline] Uploading Reference Frame...");
        rawTexture.SetData(refImage.Data);

        // 5. Execute Prepare Pass
        Console.WriteLine("[VulkanComputePipeline] Executing Prepare Pass...");
        ExecutePrepare(rawTexture, preparedTexture, refImage, pad, pad);

        // DEBUG: Dump after Prepare
        _debugHelper.DumpTexture(preparedTexture, "step_1_prepare", refImage, outWidth, outHeight, pad);

        progress.ProgressInt += 50_000_000;

        // 5b. Alignment Pyramid
        Console.WriteLine("[VulkanComputePipeline] Generating Alignment Pyramid...");
        var pyramid = new List<VulkanImage>();

        // Level 0: Downsampled from preparedTexture by 2
        var l0W = (int)preparedTexture.Width / 2;
        var l0H = (int)preparedTexture.Height / 2;
        if (l0W % 2 != 0) l0W++;
        if (l0H % 2 != 0) l0H++;

        var level0 = new VulkanImage(_ctx, (uint)l0W, (uint)l0H, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
        ExecuteAvgPool(preparedTexture, level0, 2, refImage);
        pyramid.Add(level0);

        var currentW = l0W;
        var currentH = l0H;

        // Create 3 more levels
        for (var i = 1; i < 4; i++)
        {
            var nextW = currentW / 2;
            if (nextW % 2 != 0) nextW++;
            var nextH = currentH / 2;
            if (nextH % 2 != 0) nextH++;

            var levelImg = new VulkanImage(_ctx, (uint)nextW, (uint)nextH, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

            ExecuteAvgPool(pyramid[i - 1], levelImg, 2, refImage);

            pyramid.Add(levelImg);
            currentW = nextW;
            currentH = nextH;
        }

        var disposables = new List<IDisposable>();

        Console.WriteLine("[VulkanComputePipeline] Starting Alignment Search...");
        // Calculate TileInfo based on Level 0 dimensions (Half Resolution)
        var tileInfo = TileInfo.Calculate(width / 2, height / 2, ProcessingOptions.GetTileSizePixels(options.TileSize), ProcessingOptions.GetSearchDistancePixels(options.SearchDistance));

        // Accumulators
        VulkanImage? pixelAccum;
        VulkanImage? weightAccum;

        float estimatedNoiseSd;
        float[] floatData;

        if (isFrequency)
        {
            Console.WriteLine("[VulkanComputePipeline] === FREQUENCY DOMAIN MERGE (4-ITERATION) ===");

            // CRITICAL: Swift hardcodes tile_size_merge = 8 for FFT merging
            const int tileSizeMerge = 8;

            // Calculate alignment padding (from frequency.swift lines 80-97)
            var downscaleFactors = new[] { refImage.MosaicPatternWidth, 2, 2, 2 };
            var tileFactor = tileSize * downscaleFactors.Aggregate(1, (a, b) => a * b);

            var padAlignX = (int)Math.Ceiling((float)(width + tileSizeMerge) / tileFactor);
            padAlignX = (padAlignX * tileFactor - width - tileSizeMerge) / 2;

            var padAlignY = (int)Math.Ceiling((float)(height + tileSizeMerge) / tileFactor);
            padAlignY = (padAlignY * tileFactor - height - tileSizeMerge) / 2;

            // Calculate merge padding (smaller margin for FFT processing)
            var cropMergeX = (int)Math.Floor((float)padAlignX / (2 * tileSizeMerge));
            cropMergeX = cropMergeX * 2 * tileSizeMerge;
            var cropMergeY = (int)Math.Floor((float)padAlignY / (2 * tileSizeMerge));
            cropMergeY = cropMergeY * 2 * tileSizeMerge;

            var padMergeX = padAlignX - cropMergeX;
            var padMergeY = padAlignY - cropMergeY;

            Console.WriteLine($"[VulkanComputePipeline] Padding: Align=({padAlignX},{padAlignY}), Merge=({padMergeX},{padMergeY}), Crop=({cropMergeX},{cropMergeY})");

            // Calculate exposure correction factors
            var exposureCorr1 = 0.0;
            var exposureCorr2 = 0.0;
            var refExpBias = refImage.ExposureBias;
            var refIsoExpTime = refImage.IsoExposureTime;

            var hasExposureBias = input.Images.Any(img => img.ExposureBias != 0);

            for (var i = 0; i < input.Images.Count; i++)
            {
                var img = input.Images[i];
                double exposureFactor;

                if (hasExposureBias)
                {
                    exposureFactor = Math.Pow(2.0, (img.ExposureBias - refExpBias) / 100.0);
                }
                else
                {
                    exposureFactor = refIsoExpTime > 0 ? img.IsoExposureTime / refIsoExpTime : 1.0;
                }

                exposureCorr1 += 0.5 + 0.5 / exposureFactor;
                exposureCorr2 += Math.Min(4.0, exposureFactor);
            }
            exposureCorr1 /= input.Images.Count;
            exposureCorr2 /= input.Images.Count;

            // Allocate final accumulator
            var accWidth = width + 2 * padAlignX;
            var accHeight = height + 2 * padAlignY;
            var finalAccumulator = new VulkanImage(_ctx, (uint)accWidth, (uint)accHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            _textureUtils.FillWithZeros(finalAccumulator);
            disposables.Add(finalAccumulator);

            // === 4-ITERATION LOOP ===
            for (var iteration = 1; iteration <= 4; iteration++)
            {
                Console.WriteLine($"\n[VulkanComputePipeline] === ITERATION {iteration}/4 ===");

                // Calculate shift values
                var shiftLeft = (iteration % 2 == 0) ? tileSizeMerge : 0;
                var shiftRight = (iteration % 2 == 1) ? tileSizeMerge : 0;
                var shiftTop = (iteration < 3) ? tileSizeMerge : 0;
                var shiftBottom = (iteration >= 3) ? tileSizeMerge : 0;

                var padLeft = padAlignX + shiftLeft;
                var padRight = padAlignX + shiftRight;
                var padTop = padAlignY + shiftTop;
                var padBottom = padAlignY + shiftBottom;

                var iterOutWidth = width + padLeft + padRight;
                var iterOutHeight = height + padTop + padBottom;

                // Prepare reference with iteration-specific padding
                using var preparedRef = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                // NOTE: rawTexture already contains refImage.Data - no need to re-upload
                ExecutePrepare(rawTexture, preparedRef, refImage, padLeft, padTop);

                // Debug inspection (only runs if enabled)
                _debugInspector.InspectPreparedTexture(preparedRef, iteration, padLeft, padTop, width, height);
                _debugHelper.DumpTexture(preparedRef, $"step_1b_iter{iteration}_prepared_ref_bayer", refImage, iterOutWidth, iterOutHeight, 0);

                // RGBA dimensions
                var rgbaWidth = (iterOutWidth - 2 * cropMergeX) / 2;
                var rgbaHeight = (iterOutHeight - 2 * cropMergeY) / 2;
                var ftWidth = rgbaWidth * 2;
                var ftHeight = rgbaHeight;

                // Convert reference Bayer -> RGBA
                using var rgbaRefTexture = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteConvertToRgba(preparedRef, rgbaRefTexture, refImage.CfaPattern, cropMergeX, cropMergeY);

                _debugInspector.InspectRgbaTexture(rgbaRefTexture, iteration, "After convert_to_rgba");
                _debugHelper.DumpRgbaTexture(rgbaRefTexture, $"step_2_iter{iteration}_ref_rgba", refImage);

                // FFT VALIDATION: Run round-trip test on first iteration only
                if (EnableFftValidation && iteration == 1)
                {
                    Console.WriteLine("\n[VulkanComputePipeline] Running FFT validation (first iteration)...");
                    var validationResults = RunFftRoundTripValidation(rgbaRefTexture, tileSizeMerge);
                    _validationResults.AddRange(validationResults);

                    var roundTripResult = validationResults.FirstOrDefault(r => r.TestName.Contains("Round-Trip"));
                    if (roundTripResult is not null && !roundTripResult.Passed)
                    {
                        Console.WriteLine("\n>>> FFT VALIDATION FAILED - Round-trip test indicates FFT shader bug");
                        Console.WriteLine(">>> Continuing with processing to capture full output for analysis...\n");
                    }
                    else
                    {
                        Console.WriteLine("\n>>> FFT VALIDATION PASSED - FFT shaders are working correctly\n");
                    }
                }

                // Build reference pyramid
                var refPyramid = new List<VulkanImage>();
                var l0RefW = (int)preparedRef.Width / 2;
                var l0RefH = (int)preparedRef.Height / 2;

                if (l0RefW % 2 != 0) l0RefW++;
                if (l0RefH % 2 != 0) l0RefH++;

                var refLevel0 = new VulkanImage(_ctx, (uint)l0RefW, (uint)l0RefH, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteAvgPool(preparedRef, refLevel0, 2, refImage);
                refPyramid.Add(refLevel0);

                int currW = l0RefW, currH = l0RefH;
                for (var lvl = 1; lvl < 4; lvl++)
                {
                    var nW = currW / 2;
                    if (nW % 2 != 0) nW++;
                    var nH = currH / 2;
                    if (nH % 2 != 0) nH++;
                    var blurredPrev = new VulkanImage(_ctx, (uint)currW, (uint)currH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    disposables.Add(blurredPrev);
                    ExecuteBlur(refPyramid[lvl - 1], blurredPrev, 2, 1);
                    var levelImg = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(blurredPrev, levelImg, 2, refImage);
                    refPyramid.Add(levelImg);
                    currW = nW;
                    currH = nH;
                }

                var iterTileInfo = TileInfo.Calculate(l0RefW, l0RefH, tileSize, ProcessingOptions.GetSearchDistancePixels(options.SearchDistance));

                // Calculate RMS per iteration (tile grid size)
                var nTilesX = (iterOutWidth - 2 * cropMergeX) / (2 * tileSizeMerge);
                var nTilesY = (iterOutHeight - 2 * cropMergeY) / (2 * tileSizeMerge);

                using var rmsTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                ExecuteCalculateRms(rgbaRefTexture, rmsTexture, nTilesX, nTilesY, tileSizeMerge);

                // Initialize total mismatch texture for this iteration
                using var totalMismatchTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
                _textureUtils.FillWithZeros(totalMismatchTexture);

                // Forward FFT on reference
                using var refFt = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteForwardFft(rgbaRefTexture, refFt, tileSizeMerge, rgbaWidth, rgbaHeight);

                _debugInspector.InspectFftOutput(refFt, iteration, "After forward_fft");

                // Initialize frequency domain accumulator
                using var finalTextureFt = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
                _textureUtils.CopyImage(refFt, finalTextureFt, ftWidth, ftHeight);

                // Estimate noise for this iteration
                estimatedNoiseSd = ExecuteNoiseEstimationGpu(preparedRef, refImage.MosaicPatternWidth);

                // === COMPARISON LOOP ===
                for (var compIdx = 0; compIdx < input.Images.Count; compIdx++)
                {
                    if (compIdx == input.ReferenceFrameIndex)
                    {
                        continue;
                    }

                    var altImage = input.Images[compIdx];

                    // Prepare comparison frame with iteration-specific padding
                    using var rawAlt = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint,
                        ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                    rawAlt.SetData(altImage.Data);

                    using var preparedAlt = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                    // Calculate exposure difference for prepare pass
                    int prepareExpDiff;
                    if (hasExposureBias)
                    {
                        prepareExpDiff = refImage.ExposureBias - altImage.ExposureBias;
                    }
                    else
                    {
                        var ratio = refImage.IsoExposureTime / Math.Max(altImage.IsoExposureTime, 0.0001);
                        prepareExpDiff = (int)Math.Round(Math.Log2(ratio) * 100.0);
                    }
                    ExecutePrepare(rawAlt, preparedAlt, altImage, padLeft, padTop, prepareExpDiff);

                    _debugHelper.DumpTexture(preparedAlt, $"step_1c_iter{iteration}_prepared_comp{compIdx}_bayer", refImage, iterOutWidth, iterOutHeight, 0);

                    // Build comparison pyramid
                    var altPyramid = new List<VulkanImage>();
                    var l0AltW = (int)preparedAlt.Width / 2;
                    var l0AltH = (int)preparedAlt.Height / 2;

                    if (l0AltW % 2 != 0) l0AltW++;
                    if (l0AltH % 2 != 0) l0AltH++;

                    var altLevel0 = new VulkanImage(_ctx, (uint)l0AltW, (uint)l0AltH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(preparedAlt, altLevel0, 2, altImage);
                    altPyramid.Add(altLevel0);

                    currW = l0AltW;
                    currH = l0AltH;
                    for (var lvl = 1; lvl < 4; lvl++)
                    {
                        var nW = currW / 2;
                        if (nW % 2 != 0) nW++;
                        var nH = currH / 2;
                        if (nH % 2 != 0) nH++;
                        using var blurredPrevAlt = new VulkanImage(_ctx, (uint)currW, (uint)currH, Format.R32Sfloat,
                            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteBlur(altPyramid[lvl - 1], blurredPrevAlt, 2, 1);
                        var levelImg = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat,
                            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteAvgPool(blurredPrevAlt, levelImg, 2, altImage);
                        altPyramid.Add(levelImg);
                        currW = nW;
                        currH = nH;
                    }

                    // Align and warp
                    using var alignment = new VulkanImage(_ctx, (uint)iterTileInfo.NTilesX, (uint)iterTileInfo.NTilesY, Format.R16G16B16A16Sint,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                    var isUniformExposure = (altImage.ExposureBias == refImage.ExposureBias);
                    ExecuteAlignmentSearch(refPyramid, altPyramid, alignment, iterTileInfo, 2, isUniformExposure);

                    _debugHelper.DumpAlignment(alignment, $"step_2a_iter{iteration}_alignment_comp{compIdx}", refImage);

                    using var warpedAlt = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                    ExecuteWarp(preparedAlt, warpedAlt, alignment, iterTileInfo, padLeft, padTop);

                    _debugHelper.DumpTexture(warpedAlt, $"step_2b_iter{iteration}_warped_comp{compIdx}_bayer", refImage, iterOutWidth, iterOutHeight, 0);

                    using var alignedTextureRgba = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteConvertToRgba(warpedAlt, alignedTextureRgba, refImage.CfaPattern, cropMergeX, cropMergeY);

                    _debugHelper.DumpRgbaTexture(alignedTextureRgba, $"step_3_iter{iteration}_aligned_comp{compIdx}_rgba", refImage);

                    // Execute frequency domain merge
                    var uniformExp = hasExposureBias
                        ? (altImage.ExposureBias == refImage.ExposureBias ? 1 : 0)
                        : (Math.Abs(altImage.IsoExposureTime - refImage.IsoExposureTime) < 0.001f ? 1 : 0);
                    var expDiffForMerge = (float)(-prepareExpDiff);
                    ExecuteMergeFrequency(refFt, rgbaRefTexture, alignedTextureRgba, null!, finalTextureFt,
                        refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd, expDiffForMerge, tileSize, refImage.MosaicPatternWidth, uniformExp,
                        totalMismatchTexture, input.Images.Count, exposureCorr1 / exposureCorr2);

                    // Cleanup alt pyramid
                    foreach (var lvl in altPyramid)
                    {
                        if (lvl != preparedAlt)
                        {
                            lvl.Dispose();
                        }
                    }
                }

                // Post-iteration processing
                _debugInspector.InspectDeconvolution(finalTextureFt, iteration, true);

                // Deconvolute with accumulated mismatch
                ExecuteDeconvoluteFrequency(finalTextureFt, totalMismatchTexture, nTilesX, nTilesY, tileSizeMerge);

                _debugInspector.InspectDeconvolution(finalTextureFt, iteration, false);

                // Backward FFT
                using var outputTextureRgba = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteBackwardFft(finalTextureFt, outputTextureRgba, input.Images.Count, tileSizeMerge);

                _debugInspector.InspectBackwardFftOutput(outputTextureRgba, iteration, tileSizeMerge, rgbaWidth, rgbaHeight);
                _debugHelper.DumpRgbaTexture(outputTextureRgba, $"step_4_iter{iteration}_merged_before_reduce", refImage);

                // Reduce tile border artifacts
                var bayerTilesX = nTilesX;
                var bayerTilesY = nTilesY;

                ExecuteReduceArtifacts(outputTextureRgba, rgbaRefTexture, bayerTilesX, bayerTilesY, tileSizeMerge, refImage.BlackLevel);

                _debugHelper.DumpRgbaTexture(outputTextureRgba, $"step_5_iter{iteration}_merged_after_reduce", refImage);

                // Convert RGBA -> Bayer
                var bayerWidth = rgbaWidth * 2;
                var bayerHeight = rgbaHeight * 2;
                using var outputTextureBayer = new VulkanImage(_ctx, (uint)bayerWidth, (uint)bayerHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteConvertToBayer(outputTextureRgba, outputTextureBayer, refImage.CfaPattern);

                _debugInspector.InspectBayerOutput(outputTextureBayer, iteration);
                _debugHelper.DumpTexture(outputTextureBayer, $"step_5b_iter{iteration}_bayer_after_convert", refImage, bayerWidth, bayerHeight, 0);

                // Calculate crop amounts
                var cropLeft = padMergeX + shiftLeft;
                var cropRight = padMergeX + shiftRight;
                var cropTop = padMergeY + shiftTop;
                var cropBottom = padMergeY + shiftBottom;

                // GPU-accelerated accumulation (no CPU round-trip!)
                _textureUtils.AccumulateCroppedRegionGpu(
                    outputTextureBayer,
                    finalAccumulator,
                    cropLeft, cropTop,
                    padAlignX, padAlignY,
                    width, height);

                // Cleanup ref pyramid
                foreach (var lvl in refPyramid)
                {
                    if (lvl != preparedRef) lvl.Dispose();
                }

                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} complete");
            }

            // Use finalAccumulator as the merged result
            Console.WriteLine("[VulkanComputePipeline] All 4 iterations complete");

            estimatedNoiseSd = ExecuteNoiseEstimationGpu(finalAccumulator, refImage.MosaicPatternWidth);

            // Download result from final accumulator
            floatData = finalAccumulator.GetData<float>();

            // Update dimensions for exposure correction and cropping
            outWidth = accWidth;
            outHeight = accHeight;
            pad = padAlignX;

            _debugInspector.InspectFinalAccumulator(floatData, padAlignX, padAlignY, accWidth, width, height);
            _debugHelper.AnalyzeBayerTileBoundaries(floatData, accWidth, accHeight, padAlignX, padAlignY, tileSizeMerge * 2);
        }
        else
        {
            // Spatial mode
            pixelAccum = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            weightAccum = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

            pixelAccum.SetData(preparedTexture.GetData<float>()); // Init with Ref

            // Init Weight with 1.0
            var ones = new float[outWidth * outHeight];
            Array.Fill(ones, 1.0f);
            weightAccum.SetData(ones);

            disposables.Add(pixelAccum);
            disposables.Add(weightAccum);

            estimatedNoiseSd = ExecuteNoiseEstimationGpu(preparedTexture, refImage.MosaicPatternWidth);

            for (var i = 0; i < input.Images.Count; i++)
            {
                if (i == input.ReferenceFrameIndex)
                {
                    continue;
                }

                Console.WriteLine($"[VulkanComputePipeline] Aligning Image {i}...");
                var altImage = input.Images[i];

                using var rawAlt = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint,
                    ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                rawAlt.SetData(altImage.Data);

                using var preparedAlt = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecutePrepare(rawAlt, preparedAlt, altImage, pad, pad);

                // Pyramid Alternate
                var altPyramid = new List<VulkanImage>();

                var l0AltSw = (int)preparedAlt.Width / 2;
                var l0AltSh = (int)preparedAlt.Height / 2;

                if (l0AltSw % 2 != 0) l0AltSw++;
                if (l0AltSh % 2 != 0) l0AltSh++;

                var altLevel0S = new VulkanImage(_ctx, (uint)l0AltSw, (uint)l0AltSh, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteAvgPool(preparedAlt, altLevel0S, 2, altImage);
                altPyramid.Add(altLevel0S);

                var currW = l0AltSw;
                var currH = l0AltSh;
                for (var val = 1; val < 4; val++)
                {
                    var nW = currW / 2;
                    if (nW % 2 != 0) nW++;

                    var nH = currH / 2;
                    if (nH % 2 != 0) nH++;

                    var lvl = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(altPyramid[val - 1], lvl, 2, altImage);
                    altPyramid.Add(lvl);
                    currW = nW;
                    currH = nH;
                }

                // Align
                var alignment = new VulkanImage(_ctx, (uint)tileInfo.NTilesX, (uint)tileInfo.NTilesY, Format.R16G16B16A16Sint,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                ExecuteAlignmentSearch(pyramid, altPyramid, alignment, tileInfo, 2);
                disposables.Add(alignment);

                // Warp
                Console.WriteLine($"[VulkanComputePipeline] Warping Image {i}...");
                var warpedAlt = new VulkanImage(_ctx, preparedAlt.Width, preparedAlt.Height, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo, pad, pad);

                // Merge (Spatial only)
                Console.WriteLine($"[VulkanComputePipeline] Merging Image {i}...");
                var expDiff = (float)(refImage.ExposureBias - altImage.ExposureBias);
                ExecuteMerge(preparedTexture, warpedAlt, weightAccum!, pixelAccum!, refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd, expDiff);

                // Cleanup Alt Pyramid
                foreach (var p in altPyramid)
                {
                    if (p != preparedAlt)
                    {
                        p.Dispose();
                    }
                }
                warpedAlt.Dispose();
            }

            // Cleanup pyramid levels
            for (var i = 1; i < pyramid.Count; i++)
            {
                disposables.Add(pyramid[i]);
            }

            // DEBUG: Dump after all merges complete
            _debugHelper.DumpTexture(pixelAccum!, "step_3_merge_accum_spatial", refImage, outWidth, outHeight, pad);

            // Normalize: result = pixelAccum / weightAccum
            var pixAcc = pixelAccum!.GetData<float>();
            var wAcc = weightAccum!.GetData<float>();
            floatData = new float[pixAcc.Length];
            for (var i = 0; i < pixAcc.Length; i++)
            {
                floatData[i] = wAcc[i] > 0.0001f ? pixAcc[i] / wAcc[i] : pixAcc[i];
            }
        }

        // --- Exposure Correction ---
        if (options.ExposureControl != ExposureControlOption.Off)
        {
            Console.WriteLine("[VulkanComputePipeline] Uploading for Exposure Correction...");
            using var exposureTexture = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            exposureTexture.SetData(floatData);
            ExecuteExposureCorrection(exposureTexture, options.ExposureControl, refImage);
            floatData = exposureTexture.GetData<float>();

            _debugHelper.DumpTexture(exposureTexture, "step_6_exposure", refImage, outWidth, outHeight, pad);
        }

        // 7. Convert back to RawImage (Crop back to original size)
        Console.WriteLine("[VulkanComputePipeline] Converting to Output...");
        var outputImage = new RawImage
        {
            Width = width,
            Height = height,
            Data = new ushort[width * height],
            MosaicPatternWidth = refImage.MosaicPatternWidth,
            WhiteLevel = refImage.WhiteLevel,
            BlackLevel = refImage.BlackLevel,
            ExposureBias = refImage.ExposureBias,
            IsoExposureTime = refImage.IsoExposureTime,
            ColorFactors = refImage.ColorFactors,
            SourcePath = refImage.SourcePath,
            CfaPattern = refImage.CfaPattern,
            ColorMatrix1 = refImage.ColorMatrix1,
            ColorMatrix2 = refImage.ColorMatrix2,
            CalibrationIlluminant1 = refImage.CalibrationIlluminant1,
            CalibrationIlluminant2 = refImage.CalibrationIlluminant2,
            AsShotNeutral = refImage.AsShotNeutral,
            CameraMake = refImage.CameraMake,
            CameraModel = refImage.CameraModel,
            IsBayerData = refImage.IsBayerData
        };

        var factor16Bit = 1.0f;
        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            float maxVal = refImage.WhiteLevel;
            factor16Bit = (float)Math.Pow(2.0, 16.0 - Math.Ceiling(Math.Log2(maxVal)));
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var srcIdx = (y + pad) * outWidth + (x + pad);
                var dstIdx = y * width + x;
                var val = floatData[srcIdx] * factor16Bit;
                outputImage.Data[dstIdx] = (ushort)Math.Clamp(val, 0, 65535);
            }
        }

        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            outputImage.WhiteLevel = (int)(refImage.WhiteLevel * factor16Bit);
            if (outputImage.WhiteLevel > 65535)
            {
                outputImage.WhiteLevel = 65535;
            }
        }

        foreach (var d in disposables)
        {
            d.Dispose();
        }

        return outputImage;
    }

    private void EnsurePreparePipeline()
    {
        if (_kernelPrepareBayer is not null)
        {
            return;
        }

        _prepareLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.PrepareLayout);
        _kernelPrepareBayer = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.PrepareBayer, _prepareLayout);
    }

    private void EnsureConversionPipeline()
    {
        if (_kernelConvertToRgba is not null)
        {
            return;
        }

        _conversionLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.ConversionLayout);
        _kernelConvertToRgba = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ConvertToRgba, _conversionLayout);
        _kernelConvertToBayer = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.ConvertToBayer, _conversionLayout);
    }

    private void ExecuteMergeFrequency(VulkanImage refFt, VulkanImage refPyramid0, VulkanImage aligned, VulkanImage weightAccum, VulkanImage pixelAccumFt,
        float whiteLevel, float blackLevel, double noiseReduction, float noiseSd, float exposureDiff, int tileSize, int mosaicPatternWidth, int uniformExposure,
        VulkanImage? totalMismatchTexture = null, int totalImageCount = 1, double exposureCorrRatio = 1.0)
        => _frequencyMergePipeline.ExecuteMergeFrequency(refFt, refPyramid0, aligned, weightAccum, pixelAccumFt, whiteLevel, blackLevel, noiseReduction, noiseSd, exposureDiff, tileSize, mosaicPatternWidth, uniformExposure, totalMismatchTexture, totalImageCount, exposureCorrRatio);

    private void ExecuteBackwardFft(VulkanImage inputFt, VulkanImage outputSpatial, int numTextures, int tileSize)
        => _frequencyMergePipeline.ExecuteBackwardFft(inputFt, outputSpatial, numTextures, tileSize);

    private List<ValidationResult> RunFftRoundTripValidation(VulkanImage rgbaInput, int tileSize)
        => _frequencyMergePipeline.RunFftRoundTripValidation(rgbaInput, tileSize);

    private void ExecuteForwardFft(VulkanImage input, VulkanImage output, int tileSize, int width, int height)
        => _frequencyMergePipeline.ExecuteForwardFft(input, output, tileSize, width, height);

    private void ExecuteConvertToRgba(VulkanImage bayerInput, VulkanImage rgbaOutput, int[] cfaPattern, int cropX = 0, int cropY = 0)
    {
        _conversionHelper.ConvertToRgba(bayerInput, rgbaOutput, cfaPattern, _kernelConvertToRgba!, _conversionLayout, cropX, cropY);
    }

    private void ExecuteConvertToBayer(VulkanImage rgbaInput, VulkanImage bayerOutput, int[] cfaPattern)
    {
        _conversionHelper.ConvertToBayer(rgbaInput, bayerOutput, cfaPattern, _kernelConvertToBayer!, _conversionLayout);
    }

    private void ExecuteCalculateRms(VulkanImage rgbaInput, VulkanImage rmsOutput, int nTilesX, int nTilesY, int tileSize)
        => _frequencyMergePipeline.ExecuteCalculateRms(rgbaInput, rmsOutput, nTilesX, nTilesY, tileSize);

    private void ExecuteDeconvoluteFrequency(VulkanImage finalTextureFt, VulkanImage mismatchTexture, int nTilesX, int nTilesY, int tileSize)
        => _frequencyMergePipeline.ExecuteDeconvoluteFrequency(finalTextureFt, mismatchTexture, nTilesX, nTilesY, tileSize);

    private void ExecuteReduceArtifacts(VulkanImage outputTexture, VulkanImage refTexture, int nTilesX, int nTilesY, int tileSize, int[] blackLevel)
        => _frequencyMergePipeline.ExecuteReduceArtifacts(outputTexture, refTexture, nTilesX, nTilesY, tileSize, blackLevel);

    private void ExecuteAvgPool(VulkanImage input, VulkanImage output, int scale, RawImage rawInfo, bool normalize = false)
        => _alignmentPipeline.ExecuteAvgPool(input, output, scale, rawInfo, normalize);

    private void ExecuteAlignmentSearch(List<VulkanImage> refPyramid, List<VulkanImage> compPyramid, VulkanImage alignmentOut, TileInfo baseTileInfo, int scale, bool uniformExposure = true)
        => _alignmentPipeline.ExecuteAlignmentSearch(refPyramid, compPyramid, alignmentOut, baseTileInfo, scale, uniformExposure);

    private void ExecuteWarp(VulkanImage altImage, VulkanImage output, VulkanImage alignment, TileInfo tileInfo, int padLeft = 0, int padTop = 0)
        => _alignmentPipeline.ExecuteWarp(altImage, output, alignment, tileInfo, padLeft, padTop);

    private void ExecuteMerge(VulkanImage referenceFrame, VulkanImage warpedFrame, VulkanImage weightAccum, VulkanImage pixelAccum, float whiteLevel, float blackLevel, double noiseReduction, float noiseSd, float exposureDiff)
        => _spatialMergePipeline.ExecuteMerge(referenceFrame, warpedFrame, weightAccum, pixelAccum, whiteLevel, blackLevel, noiseReduction, noiseSd, exposureDiff);

    private void ExecutePrepare(VulkanImage input, VulkanImage output, RawImage rawInfo, int padLeft, int padTop, int exposureDiff = 0)
    {
        _conversionHelper.Prepare(input, output, rawInfo, padLeft, padTop, _kernelPrepareBayer!, _prepareLayout, exposureDiff);
    }

    private void ExecuteBlur(VulkanImage input, VulkanImage output, int kernelSize, int mosaicPatternWidth, VulkanImage? intermediate = null)
        => _exposurePipeline.ExecuteBlur(input, output, kernelSize, mosaicPatternWidth, intermediate);

    private void ExecuteMaxReduction(VulkanImage input, VulkanBuffer outBuffer, int mosaicPatternWidth)
        => _exposurePipeline.ExecuteMaxReduction(input, outBuffer, mosaicPatternWidth);

    private void ExecuteExposureCorrection(VulkanImage image, ExposureControlOption option, RawImage metadata)
        => _exposurePipeline.ExecuteExposureCorrection(image, option, metadata);

    private float ExecuteNoiseEstimationGpu(VulkanImage inputTexture, int mosaicPatternWidth)
        => _exposurePipeline.ExecuteNoiseEstimationGpu(inputTexture, mosaicPatternWidth);

    private float CalculateRobustness(double noiseReduction)
    {
        return (float)noiseReduction;
    }

    public void Dispose()
    {
        _descriptors.Dispose();

        // Layouts managed by this class
        if (_prepareLayout.Handle != 0)
        {
            _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _prepareLayout, null);
        }

        if (_conversionLayout.Handle != 0)
        {
            _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _conversionLayout, null);
        }

        // Kernels managed by this class
        _kernelPrepareBayer?.Dispose();
        _kernelConvertToRgba?.Dispose();
        _kernelConvertToBayer?.Dispose();
    }
}
