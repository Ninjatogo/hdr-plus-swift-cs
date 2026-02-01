using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using BurstPhoto.Rendering.Debug;
using BurstPhoto.Rendering.Pipelines;
using BurstPhoto.Rendering.Utilities;
using BurstPhoto.Rendering.Validation;
using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering.Implementations;

/// <summary>
/// GPU-accelerated compute pipeline for HDR+ burst photo processing using Vulkan.
/// </summary>
/// <remarks>
/// This pipeline implements two merging algorithms:
/// <list type="bullet">
///   <item><description><strong>Spatial (Fast)</strong>: Weighted pixel averaging in the spatial domain</description></item>
///   <item><description><strong>Frequency (HigherQuality)</strong>: FFT-based merging with 4 iterations for improved noise reduction</description></item>
/// </list>
/// The pipeline performs the following stages:
/// <list type="number">
///   <item><description>Upload and prepare raw Bayer data</description></item>
///   <item><description>Build alignment pyramids for coarse-to-fine motion estimation</description></item>
///   <item><description>Align and warp comparison frames to the reference</description></item>
///   <item><description>Merge aligned frames using the selected algorithm</description></item>
///   <item><description>Apply optional exposure correction</description></item>
///   <item><description>Convert back to 16-bit output</description></item>
/// </list>
/// </remarks>
public class VulkanComputePipeline : IComputePipeline
{
    #region Constants

    /// <summary>
    /// Number of pyramid levels used for coarse-to-fine alignment.
    /// Level 0 is half the prepared image size, with each subsequent level halved again.
    /// </summary>
    private const int PyramidLevelCount = 4;

    /// <summary>
    /// Number of iterations for frequency domain merging.
    /// Each iteration uses a different tile offset pattern to reduce blocking artifacts.
    /// </summary>
    private const int FrequencyMergeIterationCount = 4;

    /// <summary>
    /// Tile size (in pixels) used for FFT-based frequency domain merging.
    /// This is hardcoded to match the Swift implementation (tile_size_merge = 8).
    /// </summary>
    private const int FrequencyMergeTileSize = 8;

    /// <summary>
    /// Maximum pixel value for 16-bit output (2^16 - 1).
    /// </summary>
    private const int MaxPixelValue16Bit = 65535;

    #endregion

    #region Private Fields

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

    // Performance profiler for timing pipeline stages
    private readonly PerformanceProfiler _profiler;

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

    /// <summary>
    /// Stores FFT validation test results when <see cref="EnableFftValidation"/> is enabled.
    /// </summary>
    private List<ValidationResult> _validationResults = [];

    /// <summary>
    /// Flag to track if shader pipelines have been initialized.
    /// </summary>
    private bool _pipelinesInitialized;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="VulkanComputePipeline"/> class.
    /// </summary>
    /// <param name="ctx">The Vulkan context providing device and command pool access.</param>
    public VulkanComputePipeline(VulkanContext ctx)
    {
        _ctx = ctx;
        _compiler = new VulkanShaderCompiler();
        // Start with a modest pool size - it will be dynamically resized based on workload
        _descriptors = new VulkanDescriptorManager(_ctx, maxSets: 200);
        _kernelManager = new VulkanKernelManager(_ctx, _descriptors);
        _alignmentPipeline = new AlignmentPipeline(_ctx, _descriptors, _kernelManager);
        _exposurePipeline = new ExposurePipeline(_ctx, _descriptors, _kernelManager);
        _spatialMergePipeline = new SpatialMergePipeline(_ctx, _descriptors, _kernelManager);
        _frequencyMergePipeline = new FrequencyMergePipeline(_ctx, _descriptors, _kernelManager);
        _debugHelper = new PipelineDebugHelper();
        _debugInspector = new PipelineDebugInspector();
        _profiler = new PerformanceProfiler();
        _textureUtils = new TextureUtilities(_ctx, _descriptors, _kernelManager);
        _conversionHelper = new TextureConversionHelper(_ctx, _descriptors, _textureUtils);
    }

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets or sets whether FFT mathematical validation tests should be run during processing.
    /// </summary>
    /// <remarks>
    /// When enabled, performs round-trip FFT validation on the first iteration to verify
    /// that the FFT shaders are producing mathematically correct results. This is useful
    /// for debugging but adds processing overhead.
    /// </remarks>
    public bool EnableFftValidation { get; set; }

    #endregion

    #region Pipeline Initialization

    /// <summary>
    /// Initializes all shader pipelines upfront to avoid per-execution overhead.
    /// </summary>
    /// <param name="isFrequencyMode">True if using frequency domain merging, which requires additional conversion pipelines.</param>
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

    public async Task<RawImage> ProcessAsync(RenderingInput input, ProcessingOptions options, ProcessingProgress progress, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[VulkanComputePipeline] Starting processing...");

        var isFrequency = options.Merging == MergingAlgorithm.HigherQuality;

        // Initialize all pipelines upfront
        InitializePipelines(isFrequency);

        // Calculate required pool size based on workload and ensure capacity
        var requiredPoolSize = VulkanDescriptorManager.CalculateRequiredPoolSize(
            input.Images.Count, isFrequency);
        _descriptors.EnsureCapacity(requiredPoolSize);

        // Reset descriptor pool to free any previously allocated descriptor sets
        _descriptors.ResetPool();

        // Check for cancellation before heavy processing
        cancellationToken.ThrowIfCancellationRequested();

        // Enable debug dump if requested
        _debugHelper.Enabled = options.EnableDebugDump;
        _debugInspector.Enabled = options.EnableDebugDump;

        // Enable performance profiling if requested
        _profiler.Enabled = options.EnableProfiling;
        _profiler.Reset();
        _profiler.StartTotal();

        if (_debugHelper.Enabled)
        {
            Console.WriteLine("[VulkanComputePipeline] DEBUG DUMP ENABLED - intermediate DNGs will be saved to DebugOutput/");
        }

        if (_profiler.Enabled)
        {
            Console.WriteLine("[VulkanComputePipeline] PROFILING ENABLED - stage timings will be reported");
        }

        // Enable FFT validation if requested (from options, not hardcoded)
        EnableFftValidation = options.EnableFftValidation;
        _validationResults.Clear();

        if (EnableFftValidation)
        {
            Console.WriteLine("[VulkanComputePipeline] FFT VALIDATION ENABLED - mathematical tests will be run on each stage");
        }

        // Setup Reference Frame
        var referenceImage = input.Images[input.ReferenceFrameIndex];
        var imageWidth = referenceImage.Width;
        var imageHeight = referenceImage.Height;

        // Calculate Padded Dimensions for Alignment
        // The padding ensures tiles align properly at image boundaries
        var alignmentTileSize = ProcessingOptions.GetTileSizePixels(options.TileSize);
        int paddingAmount;
        int paddedWidth;
        int paddedHeight;

        if (isFrequency)
        {
            paddingAmount = 0;
            // Ensure dimensions are multiples of tile size for FFT alignment
            paddedWidth = ((imageWidth + alignmentTileSize - 1) / alignmentTileSize) * alignmentTileSize;
            paddedHeight = ((imageHeight + alignmentTileSize - 1) / alignmentTileSize) * alignmentTileSize;
        }
        else
        {
            // Spatial mode: add half-tile padding on each side for centered alignment
            paddingAmount = alignmentTileSize / 2;
            paddedWidth = imageWidth + alignmentTileSize;
            paddedHeight = imageHeight + alignmentTileSize;

            // Ensure even dimensions for Bayer pattern compatibility
            if (paddedWidth % 2 != 0) paddedWidth++;
            if (paddedHeight % 2 != 0) paddedHeight++;
        }

        Console.WriteLine($"[VulkanComputePipeline] Input: {imageWidth}x{imageHeight}, Padded: {paddedWidth}x{paddedHeight}, Mode: {options.Merging}");

        // Allocate GPU Resources
        using var rawBayerTexture = new VulkanImage(_ctx, (uint)imageWidth, (uint)imageHeight, Format.R16Uint,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);

        // Padded float texture for processing (converts 16-bit Bayer to 32-bit float with padding)
        using var preparedTexture = new VulkanImage(_ctx, (uint)paddedWidth, (uint)paddedHeight, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

        // Upload Reference Frame (once, before iteration loop)
        Console.WriteLine("[VulkanComputePipeline] Uploading Reference Frame...");
        using (_profiler.MeasureStage("Upload"))
        {
            rawBayerTexture.SetData(referenceImage.Data);
        }

        // Execute Prepare Pass - converts raw Bayer to padded float format
        Console.WriteLine("[VulkanComputePipeline] Executing Prepare Pass...");
        using (_profiler.MeasureStage("Prepare"))
        {
            ExecutePrepare(rawBayerTexture, preparedTexture, referenceImage, paddingAmount, paddingAmount);
        }

        // DEBUG: Dump after Prepare
        _debugHelper.DumpTexture(preparedTexture, "step_1_prepare", referenceImage, paddedWidth, paddedHeight, paddingAmount);

        progress.Update(20_000_000, "Building alignment pyramid...");

        // Build Alignment Pyramid for coarse-to-fine motion estimation
        // Level 0 is half the prepared image size; each subsequent level is halved again
        Console.WriteLine("[VulkanComputePipeline] Generating Alignment Pyramid...");
        _profiler.BeginStage("BuildPyramid");
        var referencePyramid = new List<VulkanImage>();

        // Level 0: Downsampled from preparedTexture by factor of 2
        var pyramidLevel0Width = (int)preparedTexture.Width / 2;
        var pyramidLevel0Height = (int)preparedTexture.Height / 2;
        // Ensure even dimensions for subsequent downsampling
        if (pyramidLevel0Width % 2 != 0) pyramidLevel0Width++;
        if (pyramidLevel0Height % 2 != 0) pyramidLevel0Height++;

        var pyramidLevel0 = new VulkanImage(_ctx, (uint)pyramidLevel0Width, (uint)pyramidLevel0Height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
        ExecuteAvgPool(preparedTexture, pyramidLevel0, scale: 2, referenceImage);
        referencePyramid.Add(pyramidLevel0);

        var currentPyramidWidth = pyramidLevel0Width;
        var currentPyramidHeight = pyramidLevel0Height;

        // Create remaining pyramid levels (levels 1 through PyramidLevelCount-1)
        for (var pyramidLevel = 1; pyramidLevel < PyramidLevelCount; pyramidLevel++)
        {
            var nextLevelWidth = currentPyramidWidth / 2;
            if (nextLevelWidth % 2 != 0) nextLevelWidth++;
            var nextLevelHeight = currentPyramidHeight / 2;
            if (nextLevelHeight % 2 != 0) nextLevelHeight++;

            var pyramidLevelImage = new VulkanImage(_ctx, (uint)nextLevelWidth, (uint)nextLevelHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

            ExecuteAvgPool(referencePyramid[pyramidLevel - 1], pyramidLevelImage, scale: 2, referenceImage);

            referencePyramid.Add(pyramidLevelImage);
            currentPyramidWidth = nextLevelWidth;
            currentPyramidHeight = nextLevelHeight;
        }
        _profiler.EndStage("BuildPyramid");

        var disposableResources = new List<IDisposable>();

        Console.WriteLine("[VulkanComputePipeline] Starting Alignment Search...");
        // Calculate TileInfo based on Level 0 dimensions (half resolution of input)
        var alignmentTileInfo = TileInfo.Calculate(
            imageWidth / 2,
            imageHeight / 2,
            ProcessingOptions.GetTileSizePixels(options.TileSize),
            ProcessingOptions.GetSearchDistancePixels(options.SearchDistance));

        // Accumulators for spatial mode (frequency mode uses its own accumulator)
        VulkanImage? pixelAccumulator;
        VulkanImage? weightAccumulator;

        float estimatedNoiseStandardDeviation;
        float[] outputFloatData;

        if (isFrequency)
        {
            Console.WriteLine($"[VulkanComputePipeline] === FREQUENCY DOMAIN MERGE ({FrequencyMergeIterationCount}-ITERATION) ===");
            _profiler.BeginStage("FrequencyMerge");

            // Calculate alignment padding to ensure tiles align with mosaic pattern boundaries
            // This formula comes from frequency.swift lines 80-97
            var mosaicDownscaleFactors = new[] { referenceImage.MosaicPatternWidth, 2, 2, 2 };
            var combinedTileFactor = alignmentTileSize * mosaicDownscaleFactors.Aggregate(1, (product, factor) => product * factor);

            // Calculate horizontal alignment padding
            var alignmentPaddingX = (int)Math.Ceiling((float)(imageWidth + FrequencyMergeTileSize) / combinedTileFactor);
            alignmentPaddingX = (alignmentPaddingX * combinedTileFactor - imageWidth - FrequencyMergeTileSize) / 2;

            // Calculate vertical alignment padding
            var alignmentPaddingY = (int)Math.Ceiling((float)(imageHeight + FrequencyMergeTileSize) / combinedTileFactor);
            alignmentPaddingY = (alignmentPaddingY * combinedTileFactor - imageHeight - FrequencyMergeTileSize) / 2;

            // Calculate merge crop amounts (align to FFT tile boundaries)
            var mergeCropX = (int)Math.Floor((float)alignmentPaddingX / (2 * FrequencyMergeTileSize));
            mergeCropX = mergeCropX * 2 * FrequencyMergeTileSize;
            var mergeCropY = (int)Math.Floor((float)alignmentPaddingY / (2 * FrequencyMergeTileSize));
            mergeCropY = mergeCropY * 2 * FrequencyMergeTileSize;

            // Final merge padding is alignment padding minus crop
            var mergePaddingX = alignmentPaddingX - mergeCropX;
            var mergePaddingY = alignmentPaddingY - mergeCropY;

            Console.WriteLine($"[VulkanComputePipeline] Padding: Align=({alignmentPaddingX},{alignmentPaddingY}), Merge=({mergePaddingX},{mergePaddingY}), Crop=({mergeCropX},{mergeCropY})");

            // Calculate exposure correction factors for bracketed exposure handling
            var exposureCorrectionSum1 = 0.0;
            var exposureCorrectionSum2 = 0.0;
            var referenceExposureBias = referenceImage.ExposureBias;
            var referenceIsoExposureProduct = referenceImage.IsoSpeedExposureTimeProduct;

            var hasExposureBiasMetadata = input.Images.Any(img => img.ExposureBias != 0);

            for (var imageIndex = 0; imageIndex < input.Images.Count; imageIndex++)
            {
                var currentImage = input.Images[imageIndex];
                double exposureFactor;

                if (hasExposureBiasMetadata)
                {
                    // Use exposure bias from metadata (stored in hundredths of an EV)
                    exposureFactor = Math.Pow(2.0, (currentImage.ExposureBias - referenceExposureBias) / 100.0);
                }
                else
                {
                    // Fall back to ISO * exposure time product
                    exposureFactor = referenceIsoExposureProduct > 0
                        ? currentImage.IsoSpeedExposureTimeProduct / referenceIsoExposureProduct
                        : 1.0;
                }

                exposureCorrectionSum1 += 0.5 + 0.5 / exposureFactor;
                exposureCorrectionSum2 += Math.Min(4.0, exposureFactor);
            }
            var averageExposureCorrection1 = exposureCorrectionSum1 / input.Images.Count;
            var averageExposureCorrection2 = exposureCorrectionSum2 / input.Images.Count;

            // Allocate final accumulator with full alignment padding
            var accumulatorWidth = imageWidth + 2 * alignmentPaddingX;
            var accumulatorHeight = imageHeight + 2 * alignmentPaddingY;
            var finalAccumulator = new VulkanImage(_ctx, (uint)accumulatorWidth, (uint)accumulatorHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            _textureUtils.FillWithZeros(finalAccumulator);
            disposableResources.Add(finalAccumulator);

            // Frequency domain merging uses multiple iterations with offset tile patterns
            // This reduces blocking artifacts from the FFT tile boundaries
            for (var iterationIndex = 1; iterationIndex <= FrequencyMergeIterationCount; iterationIndex++)
            {
                // Check for cancellation at start of each iteration
                cancellationToken.ThrowIfCancellationRequested();

                Console.WriteLine($"\n[VulkanComputePipeline] === ITERATION {iterationIndex}/{FrequencyMergeIterationCount} ===");

                // Report progress for this iteration (25-85% range split across iterations)
                var iterationProgressBase = 25_000_000 + ((iterationIndex - 1) * 15_000_000);
                progress.Update(iterationProgressBase, $"Merging pass {iterationIndex} of {FrequencyMergeIterationCount}...");

                // Calculate tile offset shifts for this iteration
                // The pattern cycles through 4 positions to cover all tile boundary cases:
                // Iteration 1: shift right, shift top
                // Iteration 2: shift left, shift top
                // Iteration 3: shift right, shift bottom
                // Iteration 4: shift left, shift bottom
                var tileShiftLeft = (iterationIndex % 2 == 0) ? FrequencyMergeTileSize : 0;
                var tileShiftRight = (iterationIndex % 2 == 1) ? FrequencyMergeTileSize : 0;
                var tileShiftTop = (iterationIndex < 3) ? FrequencyMergeTileSize : 0;
                var tileShiftBottom = (iterationIndex >= 3) ? FrequencyMergeTileSize : 0;

                var iterationPaddingLeft = alignmentPaddingX + tileShiftLeft;
                var iterationPaddingRight = alignmentPaddingX + tileShiftRight;
                var iterationPaddingTop = alignmentPaddingY + tileShiftTop;
                var iterationPaddingBottom = alignmentPaddingY + tileShiftBottom;

                var iterationOutputWidth = imageWidth + iterationPaddingLeft + iterationPaddingRight;
                var iterationOutputHeight = imageHeight + iterationPaddingTop + iterationPaddingBottom;

                // Prepare reference with iteration-specific padding
                using var iterationPreparedReference = new VulkanImage(_ctx, (uint)iterationOutputWidth, (uint)iterationOutputHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                // NOTE: rawBayerTexture already contains referenceImage.Data - no need to re-upload
                ExecutePrepare(rawBayerTexture, iterationPreparedReference, referenceImage, iterationPaddingLeft, iterationPaddingTop);

                // Debug inspection (only runs if enabled)
                _debugInspector.InspectPreparedTexture(iterationPreparedReference, iterationIndex, iterationPaddingLeft, iterationPaddingTop, imageWidth, imageHeight);
                _debugHelper.DumpTexture(iterationPreparedReference, $"step_1b_iter{iterationIndex}_prepared_ref_bayer", referenceImage, iterationOutputWidth, iterationOutputHeight, 0);

                // Calculate RGBA dimensions (Bayer demosaicing halves dimensions)
                var rgbaTextureWidth = (iterationOutputWidth - 2 * mergeCropX) / 2;
                var rgbaTextureHeight = (iterationOutputHeight - 2 * mergeCropY) / 2;
                var fourierTransformWidth = rgbaTextureWidth * 2;  // Complex numbers stored as pairs
                var fourierTransformHeight = rgbaTextureHeight;

                // Convert reference Bayer -> RGBA (demosaic)
                using var referenceRgbaTexture = new VulkanImage(_ctx, (uint)rgbaTextureWidth, (uint)rgbaTextureHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteConvertToRgba(iterationPreparedReference, referenceRgbaTexture, referenceImage.CfaPattern, mergeCropX, mergeCropY);

                _debugInspector.InspectRgbaTexture(referenceRgbaTexture, iterationIndex, "After convert_to_rgba");
                _debugHelper.DumpRgbaTexture(referenceRgbaTexture, $"step_2_iter{iterationIndex}_ref_rgba", referenceImage);

                // FFT VALIDATION: Run round-trip test on first iteration only
                if (EnableFftValidation && iterationIndex == 1)
                {
                    Console.WriteLine("\n[VulkanComputePipeline] Running FFT validation (first iteration)...");
                    var fftValidationResults = RunFftRoundTripValidation(referenceRgbaTexture, FrequencyMergeTileSize);
                    _validationResults.AddRange(fftValidationResults);

                    var roundTripResult = fftValidationResults.FirstOrDefault(r => r.TestName.Contains("Round-Trip"));
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

                // Build reference pyramid for this iteration
                var iterationReferencePyramid = new List<VulkanImage>();
                var iterationPyramidLevel0Width = (int)iterationPreparedReference.Width / 2;
                var iterationPyramidLevel0Height = (int)iterationPreparedReference.Height / 2;

                if (iterationPyramidLevel0Width % 2 != 0) iterationPyramidLevel0Width++;
                if (iterationPyramidLevel0Height % 2 != 0) iterationPyramidLevel0Height++;

                var iterationRefLevel0 = new VulkanImage(_ctx, (uint)iterationPyramidLevel0Width, (uint)iterationPyramidLevel0Height, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteAvgPool(iterationPreparedReference, iterationRefLevel0, scale: 2, referenceImage);
                iterationReferencePyramid.Add(iterationRefLevel0);

                int iterPyramidWidth = iterationPyramidLevel0Width, iterPyramidHeight = iterationPyramidLevel0Height;
                for (var pyramidLevel = 1; pyramidLevel < PyramidLevelCount; pyramidLevel++)
                {
                    var nextPyramidWidth = iterPyramidWidth / 2;
                    if (nextPyramidWidth % 2 != 0) nextPyramidWidth++;
                    var nextPyramidHeight = iterPyramidHeight / 2;
                    if (nextPyramidHeight % 2 != 0) nextPyramidHeight++;
                    var blurredPreviousLevel = new VulkanImage(_ctx, (uint)iterPyramidWidth, (uint)iterPyramidHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    disposableResources.Add(blurredPreviousLevel);
                    ExecuteBlur(iterationReferencePyramid[pyramidLevel - 1], blurredPreviousLevel, kernelSize: 2, mosaicPatternWidth: 1);
                    var pyramidLevelImage = new VulkanImage(_ctx, (uint)nextPyramidWidth, (uint)nextPyramidHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(blurredPreviousLevel, pyramidLevelImage, scale: 2, referenceImage);
                    iterationReferencePyramid.Add(pyramidLevelImage);
                    iterPyramidWidth = nextPyramidWidth;
                    iterPyramidHeight = nextPyramidHeight;
                }

                var iterationTileInfo = TileInfo.Calculate(iterationPyramidLevel0Width, iterationPyramidLevel0Height, alignmentTileSize, ProcessingOptions.GetSearchDistancePixels(options.SearchDistance));

                // Calculate tile grid dimensions for RMS and mismatch tracking
                var tileGridCountX = (iterationOutputWidth - 2 * mergeCropX) / (2 * FrequencyMergeTileSize);
                var tileGridCountY = (iterationOutputHeight - 2 * mergeCropY) / (2 * FrequencyMergeTileSize);

                using var rmsTexture = new VulkanImage(_ctx, (uint)tileGridCountX, (uint)tileGridCountY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                ExecuteCalculateRms(referenceRgbaTexture, rmsTexture, tileGridCountX, tileGridCountY, FrequencyMergeTileSize);

                // Initialize total mismatch texture for accumulating alignment errors across frames
                using var totalMismatchTexture = new VulkanImage(_ctx, (uint)tileGridCountX, (uint)tileGridCountY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
                _textureUtils.FillWithZeros(totalMismatchTexture);

                // Forward FFT on reference to transform to frequency domain
                using var referenceFourierTransform = new VulkanImage(_ctx, (uint)fourierTransformWidth, (uint)fourierTransformHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteForwardFft(referenceRgbaTexture, referenceFourierTransform, FrequencyMergeTileSize, rgbaTextureWidth, rgbaTextureHeight);

                _debugInspector.InspectFftOutput(referenceFourierTransform, iterationIndex, "After forward_fft");

                // Initialize frequency domain accumulator (starts with reference FFT)
                using var accumulatedFourierTransform = new VulkanImage(_ctx, (uint)fourierTransformWidth, (uint)fourierTransformHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
                _textureUtils.CopyImage(referenceFourierTransform, accumulatedFourierTransform, fourierTransformWidth, fourierTransformHeight);

                // Estimate noise for this iteration
                estimatedNoiseStandardDeviation = ExecuteNoiseEstimationGpu(iterationPreparedReference, referenceImage.MosaicPatternWidth);

                // Process each comparison frame (non-reference frames)
                for (var comparisonFrameIndex = 0; comparisonFrameIndex < input.Images.Count; comparisonFrameIndex++)
                {
                    if (comparisonFrameIndex == input.ReferenceFrameIndex)
                    {
                        continue;
                    }

                    // Check for cancellation at start of each comparison
                    cancellationToken.ThrowIfCancellationRequested();

                    var comparisonImage = input.Images[comparisonFrameIndex];

                    // Report progress for each comparison frame
                    var comparisonProgress = iterationProgressBase + (comparisonFrameIndex * 15_000_000 / Math.Max(1, input.Images.Count - 1));
                    progress.Update(comparisonProgress, $"Pass {iterationIndex}/{FrequencyMergeIterationCount}: Aligning frame {comparisonFrameIndex + 1}/{input.Images.Count}...");

                    // Prepare comparison frame with iteration-specific padding
                    using var rawComparisonBayer = new VulkanImage(_ctx, (uint)imageWidth, (uint)imageHeight, Format.R16Uint,
                        ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                    rawComparisonBayer.SetData(comparisonImage.Data);

                    using var preparedComparisonFrame = new VulkanImage(_ctx, (uint)iterationOutputWidth, (uint)iterationOutputHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                    // Calculate exposure difference for prepare pass (in hundredths of an EV)
                    int prepareExposureDifference;
                    if (hasExposureBiasMetadata)
                    {
                        prepareExposureDifference = referenceImage.ExposureBias - comparisonImage.ExposureBias;
                    }
                    else
                    {
                        var exposureRatio = referenceImage.IsoSpeedExposureTimeProduct / Math.Max(comparisonImage.IsoSpeedExposureTimeProduct, 0.0001);
                        prepareExposureDifference = (int)Math.Round(Math.Log2(exposureRatio) * 100.0);
                    }
                    ExecutePrepare(rawComparisonBayer, preparedComparisonFrame, comparisonImage, iterationPaddingLeft, iterationPaddingTop, prepareExposureDifference);

                    _debugHelper.DumpTexture(preparedComparisonFrame, $"step_1c_iter{iterationIndex}_prepared_comp{comparisonFrameIndex}_bayer", referenceImage, iterationOutputWidth, iterationOutputHeight, 0);

                    // Build comparison pyramid for alignment
                    var comparisonPyramid = new List<VulkanImage>();
                    var comparisonPyramidLevel0Width = (int)preparedComparisonFrame.Width / 2;
                    var comparisonPyramidLevel0Height = (int)preparedComparisonFrame.Height / 2;

                    if (comparisonPyramidLevel0Width % 2 != 0) comparisonPyramidLevel0Width++;
                    if (comparisonPyramidLevel0Height % 2 != 0) comparisonPyramidLevel0Height++;

                    var comparisonLevel0 = new VulkanImage(_ctx, (uint)comparisonPyramidLevel0Width, (uint)comparisonPyramidLevel0Height, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(preparedComparisonFrame, comparisonLevel0, scale: 2, comparisonImage);
                    comparisonPyramid.Add(comparisonLevel0);

                    var compPyramidWidth = comparisonPyramidLevel0Width;
                    var compPyramidHeight = comparisonPyramidLevel0Height;
                    for (var compPyramidLevel = 1; compPyramidLevel < PyramidLevelCount; compPyramidLevel++)
                    {
                        var nextCompPyramidWidth = compPyramidWidth / 2;
                        if (nextCompPyramidWidth % 2 != 0) nextCompPyramidWidth++;
                        var nextCompPyramidHeight = compPyramidHeight / 2;
                        if (nextCompPyramidHeight % 2 != 0) nextCompPyramidHeight++;
                        using var blurredPreviousComparison = new VulkanImage(_ctx, (uint)compPyramidWidth, (uint)compPyramidHeight, Format.R32Sfloat,
                            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteBlur(comparisonPyramid[compPyramidLevel - 1], blurredPreviousComparison, kernelSize: 2, mosaicPatternWidth: 1);
                        var comparisonPyramidLevel = new VulkanImage(_ctx, (uint)nextCompPyramidWidth, (uint)nextCompPyramidHeight, Format.R32Sfloat,
                            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteAvgPool(blurredPreviousComparison, comparisonPyramidLevel, scale: 2, comparisonImage);
                        comparisonPyramid.Add(comparisonPyramidLevel);
                        compPyramidWidth = nextCompPyramidWidth;
                        compPyramidHeight = nextCompPyramidHeight;
                    }

                    // Compute alignment vectors between reference and comparison pyramids
                    using var alignmentVectors = new VulkanImage(_ctx, (uint)iterationTileInfo.TileCountX, (uint)iterationTileInfo.TileCountY, Format.R16G16B16A16Sint,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                    var hasUniformExposure = (comparisonImage.ExposureBias == referenceImage.ExposureBias);
                    ExecuteAlignmentSearch(iterationReferencePyramid, comparisonPyramid, alignmentVectors, iterationTileInfo, scale: 2, hasUniformExposure);

                    _debugHelper.DumpAlignment(alignmentVectors, $"step_2a_iter{iterationIndex}_alignment_comp{comparisonFrameIndex}", referenceImage);

                    using var warpedComparisonFrame = new VulkanImage(_ctx, (uint)iterationOutputWidth, (uint)iterationOutputHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                    ExecuteWarp(preparedComparisonFrame, warpedComparisonFrame, alignmentVectors, iterationTileInfo, iterationPaddingLeft, iterationPaddingTop);

                    _debugHelper.DumpTexture(warpedComparisonFrame, $"step_2b_iter{iterationIndex}_warped_comp{comparisonFrameIndex}_bayer", referenceImage, iterationOutputWidth, iterationOutputHeight, 0);

                    using var alignedComparisonRgba = new VulkanImage(_ctx, (uint)rgbaTextureWidth, (uint)rgbaTextureHeight, Format.R32G32B32A32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteConvertToRgba(warpedComparisonFrame, alignedComparisonRgba, referenceImage.CfaPattern, mergeCropX, mergeCropY);

                    _debugHelper.DumpRgbaTexture(alignedComparisonRgba, $"step_3_iter{iterationIndex}_aligned_comp{comparisonFrameIndex}_rgba", referenceImage);

                    // Execute frequency domain merge
                    var isUniformExposureForMerge = hasExposureBiasMetadata
                        ? (comparisonImage.ExposureBias == referenceImage.ExposureBias ? 1 : 0)
                        : (Math.Abs(comparisonImage.IsoSpeedExposureTimeProduct - referenceImage.IsoSpeedExposureTimeProduct) < 0.001f ? 1 : 0);
                    var exposureDifferenceForMerge = (float)(-prepareExposureDifference);
                    ExecuteMergeFrequency(referenceFourierTransform, referenceRgbaTexture, alignedComparisonRgba, null!, accumulatedFourierTransform,
                        referenceImage.WhiteLevel, blackLevel: 0.0f, options.NoiseReduction, estimatedNoiseStandardDeviation, exposureDifferenceForMerge, alignmentTileSize, referenceImage.MosaicPatternWidth, isUniformExposureForMerge,
                        totalMismatchTexture, input.Images.Count, averageExposureCorrection1 / averageExposureCorrection2);

                    // Cleanup comparison pyramid
                    foreach (var pyramidLevel in comparisonPyramid)
                    {
                        if (pyramidLevel != preparedComparisonFrame)
                        {
                            pyramidLevel.Dispose();
                        }
                    }
                }

                // Post-iteration processing: deconvolute, inverse FFT, artifact reduction
                using var iterationOutputRgba = new VulkanImage(_ctx, (uint)rgbaTextureWidth, (uint)rgbaTextureHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                // Use batched execution when debug is disabled for maximum performance
                if (!_debugHelper.Enabled)
                {
                    // OPTIMIZED PATH: Single command buffer for deconvolute + backward FFT + reduce artifacts
                    ExecutePostProcessingBatched(
                        accumulatedFourierTransform, totalMismatchTexture, iterationOutputRgba, referenceRgbaTexture,
                        tileGridCountX, tileGridCountY, FrequencyMergeTileSize, input.Images.Count, referenceImage.BlackLevels, options.SkipReduceArtifacts);
                }
                else
                {
                    // DEBUG PATH: Individual calls for inspection
                    _debugInspector.InspectDeconvolution(accumulatedFourierTransform, iterationIndex, isBefore: true);

                    // Deconvolute with accumulated mismatch
                    ExecuteDeconvoluteFrequency(accumulatedFourierTransform, totalMismatchTexture, tileGridCountX, tileGridCountY, FrequencyMergeTileSize);

                    _debugInspector.InspectDeconvolution(accumulatedFourierTransform, iterationIndex, isBefore: false);

                    // Backward FFT to convert from frequency to spatial domain
                    ExecuteBackwardFft(accumulatedFourierTransform, iterationOutputRgba, input.Images.Count, FrequencyMergeTileSize);

                    _debugInspector.InspectBackwardFftOutput(iterationOutputRgba, iterationIndex, FrequencyMergeTileSize, rgbaTextureWidth, rgbaTextureHeight);
                    _debugHelper.DumpRgbaTexture(iterationOutputRgba, $"step_4_iter{iterationIndex}_merged_before_reduce", referenceImage);

                    // Reduce tile border artifacts from FFT processing
                    if (!options.SkipReduceArtifacts)
                    {
                        ExecuteReduceArtifacts(iterationOutputRgba, referenceRgbaTexture, tileGridCountX, tileGridCountY, FrequencyMergeTileSize, referenceImage.BlackLevels);
                    }

                    _debugHelper.DumpRgbaTexture(iterationOutputRgba, $"step_5_iter{iterationIndex}_merged_after_reduce", referenceImage);
                }

                // Convert RGBA back to Bayer format
                var iterationBayerWidth = rgbaTextureWidth * 2;
                var iterationBayerHeight = rgbaTextureHeight * 2;
                using var iterationOutputBayer = new VulkanImage(_ctx, (uint)iterationBayerWidth, (uint)iterationBayerHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteConvertToBayer(iterationOutputRgba, iterationOutputBayer, referenceImage.CfaPattern);

                _debugInspector.InspectBayerOutput(iterationOutputBayer, iterationIndex);
                _debugHelper.DumpTexture(iterationOutputBayer, $"step_5b_iter{iterationIndex}_bayer_after_convert", referenceImage, iterationBayerWidth, iterationBayerHeight, 0);

                // Calculate crop amounts for accumulator blending
                var iterationCropLeft = mergePaddingX + tileShiftLeft;
                var iterationCropRight = mergePaddingX + tileShiftRight;
                var iterationCropTop = mergePaddingY + tileShiftTop;
                var iterationCropBottom = mergePaddingY + tileShiftBottom;

                // GPU-accelerated accumulation (no CPU round-trip!)
                _textureUtils.AccumulateCroppedRegionGpu(
                    iterationOutputBayer,
                    finalAccumulator,
                    iterationCropLeft, iterationCropTop,
                    alignmentPaddingX, alignmentPaddingY,
                    imageWidth, imageHeight);

                // Cleanup reference pyramid for this iteration
                foreach (var pyramidLevel in iterationReferencePyramid)
                {
                    if (pyramidLevel != iterationPreparedReference) pyramidLevel.Dispose();
                }

                Console.WriteLine($"[VulkanComputePipeline] Iteration {iterationIndex} complete");
            }

            // All iterations complete - finalAccumulator contains the merged result
            Console.WriteLine($"[VulkanComputePipeline] All {FrequencyMergeIterationCount} iterations complete");
            _profiler.EndStage("FrequencyMerge");

            using (_profiler.MeasureStage("NoiseEstimation"))
            {
                estimatedNoiseStandardDeviation = ExecuteNoiseEstimationGpu(finalAccumulator, referenceImage.MosaicPatternWidth);
            }

            // Download result from final accumulator
            using (_profiler.MeasureStage("Download"))
            {
                outputFloatData = finalAccumulator.GetData<float>();
            }

            // Update dimensions for exposure correction and cropping
            paddedWidth = accumulatorWidth;
            paddedHeight = accumulatorHeight;
            paddingAmount = alignmentPaddingX;

            _debugInspector.InspectFinalAccumulator(outputFloatData, alignmentPaddingX, alignmentPaddingY, accumulatorWidth, imageWidth, imageHeight);
            _debugHelper.AnalyzeBayerTileBoundaries(outputFloatData, accumulatorWidth, accumulatorHeight, alignmentPaddingX, alignmentPaddingY, FrequencyMergeTileSize * 2);
        }
        else
        {
            // Spatial mode: weighted pixel averaging in spatial domain
            _profiler.BeginStage("SpatialMerge");
            pixelAccumulator = new VulkanImage(_ctx, (uint)paddedWidth, (uint)paddedHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            weightAccumulator = new VulkanImage(_ctx, (uint)paddedWidth, (uint)paddedHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

            // Initialize pixel accumulator with reference frame
            pixelAccumulator.SetData(preparedTexture.GetData<float>());

            // Initialize weight accumulator with 1.0 (reference frame has weight 1)
            var initialWeights = new float[paddedWidth * paddedHeight];
            Array.Fill(initialWeights, 1.0f);
            weightAccumulator.SetData(initialWeights);

            disposableResources.Add(pixelAccumulator);
            disposableResources.Add(weightAccumulator);

            using (_profiler.MeasureStage("NoiseEstimation"))
            {
                estimatedNoiseStandardDeviation = ExecuteNoiseEstimationGpu(preparedTexture, referenceImage.MosaicPatternWidth);
            }

            for (var frameIndex = 0; frameIndex < input.Images.Count; frameIndex++)
            {
                if (frameIndex == input.ReferenceFrameIndex)
                {
                    continue;
                }

                // Check for cancellation at start of each image
                cancellationToken.ThrowIfCancellationRequested();

                // Report progress for each frame
                var frameProgressValue = 25_000_000 + (frameIndex * 60_000_000 / Math.Max(1, input.Images.Count - 1));
                progress.Update(frameProgressValue, $"Aligning frame {frameIndex + 1}/{input.Images.Count}...");

                Console.WriteLine($"[VulkanComputePipeline] Aligning Image {frameIndex}...");
                var comparisonImage = input.Images[frameIndex];

                using (_profiler.MeasureStage("UploadAlt"))
                {
                    using var rawComparisonBayer = new VulkanImage(_ctx, (uint)imageWidth, (uint)imageHeight, Format.R16Uint,
                        ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                    rawComparisonBayer.SetData(comparisonImage.Data);

                    using var preparedComparisonFrame = new VulkanImage(_ctx, (uint)paddedWidth, (uint)paddedHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    using (_profiler.MeasureStage("PrepareAlt"))
                    {
                        ExecutePrepare(rawComparisonBayer, preparedComparisonFrame, comparisonImage, paddingAmount, paddingAmount);
                    }

                    // Build comparison pyramid for alignment
                    _profiler.BeginStage("BuildAltPyramid");
                    var comparisonPyramid = new List<VulkanImage>();

                    var compPyramidLevel0Width = (int)preparedComparisonFrame.Width / 2;
                    var compPyramidLevel0Height = (int)preparedComparisonFrame.Height / 2;

                    if (compPyramidLevel0Width % 2 != 0) compPyramidLevel0Width++;
                    if (compPyramidLevel0Height % 2 != 0) compPyramidLevel0Height++;

                    var comparisonLevel0 = new VulkanImage(_ctx, (uint)compPyramidLevel0Width, (uint)compPyramidLevel0Height, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(preparedComparisonFrame, comparisonLevel0, scale: 2, comparisonImage);
                    comparisonPyramid.Add(comparisonLevel0);

                    var compPyramidWidth = compPyramidLevel0Width;
                    var compPyramidHeight = compPyramidLevel0Height;
                    for (var pyramidLevel = 1; pyramidLevel < PyramidLevelCount; pyramidLevel++)
                    {
                        var nextPyramidWidth = compPyramidWidth / 2;
                        if (nextPyramidWidth % 2 != 0) nextPyramidWidth++;

                        var nextPyramidHeight = compPyramidHeight / 2;
                        if (nextPyramidHeight % 2 != 0) nextPyramidHeight++;

                        var pyramidLevelImage = new VulkanImage(_ctx, (uint)nextPyramidWidth, (uint)nextPyramidHeight, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteAvgPool(comparisonPyramid[pyramidLevel - 1], pyramidLevelImage, scale: 2, comparisonImage);
                        comparisonPyramid.Add(pyramidLevelImage);
                        compPyramidWidth = nextPyramidWidth;
                        compPyramidHeight = nextPyramidHeight;
                    }
                    _profiler.EndStage("BuildAltPyramid");

                    // Compute alignment vectors between reference and comparison pyramids
                    var alignmentVectors = new VulkanImage(_ctx, (uint)alignmentTileInfo.TileCountX, (uint)alignmentTileInfo.TileCountY, Format.R16G16B16A16Sint,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                    using (_profiler.MeasureStage("Alignment"))
                    {
                        ExecuteAlignmentSearch(referencePyramid, comparisonPyramid, alignmentVectors, alignmentTileInfo, scale: 2);
                    }
                    disposableResources.Add(alignmentVectors);

                    // Warp comparison frame to align with reference
                    Console.WriteLine($"[VulkanComputePipeline] Warping Image {frameIndex}...");
                    var warpedComparisonFrame = new VulkanImage(_ctx, preparedComparisonFrame.Width, preparedComparisonFrame.Height, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    using (_profiler.MeasureStage("Warp"))
                    {
                        ExecuteWarp(preparedComparisonFrame, warpedComparisonFrame, alignmentVectors, alignmentTileInfo, paddingAmount, paddingAmount);
                    }

                    // Merge warped frame into accumulators (spatial weighted averaging)
                    Console.WriteLine($"[VulkanComputePipeline] Merging Image {frameIndex}...");
                    var exposureDifference = (float)(referenceImage.ExposureBias - comparisonImage.ExposureBias);
                    using (_profiler.MeasureStage("Merge"))
                    {
                        ExecuteMerge(preparedTexture, warpedComparisonFrame, weightAccumulator!, pixelAccumulator!, referenceImage.WhiteLevel, blackLevel: 0.0f, options.NoiseReduction, estimatedNoiseStandardDeviation, exposureDifference);
                    }

                    // Cleanup comparison pyramid
                    foreach (var pyramidLevel in comparisonPyramid)
                    {
                        if (pyramidLevel != preparedComparisonFrame)
                        {
                            pyramidLevel.Dispose();
                        }
                    }
                    warpedComparisonFrame.Dispose();
                }
            }
            _profiler.EndStage("SpatialMerge");

            // Cleanup reference pyramid levels (keep level 0 for debug dump)
            for (var pyramidLevel = 1; pyramidLevel < referencePyramid.Count; pyramidLevel++)
            {
                disposableResources.Add(referencePyramid[pyramidLevel]);
            }

            // DEBUG: Dump after all merges complete
            _debugHelper.DumpTexture(pixelAccumulator!, "step_3_merge_accum_spatial", referenceImage, paddedWidth, paddedHeight, paddingAmount);

            // Normalize: result = pixelAccumulator / weightAccumulator (GPU - eliminates 2 GetData() calls!)
            using (_profiler.MeasureStage("Normalize"))
            {
                using var normalizedTexture = new VulkanImage(_ctx, (uint)paddedWidth, (uint)paddedHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                _spatialMergePipeline.NormalizeAccumulators(pixelAccumulator!, weightAccumulator!, normalizedTexture);
                outputFloatData = normalizedTexture.GetData<float>();
            }
        }

        #endregion

        #region Post-Processing and Output

        // Exposure Correction (optional)
        if (options.ExposureControl != ExposureControlOption.Off)
        {
            Console.WriteLine("[VulkanComputePipeline] Uploading for Exposure Correction...");
            using (_profiler.MeasureStage("ExposureCorrection"))
            {
                using var exposureCorrectionTexture = new VulkanImage(_ctx, (uint)paddedWidth, (uint)paddedHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
                exposureCorrectionTexture.SetData(outputFloatData);
                ExecuteExposureCorrection(exposureCorrectionTexture, options.ExposureControl, referenceImage);
                outputFloatData = exposureCorrectionTexture.GetData<float>();

                _debugHelper.DumpTexture(exposureCorrectionTexture, "step_6_exposure", referenceImage, paddedWidth, paddedHeight, paddingAmount);
            }
        }

        // Convert back to RawImage (crop padding and convert to 16-bit)
        Console.WriteLine("[VulkanComputePipeline] Converting to Output...");
        _profiler.BeginStage("OutputConversion");
        var outputImage = new RawImage
        {
            Width = imageWidth,
            Height = imageHeight,
            Data = new ushort[imageWidth * imageHeight],
            MosaicPatternWidth = referenceImage.MosaicPatternWidth,
            WhiteLevel = referenceImage.WhiteLevel,
            BlackLevels = referenceImage.BlackLevels,
            ExposureBias = referenceImage.ExposureBias,
            IsoSpeedExposureTimeProduct = referenceImage.IsoSpeedExposureTimeProduct,
            ColorChannelMultipliers = referenceImage.ColorChannelMultipliers,
            SourcePath = referenceImage.SourcePath,
            CfaPattern = referenceImage.CfaPattern,
            ColorMatrix1 = referenceImage.ColorMatrix1,
            ColorMatrix2 = referenceImage.ColorMatrix2,
            CalibrationIlluminant1 = referenceImage.CalibrationIlluminant1,
            CalibrationIlluminant2 = referenceImage.CalibrationIlluminant2,
            AsShotNeutral = referenceImage.AsShotNeutral,
            CameraMake = referenceImage.CameraMake,
            CameraModel = referenceImage.CameraModel,
            IsBayerData = referenceImage.IsBayerData
        };

        // Calculate scale factor for 16-bit output if requested
        var bitDepthScaleFactor = 1.0f;
        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            float whiteLevelValue = referenceImage.WhiteLevel;
            bitDepthScaleFactor = (float)Math.Pow(2.0, 16.0 - Math.Ceiling(Math.Log2(whiteLevelValue)));
        }

        // Copy pixels from padded float buffer to output, cropping padding and converting to 16-bit
        for (var pixelY = 0; pixelY < imageHeight; pixelY++)
        {
            for (var pixelX = 0; pixelX < imageWidth; pixelX++)
            {
                var sourceIndex = (pixelY + paddingAmount) * paddedWidth + (pixelX + paddingAmount);
                var destinationIndex = pixelY * imageWidth + pixelX;
                var scaledPixelValue = outputFloatData[sourceIndex] * bitDepthScaleFactor;
                outputImage.Data[destinationIndex] = (ushort)Math.Clamp(scaledPixelValue, 0, MaxPixelValue16Bit);
            }
        }

        // Update white level for 16-bit output
        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            outputImage.WhiteLevel = (int)(referenceImage.WhiteLevel * bitDepthScaleFactor);
            if (outputImage.WhiteLevel > MaxPixelValue16Bit)
            {
                outputImage.WhiteLevel = MaxPixelValue16Bit;
            }
        }

        _profiler.EndStage("OutputConversion");

        // Dispose all accumulated resources
        foreach (var resource in disposableResources)
        {
            resource.Dispose();
        }

        // Print profiling results
        _profiler.StopTotal();
        _profiler.PrintResults();

        return outputImage;
    }

    #endregion

    #region Pipeline Setup Methods

    /// <summary>
    /// Ensures the Bayer preparation pipeline is initialized.
    /// </summary>
    private void EnsurePreparePipeline()
    {
        if (_kernelPrepareBayer is not null)
        {
            return;
        }

        _prepareLayout = _kernelManager.GetOrCreateLayout(PipelineKernelSpecs.PrepareLayout);
        _kernelPrepareBayer = _kernelManager.GetOrCreateKernel(PipelineKernelSpecs.PrepareBayer, _prepareLayout);
    }

    /// <summary>
    /// Ensures the Bayer-to-RGBA and RGBA-to-Bayer conversion pipelines are initialized.
    /// </summary>
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

    #endregion

    #region Pipeline Execution Delegates

    // These methods delegate to specialized pipeline classes for cleaner separation of concerns

    private void ExecuteMergeFrequency(
        VulkanImage referenceFourierTransform,
        VulkanImage referenceRgba,
        VulkanImage alignedComparisonRgba,
        VulkanImage weightAccumulator,
        VulkanImage accumulatedFourierTransform,
        float whiteLevel,
        float blackLevel,
        double noiseReduction,
        float noiseStandardDeviation,
        float exposureDifference,
        int tileSize,
        int mosaicPatternWidth,
        int isUniformExposure,
        VulkanImage? totalMismatchTexture = null,
        int totalImageCount = 1,
        double exposureCorrectionRatio = 1.0)
        => _frequencyMergePipeline.ExecuteMergeFrequency(
            referenceFourierTransform, referenceRgba, alignedComparisonRgba, weightAccumulator, accumulatedFourierTransform,
            whiteLevel, blackLevel, noiseReduction, noiseStandardDeviation, exposureDifference, tileSize, mosaicPatternWidth, isUniformExposure,
            totalMismatchTexture, totalImageCount, exposureCorrectionRatio);

    private void ExecuteBackwardFft(VulkanImage inputFourierTransform, VulkanImage outputSpatial, int textureCount, int tileSize)
        => _frequencyMergePipeline.ExecuteBackwardFft(inputFourierTransform, outputSpatial, textureCount, tileSize);

    private List<ValidationResult> RunFftRoundTripValidation(VulkanImage rgbaInput, int tileSize)
        => _frequencyMergePipeline.RunFftRoundTripValidation(rgbaInput, tileSize);

    private void ExecuteForwardFft(VulkanImage input, VulkanImage output, int tileSize, int width, int height)
        => _frequencyMergePipeline.ExecuteForwardFft(input, output, tileSize, width, height);

    private void ExecuteConvertToRgba(VulkanImage bayerInput, VulkanImage rgbaOutput, int[] cfaPattern, int cropX = 0, int cropY = 0)
        => _conversionHelper.ConvertToRgba(bayerInput, rgbaOutput, cfaPattern, _kernelConvertToRgba!, _conversionLayout, cropX, cropY);

    private void ExecuteConvertToBayer(VulkanImage rgbaInput, VulkanImage bayerOutput, int[] cfaPattern)
        => _conversionHelper.ConvertToBayer(rgbaInput, bayerOutput, cfaPattern, _kernelConvertToBayer!, _conversionLayout);

    private void ExecuteCalculateRms(VulkanImage rgbaInput, VulkanImage rmsOutput, int tileCountX, int tileCountY, int tileSize)
        => _frequencyMergePipeline.ExecuteCalculateRms(rgbaInput, rmsOutput, tileCountX, tileCountY, tileSize);

    private void ExecuteDeconvoluteFrequency(VulkanImage accumulatedFourierTransform, VulkanImage mismatchTexture, int tileCountX, int tileCountY, int tileSize)
        => _frequencyMergePipeline.ExecuteDeconvoluteFrequency(accumulatedFourierTransform, mismatchTexture, tileCountX, tileCountY, tileSize);

    private void ExecuteReduceArtifacts(VulkanImage outputTexture, VulkanImage referenceTexture, int tileCountX, int tileCountY, int tileSize, int[] blackLevels)
        => _frequencyMergePipeline.ExecuteReduceArtifacts(outputTexture, referenceTexture, tileCountX, tileCountY, tileSize, blackLevels);

    /// <summary>
    /// Executes optimized batched post-processing that combines deconvolution, backward FFT, and artifact reduction
    /// into a single command buffer submission for improved GPU throughput.
    /// </summary>
    private void ExecutePostProcessingBatched(
        VulkanImage accumulatedFourierTransform,
        VulkanImage mismatchTexture,
        VulkanImage outputSpatial,
        VulkanImage referenceTextureForArtifacts,
        int tileCountX, int tileCountY, int tileSize, int textureCount, int[] blackLevels, bool skipReduceArtifacts)
        => _frequencyMergePipeline.ExecutePostProcessingBatched(
            accumulatedFourierTransform, mismatchTexture, outputSpatial, referenceTextureForArtifacts,
            tileCountX, tileCountY, tileSize, textureCount, blackLevels, skipReduceArtifacts);

    private void ExecuteAvgPool(VulkanImage input, VulkanImage output, int scale, RawImage rawImageMetadata, bool normalize = false)
        => _alignmentPipeline.ExecuteAvgPool(input, output, scale, rawImageMetadata, normalize);

    private void ExecuteAlignmentSearch(List<VulkanImage> referencePyramid, List<VulkanImage> comparisonPyramid, VulkanImage alignmentOutput, TileInfo baseTileInfo, int scale, bool uniformExposure = true)
        => _alignmentPipeline.ExecuteAlignmentSearch(referencePyramid, comparisonPyramid, alignmentOutput, baseTileInfo, scale, uniformExposure);

    private void ExecuteWarp(VulkanImage sourceImage, VulkanImage output, VulkanImage alignmentVectors, TileInfo tileInfo, int paddingLeft = 0, int paddingTop = 0)
        => _alignmentPipeline.ExecuteWarp(sourceImage, output, alignmentVectors, tileInfo, paddingLeft, paddingTop);

    private void ExecuteMerge(VulkanImage referenceFrame, VulkanImage warpedFrame, VulkanImage weightAccumulator, VulkanImage pixelAccumulator, float whiteLevel, float blackLevel, double noiseReduction, float noiseStandardDeviation, float exposureDifference)
        => _spatialMergePipeline.ExecuteMerge(referenceFrame, warpedFrame, weightAccumulator, pixelAccumulator, whiteLevel, blackLevel, noiseReduction, noiseStandardDeviation, exposureDifference);

    private void ExecutePrepare(VulkanImage input, VulkanImage output, RawImage rawImageMetadata, int paddingLeft, int paddingTop, int exposureDifference = 0)
        => _conversionHelper.Prepare(input, output, rawImageMetadata, paddingLeft, paddingTop, _kernelPrepareBayer!, _prepareLayout, exposureDifference);

    private void ExecuteBlur(VulkanImage input, VulkanImage output, int kernelSize, int mosaicPatternWidth, VulkanImage? intermediate = null)
        => _exposurePipeline.ExecuteBlur(input, output, kernelSize, mosaicPatternWidth, intermediate);

    private void ExecuteMaxReduction(VulkanImage input, VulkanBuffer outputBuffer, int mosaicPatternWidth)
        => _exposurePipeline.ExecuteMaxReduction(input, outputBuffer, mosaicPatternWidth);

    private void ExecuteExposureCorrection(VulkanImage image, ExposureControlOption option, RawImage metadata)
        => _exposurePipeline.ExecuteExposureCorrection(image, option, metadata);

    private float ExecuteNoiseEstimationGpu(VulkanImage inputTexture, int mosaicPatternWidth)
        => _exposurePipeline.ExecuteNoiseEstimationGpu(inputTexture, mosaicPatternWidth);

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Releases all Vulkan resources held by this pipeline.
    /// </summary>
    public void Dispose()
    {
        // Wait for any pending GPU operations to complete before disposing
        _ctx.Vk.DeviceWaitIdle(_ctx.Device);

        // Dispose kernel manager first - this disposes all cached kernels
        // NOTE: Do NOT dispose _kernelPrepareBayer, _kernelConvertToRgba, _kernelConvertToBayer
        // directly as they are obtained from the kernel manager's cache and will be disposed there
        _kernelManager.Dispose();

        // Dispose descriptor manager (frees descriptor sets and pools)
        _descriptors.Dispose();

        // Layouts are cached in kernel manager - don't double-dispose
        // The kernel manager handles layout cleanup

        // Dispose shader compiler (the one owned by this class, not kernel manager's)
        _compiler.Dispose();

        // Finally dispose the Vulkan context (destroys device, instance, etc.)
        _ctx.Dispose();
    }

    #endregion
}
