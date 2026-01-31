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

    // Constants
    private const int TileSizeDefault = 32;

    // Debug helper for dumping intermediate textures
    private readonly PipelineDebugHelper _debugHelper;

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
        _textureUtils = new TextureUtilities(_ctx);
        _conversionHelper = new TextureConversionHelper(_ctx, _descriptors, _textureUtils);
    }

    public async Task<RawImage> ProcessAsync(RenderingInput input, ProcessingOptions options, ProcessingProgress progress)
    {
        Console.WriteLine("[VulkanComputePipeline] Starting processing...");
        
        // Enable debug dump if requested
        _debugHelper.Enabled = options.EnableDebugDump;
        if (_debugHelper.Enabled)
        {
            Console.WriteLine("[VulkanComputePipeline] DEBUG DUMP ENABLED - intermediate DNGs will be saved to DebugOutput/");
        }
        
        // Enable FFT validation if requested
        // TEMPORARY: Hardcoded to true for testing - CLI flag parsing issue to fix later
        EnableFftValidation = true; // options.EnableFftValidation;
        _validationResults.Clear();
        Console.WriteLine($"[VulkanComputePipeline] EnableFftValidation = {EnableFftValidation} (HARDCODED FOR TESTING)");
        if (EnableFftValidation)
        {
            Console.WriteLine("[VulkanComputePipeline] FFT VALIDATION ENABLED - mathematical tests will be run on each stage");
        }
        
        // 1. Compile Shaders
        // 1. Shaders compiled on demand
        // CompileShaders(); removed
        
        // 2. Setup Reference Frame
        var refImage = input.Images[input.ReferenceFrameIndex];
        var width = refImage.Width;
        var height = refImage.Height;
        
        // Calculate Padded Dimensions for Alignment
        var tileSize = ProcessingOptions.GetTileSizePixels(options.TileSize);
        int pad;
        int outWidth;
        int outHeight;

        var isFrequency = options.Merging == MergingAlgorithm.HigherQuality;

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

             if (outWidth % 2 != 0)
             {
                 outWidth++;
             }

             if (outHeight % 2 != 0)
             {
                 outHeight++;
             }
        }
        
        Console.WriteLine($"[VulkanComputePipeline] Input: {width}x{height}, Padded: {outWidth}x{outHeight}, Mode: {options.Merging}");
        
        // 3. Allocate Resources
        using var rawTexture = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint, 
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
            
        // Padded/Float Texture (Buffer for Processing)
        using var preparedTexture = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            
        // 4. Upload Reference Frame
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
            var nextW = currentW / 2; if (nextW % 2 != 0) nextW++;
            var nextH = currentH / 2; if (nextH % 2 != 0) nextH++;
            
            var levelImg = new VulkanImage(_ctx, (uint)nextW, (uint)nextH, Format.R32Sfloat, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                
            ExecuteAvgPool(pyramid[i-1], levelImg, 2, refImage);
            
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

            // FrequencyMergePipeline handles its own lazy initialization
            EnsureConversionPipeline();

            // CRITICAL: Swift hardcodes tile_size_merge = 8 for FFT merging
            const int tileSizeMerge = 8;

            // Calculate alignment padding (from frequency.swift lines 80-97)
            // The tile factor accounts for pyramid downscaling
            var downscaleFactors = new[] { refImage.MosaicPatternWidth, 2, 2, 2 }; // Mosaic, then 3 levels of 2x
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

            // Calculate exposure correction factors (Swift: frequency.swift lines 38-47)
            // These account for the fact that exposure-bracketed bursts include images with different SNR
            var exposureCorr1 = 0.0;
            var exposureCorr2 = 0.0;
            var refExpBias = refImage.ExposureBias;
            var refIsoExpTime = refImage.IsoExposureTime;

            // Check if we have meaningful exposure bias values (not all zeros)
            var hasExposureBias = input.Images.Any(img => img.ExposureBias != 0);

            Console.WriteLine($"[VulkanComputePipeline] Exposure data: refBias={refExpBias}, refIsoExpTime={refIsoExpTime:F4}, hasExposureBias={hasExposureBias}");
            for (var i = 0; i < input.Images.Count; i++)
            {
                var img = input.Images[i];
                double exposureFactor;

                if (hasExposureBias)
                {
                    // Use exposure bias (centistops) - Swift's original method
                    exposureFactor = Math.Pow(2.0, (img.ExposureBias - refExpBias) / 100.0);
                }
                else
                {
                    // Fallback: derive exposure factor from IsoExposureTime ratio
                    // Higher IsoExposureTime = brighter image = higher exposure factor
                    exposureFactor = refIsoExpTime > 0 ? img.IsoExposureTime / refIsoExpTime : 1.0;
                }

                exposureCorr1 += 0.5 + 0.5 / exposureFactor;
                exposureCorr2 += Math.Min(4.0, exposureFactor);
                Console.WriteLine($"[VulkanComputePipeline]   Image {i}: bias={img.ExposureBias}, isoExpTime={img.IsoExposureTime:F4}, factor={exposureFactor:F4}");
            }
            exposureCorr1 /= input.Images.Count;
            exposureCorr2 /= input.Images.Count;
            Console.WriteLine($"[VulkanComputePipeline] Exposure corrections: corr1={exposureCorr1:F4}, corr2={exposureCorr2:F4}, ratio={exposureCorr1/exposureCorr2:F4}");

            // Allocate final accumulator (Bayer domain, accumulates 4 iteration outputs)
            var accWidth = width + 2 * padAlignX;
            var accHeight = height + 2 * padAlignY;
            Console.WriteLine($"[VulkanComputePipeline] Creating finalAccumulator: {accWidth}x{accHeight} (pad={padAlignX},{padAlignY})");
            var finalAccumulator = new VulkanImage(_ctx, (uint)accWidth, (uint)accHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            _textureUtils.FillWithZeros(finalAccumulator);
            Console.WriteLine($"[VulkanComputePipeline] finalAccumulator created: Width={finalAccumulator.Width}, Height={finalAccumulator.Height}");
            disposables.Add(finalAccumulator);

            // === 4-ITERATION LOOP (Swift: frequency.swift line 108) ===
            for (var iteration = 1; iteration <= 4; iteration++)
            {
                Console.WriteLine($"\n[VulkanComputePipeline] === ITERATION {iteration}/4 ===");

                // Calculate shift values (Swift: frequency.swift lines 111-120)
                var shiftLeft   = (iteration % 2 == 0) ? tileSizeMerge : 0;
                var shiftRight  = (iteration % 2 == 1) ? tileSizeMerge : 0;
                var shiftTop    = (iteration < 3) ? tileSizeMerge : 0;
                var shiftBottom = (iteration >= 3) ? tileSizeMerge : 0;

                var padLeft   = padAlignX + shiftLeft;
                var padRight  = padAlignX + shiftRight;
                var padTop    = padAlignY + shiftTop;
                var padBottom = padAlignY + shiftBottom;

                var iterOutWidth = width + padLeft + padRight;
                var iterOutHeight = height + padTop + padBottom;

                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Shift=({shiftLeft-shiftRight},{shiftTop-shiftBottom}), Pad=({padLeft},{padRight},{padTop},{padBottom}), Size={iterOutWidth}x{iterOutHeight}");

                // Prepare reference with iteration-specific padding
                using var preparedRef = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                // DEBUG: Check raw input data before prepare
                {
                    var rawSum = 0;
                    var sampleSize = Math.Min(refImage.Data.Length, 10000);
                    for (var i = 0; i < sampleSize; i++)
                    {
                        rawSum += refImage.Data[i];
                    }
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: Raw input data: sum(first {sampleSize})={rawSum}, mean={rawSum/(double)sampleSize:F2}");
                }

                rawTexture.SetData(refImage.Data); // Re-upload to ensure clean state
                ExecutePrepare(rawTexture, preparedRef, refImage, padLeft, padTop);

                // DEBUG DUMP: Prepared reference Bayer texture (to compare with warped comparison Bayer)
                _debugHelper.DumpTexture(preparedRef, $"step_1b_iter{iteration}_prepared_ref_bayer", refImage, iterOutWidth, iterOutHeight, 0);

                // DEBUG: Check prepared reference (sample from middle to avoid padding)
                {
                    var prepData = preparedRef.GetData<float>();
                    var startIdx = prepData.Length / 4; // Start from 25% into the image
                    var sampleSize = Math.Min(10000, prepData.Length - startIdx);
                    double prepSum = 0;
                    for (var i = 0; i < sampleSize; i++)
                    {
                        prepSum += Math.Abs(prepData[startIdx + i]);
                    }
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare: sum(mid {sampleSize})={prepSum:F2}, mean={prepSum/sampleSize:F4}");
                    // Also check specific region that should contain data (skip padding)
                    var rowStart = (padTop + height/2) * iterOutWidth + (padLeft + width/2);
                    double centerSum = 0;
                    for (var i = 0; i < Math.Min(1000, prepData.Length - rowStart); i++)
                    {
                        centerSum += Math.Abs(prepData[rowStart + i]);
                    }
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare (center region): sum={centerSum:F2}, mean={centerSum/1000.0:F4}");
                    
                    // CHECK LEFT EDGE at mid height - this is exactly where RGBA mid samples from!
                    // RGBA pixel (0, 768) reads from Bayer (padLeft, padTop + 768*2) = (260, 1788)
                    var leftEdgeRow = padTop + (height / 2); // 252 + 1536 = 1788
                    var leftEdgeStart = leftEdgeRow * iterOutWidth + padLeft;
                    double leftEdgeSum = 0;
                    for (var i = 0; i < Math.Min(100, prepData.Length - leftEdgeStart); i++)
                    {
                        leftEdgeSum += Math.Abs(prepData[leftEdgeStart + i]);
                    }
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare (LEFT EDGE row {leftEdgeRow}): sum={leftEdgeSum:F2}, mean={leftEdgeSum/100.0:F4}");

                    if (prepSum < 0.01)
                    {
                        Console.WriteLine($"[WARNING] Prepare produced near-zero output!");
                    }
                }

                // RGBA dimensions: Match Swift's calculation from frequency.swift line 125 and texture.swift line 343
                // Swift: convert_to_rgba(ref_texture, crop_merge_x, crop_merge_y)
                // Swift output size: (in_texture.width - 2*crop_x)/2, (in_texture.height - 2*crop_y)/2
                // This crops cropMergeX/Y from each side of the padded texture, then halves for RGBA packing
                var rgbaWidth = (iterOutWidth - 2 * cropMergeX) / 2;
                var rgbaHeight = (iterOutHeight - 2 * cropMergeY) / 2;
                Console.WriteLine($"[DEBUG] Iteration {iteration}: RGBA dimensions: {rgbaWidth}x{rgbaHeight} (from padded {iterOutWidth}x{iterOutHeight}, cropMerge={cropMergeX},{cropMergeY})");
                var ftWidth = rgbaWidth * 2; // Complex storage (Real + Imaginary)
                var ftHeight = rgbaHeight;

                // Convert reference Bayer → RGBA
                // CRITICAL: Use cropMergeX/cropMergeY as crop offset (constant across iterations)
                // NOT padLeft/padTop (which varies per iteration due to shifts)
                // This ensures the RGBA content is the SAME spatial region for all 4 iterations,
                // just with different FFT tile boundaries due to the shift in prepare_texture
                using var rgbaRefTexture = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                Console.WriteLine($"[DEBUG] Iteration {iteration}: Converting reference to RGBA with crop offset ({cropMergeX}, {cropMergeY})...");
                ExecuteConvertToRgba(preparedRef, rgbaRefTexture, refImage.CfaPattern, cropMergeX, cropMergeY);

                // DEBUG: Check RGBA conversion output - comprehensive sampling
                {
                    var rgbaData = rgbaRefTexture.GetData<float>();

                    // Mid sample (original)
                    var startIdx = rgbaData.Length / 4;
                    var sampleSize = Math.Min(10000, rgbaData.Length - startIdx);
                    double rgbaSumMid = 0;
                    for (var i = 0; i < sampleSize; i++)
                    {
                        rgbaSumMid += Math.Abs(rgbaData[startIdx + i]);
                    }

                    // TOTAL sum (to see if ANY data exists)
                    double rgbaTotal = 0;
                    foreach (var t in rgbaData)
                    {
                        rgbaTotal += Math.Abs(t);
                    }

                    // Find first non-zero row
                    var rgbaWidthPx = rgbaWidth;
                    var firstNonZeroRow = -1;
                    for (var row = 0; row < rgbaHeight && firstNonZeroRow < 0; row++)
                    {
                        double rowSum = 0;
                        var rowStart = row * rgbaWidthPx * 4;  // 4 floats per RGBA pixel
                        for (var col = 0; col < Math.Min(100, rgbaWidthPx); col++)
                        {
                            var idx = rowStart + col * 4;  // Sample R channel
                            if (idx < rgbaData.Length)
                            {
                                rowSum += Math.Abs(rgbaData[idx]);
                            }
                        }

                        if (rowSum > 0.01)
                        {
                            firstNonZeroRow = row;
                        }
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After convert_to_rgba: mid10k={rgbaSumMid:F2}, TOTAL={rgbaTotal:F2}, firstNonZeroRow={firstNonZeroRow}");
                    if (rgbaTotal < 0.01)
                    {
                        Console.WriteLine($"[WARNING] RGBA conversion produced COMPLETELY ZERO output!");
                    }
                }

                // DEBUG DUMP: Reference RGBA texture (before FFT)
                _debugHelper.DumpRgbaTexture(rgbaRefTexture, $"step_2_iter{iteration}_ref_rgba", refImage);

                // FFT VALIDATION: Run round-trip test on first iteration only
                if (EnableFftValidation && iteration == 1)
                {
                    Console.WriteLine("\n[VulkanComputePipeline] Running FFT validation (first iteration)...");
                    var validationResults = RunFftRoundTripValidation(rgbaRefTexture, tileSizeMerge);
                    _validationResults.AddRange(validationResults);
                    
                    // Check if round-trip failed - if so, provide diagnosis and optionally stop
                    var roundTripResult = validationResults.FirstOrDefault(r => r.TestName.Contains("Round-Trip"));
                    if (roundTripResult is not null && !roundTripResult.Passed)
                    {
                        Console.WriteLine("\n>>> FFT VALIDATION FAILED - Round-trip test indicates FFT shader bug");
                        Console.WriteLine(">>> Continuing with processing to capture full output for analysis...\n");
                        // Don't stop - let the full pipeline run so user can see the dot matrix pattern
                        // and correlate with the validation metrics
                    }
                    else
                    {
                        Console.WriteLine("\n>>> FFT VALIDATION PASSED - FFT shaders are working correctly");
                        Console.WriteLine(">>> If output is still wrong, bug is in merge/pipeline, not FFT\n");
                    }
                }

                // Build reference pyramid (Frequency Loop)
                var refPyramid = new List<VulkanImage>();
                var l0RefW = (int)preparedRef.Width / 2;
                var l0RefH = (int)preparedRef.Height / 2;

                if (l0RefW % 2 != 0)
                {
                    l0RefW++;
                }

                if (l0RefH % 2 != 0)
                {
                    l0RefH++;
                }
                
                var refLevel0 = new VulkanImage(_ctx, (uint)l0RefW, (uint)l0RefH, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteAvgPool(preparedRef, refLevel0, 2, refImage);
                refPyramid.Add(refLevel0);
                
                int currW = l0RefW, currH = l0RefH;
                for (var lvl = 1; lvl < 4; lvl++)
                {
                    var nW = currW / 2; if (nW % 2 != 0) nW++;
                    var nH = currH / 2; if (nH % 2 != 0) nH++;
                    // Swift: blur(pyramid.last!, with_pattern_width: 1, using_kernel_size: 2) before avg_pool at levels 1+
                    var blurredPrev = new VulkanImage(_ctx, (uint)currW, (uint)currH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    disposables.Add(blurredPrev);
                    ExecuteBlur(refPyramid[lvl - 1], blurredPrev, 2, 1);
                    var levelImg = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(blurredPrev, levelImg, 2, refImage);
                    refPyramid.Add(levelImg);
                    currW = nW; currH = nH;
                }

                // Calculate TileInfo for THIS ITERATION based on padded pyramid level 0 dimensions
                // CRITICAL FIX: Swift calculates n_tiles from ref_layer (pyramid level) dimensions, NOT original image
                // Swift: n_tiles_x = ref_layer.width / (tile_size / 2) - 1
                // Our pyramid level 0 is l0RefW x l0RefH (half of padded iteration size)
                var iterTileInfo = TileInfo.Calculate(l0RefW, l0RefH, tileSize, ProcessingOptions.GetSearchDistancePixels(options.SearchDistance));
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} TileInfo: {iterTileInfo.NTilesX}x{iterTileInfo.NTilesY} tiles (from {l0RefW}x{l0RefH} pyramid L0)");

                // Calculate RMS per iteration (tile grid size)
                var nTilesX = (iterOutWidth - 2 * cropMergeX) / (2 * tileSizeMerge);
                var nTilesY = (iterOutHeight - 2 * cropMergeY) / (2 * tileSizeMerge);

                using var rmsTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                // Calculate RMS from reference RGBA texture
                ExecuteCalculateRms(rgbaRefTexture, rmsTexture, nTilesX, nTilesY, tileSizeMerge);

                // Initialize total mismatch texture for this iteration
                using var totalMismatchTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
                _textureUtils.FillWithZeros(totalMismatchTexture);

                // Forward FFT on reference
                using var refFt = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                Console.WriteLine($"[DEBUG] Iteration {iteration}: Running forward FFT on reference...");
                ExecuteForwardFft(rgbaRefTexture, refFt, tileSizeMerge, rgbaWidth, rgbaHeight);

                // DEBUG: Check FFT output - comprehensive sampling
                {
                    var fftData = refFt.GetData<float>();

                    // First 10000 (may be in zero padding region)
                    double fftSumFirst = 0;
                    var sampleSize = Math.Min(fftData.Length, 10000);
                    for (var i = 0; i < sampleSize; i++)
                    {
                        fftSumFirst += Math.Abs(fftData[i]);
                    }

                    // Mid-point sample
                    double fftSumMid = 0;
                    var midStart = fftData.Length / 2;
                    for (var i = 0; i < sampleSize && midStart + i < fftData.Length; i++)
                    {
                        fftSumMid += Math.Abs(fftData[midStart + i]);
                    }

                    // TOTAL sum (to see if ANY data exists)
                    double fftTotal = 0;
                    foreach (var t in fftData)
                    {
                        fftTotal += Math.Abs(t);
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After forward_fft: first10k={fftSumFirst:F2}, mid10k={fftSumMid:F2}, TOTAL={fftTotal:F2}");
                    if (fftTotal < 0.01)
                    {
                        Console.WriteLine($"[WARNING] Forward FFT produced COMPLETELY ZERO output!");
                    }
                }

                // Initialize frequency domain accumulator
                using var finalTextureFt = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
                _textureUtils.CopyImage(refFt, finalTextureFt, ftWidth, ftHeight);

                // Estimate noise for this iteration
                estimatedNoiseSd = ExecuteNoiseEstimationGpu(preparedRef, refImage.MosaicPatternWidth);
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Estimated Noise SD = {estimatedNoiseSd:F4}");

                // === COMPARISON LOOP ===
                for (var compIdx = 0; compIdx < input.Images.Count; compIdx++)
                {
                    if (compIdx == input.ReferenceFrameIndex)
                    {
                        continue;
                    }

                    var altImage = input.Images[compIdx];
                    Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Processing comparison image {compIdx}...");

                    // Prepare comparison frame with iteration-specific padding
                    using var rawAlt = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint,
                        ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                    rawAlt.SetData(altImage.Data);

                    using var preparedAlt = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                    // Calculate exposure difference for prepare pass (ref - comp, in centistops)
                    // Swift: prepare_texture(..., exposure_bias[ref_idx]-exposure_bias[comp_idx], ...)
                    int prepareExpDiff;
                    if (hasExposureBias)
                    {
                        prepareExpDiff = refImage.ExposureBias - altImage.ExposureBias;
                    }
                    else
                    {
                        // Derive from IsoExposureTime ratio: log2(ref/comp) * 100 centistops
                        var ratio = refImage.IsoExposureTime / Math.Max(altImage.IsoExposureTime, 0.0001);
                        prepareExpDiff = (int)Math.Round(Math.Log2(ratio) * 100.0);
                    }
                    Console.WriteLine($"[VulkanComputePipeline] Comparison {compIdx}: prepareExpDiff={prepareExpDiff} centistops");
                    ExecutePrepare(rawAlt, preparedAlt, altImage, padLeft, padTop, prepareExpDiff);

                    // DEBUG DUMP: Prepared comparison Bayer texture (before warp, to compare with warped)
                    _debugHelper.DumpTexture(preparedAlt, $"step_1c_iter{iteration}_prepared_comp{compIdx}_bayer", refImage, iterOutWidth, iterOutHeight, 0);

                    // Build comparison pyramid (Frequency Loop)
                    var altPyramid = new List<VulkanImage>();
                    var l0AltW = (int)preparedAlt.Width / 2;
                    var l0AltH = (int)preparedAlt.Height / 2;
                    
                    if (l0AltW % 2 != 0)
                    {
                        l0AltW++;
                    }

                    if (l0AltH % 2 != 0)
                    {
                        l0AltH++;
                    }
                    
                    var altLevel0 = new VulkanImage(_ctx, (uint)l0AltW, (uint)l0AltH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(preparedAlt, altLevel0, 2, altImage);
                    altPyramid.Add(altLevel0);
                    
                    currW = l0AltW; currH = l0AltH;
                    for (var lvl = 1; lvl < 4; lvl++)
                    {
                        var nW = currW / 2; if (nW % 2 != 0) nW++;
                        var nH = currH / 2; if (nH % 2 != 0) nH++;
                        // Swift: blur before avg_pool at levels 1+
                        using var blurredPrevAlt = new VulkanImage(_ctx, (uint)currW, (uint)currH, Format.R32Sfloat,
                            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteBlur(altPyramid[lvl - 1], blurredPrevAlt, 2, 1);
                        var levelImg = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat,
                            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteAvgPool(blurredPrevAlt, levelImg, 2, altImage);
                        altPyramid.Add(levelImg);
                        currW = nW; currH = nH;
                    }

                    // Align and warp - use iterTileInfo (calculated from padded pyramid dimensions)
                    using var alignment = new VulkanImage(_ctx, (uint)iterTileInfo.NTilesX, (uint)iterTileInfo.NTilesY, Format.R16G16B16A16Sint,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                    var isUniformExposure = (altImage.ExposureBias == refImage.ExposureBias);
                    ExecuteAlignmentSearch(refPyramid, altPyramid, alignment, iterTileInfo, 2, isUniformExposure);

                    // DEBUG DUMP: Alignment vectors visualization
                    _debugHelper.DumpAlignment(alignment, $"step_2a_iter{iteration}_alignment_comp{compIdx}", refImage);

                    using var warpedAlt = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    
                    // DEBUG: Check preparedAlt BEFORE warp
                    {
                        var prepAltData = preparedAlt.GetData<float>();
                        var dataStartIdx = padTop * iterOutWidth + padLeft;
                        double prepAltSum = 0;
                        var samples = Math.Min(1000, prepAltData.Length - dataStartIdx);
                        if (dataStartIdx >= 0 && dataStartIdx < prepAltData.Length)
                        {
                            for (var idx = 0; idx < samples; idx++)
                            {
                                prepAltSum += Math.Abs(prepAltData[dataStartIdx + idx]);
                            }
                        }
                        Console.WriteLine($"[WARP DEBUG] preparedAlt BEFORE warp (at data region): sum={prepAltSum:F2}, mean={prepAltSum/samples:F4}");
                        if (prepAltSum < 0.01)
                        {
                            Console.WriteLine($"[WARP DEBUG] ❌ ERROR: preparedAlt is EMPTY before warp!");
                        }
                    }
                    
                    ExecuteWarp(preparedAlt, warpedAlt, alignment, iterTileInfo, padLeft, padTop);

                    // DEBUG DUMP: Warped Bayer texture (before RGBA conversion) to see if split is in warp
                    _debugHelper.DumpTexture(warpedAlt, $"step_2b_iter{iteration}_warped_comp{compIdx}_bayer", refImage, iterOutWidth, iterOutHeight, 0);

                    // DEBUG: Check warpedAlt before RGBA conversion
                    {
                        var warpData = warpedAlt.GetData<float>();
                        double warpSum = 0;
                        var warpSamples = Math.Min(warpData.Length, 1000);
                        for (var i = 0; i < warpSamples; i++)
                        {
                            warpSum += Math.Abs(warpData[i]);
                        }
                        
                        // Also check at the data region offset
                        var dataStartIdx = padTop * iterOutWidth + padLeft;
                        double warpDataSum = 0;
                        var samples = Math.Min(1000, warpData.Length - dataStartIdx);
                        if (dataStartIdx >= 0 && dataStartIdx < warpData.Length)
                        {
                            for (var idx = 0; idx < samples; idx++)
                            {
                                warpDataSum += Math.Abs(warpData[dataStartIdx + idx]);
                            }
                        }
                        
                        Console.WriteLine($"[WARP DEBUG] warpedAlt AFTER warp (first 1000): sum={warpSum:F2}, mean={warpSum/warpSamples:F4}");
                        Console.WriteLine($"[WARP DEBUG] warpedAlt AFTER warp (at data region): sum={warpDataSum:F2}, mean={warpDataSum/samples:F4}");
                        if (warpSum < 0.01 && warpDataSum < 0.01)
                        {
                            Console.WriteLine($"[WARP DEBUG] ❌ ERROR: warpedAlt is EMPTY after warp!");
                        }
                    }

                    using var alignedTextureRgba = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    // CRITICAL: Use cropMergeX/cropMergeY as crop offset (same as reference)
                    // This ensures aligned texture uses the same spatial window as reference
                    ExecuteConvertToRgba(warpedAlt, alignedTextureRgba, refImage.CfaPattern, cropMergeX, cropMergeY);

                    // DEBUG: Check alignedTextureRgba after RGBA conversion
                    {
                        var rgbaData = alignedTextureRgba.GetData<float>();
                        double rgbaSum = 0;
                        double rgbaSumMid = 0;
                        var rgbaSamples = Math.Min(rgbaData.Length, 1000);
                        var midStart = rgbaData.Length / 2;
                        for (var i = 0; i < rgbaSamples; i++)
                        {
                            rgbaSum += Math.Abs(rgbaData[i]);
                        }

                        for (var i = 0; i < rgbaSamples && midStart + i < rgbaData.Length; i++)
                        {
                            rgbaSumMid += Math.Abs(rgbaData[midStart + i]);
                        }
                        Console.WriteLine($"[WARP DEBUG] alignedTextureRgba AFTER convert: first1000 sum={rgbaSum:F2}, mid1000 sum={rgbaSumMid:F2}");
                        Console.WriteLine($"[WARP DEBUG]   Total size={rgbaData.Length} floats ({rgbaWidth}x{rgbaHeight}x4)");
                    }

                    // DEBUG DUMP: Aligned comparison frame (after warp + convert to RGBA)
                    _debugHelper.DumpRgbaTexture(alignedTextureRgba, $"step_3_iter{iteration}_aligned_comp{compIdx}_rgba", refImage);

                    // Calculate exposure factor
                    var exposureFactor = Math.Pow(2.0, (altImage.ExposureBias - refImage.ExposureBias) / 100.0);

                    // Calculate mismatch, normalize, accumulate (using existing ExecuteMergeFrequency helper kernels)
                    // We'll extract and call the individual steps from ExecuteMergeFrequency

                    // For now, call the existing ExecuteMergeFrequency which handles:
                    // - Abs diff, RMS calculation, mismatch calculation & normalization
                    // - Highlights norm calculation
                    // - Forward FFT on aligned RGBA
                    // - Frequency domain merge
                    // Note: This internally passes aligned as Bayer which is the OLD BUG
                    // We're passing alignedTextureRgba instead (RGBA) which is correct

                    // Create temporary textures for mismatch operations
                    using var mismatchTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
                    using var highlightsNormTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                    using var alignedTextureFt = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

                    // Call modified merge (we'll pass RGBA aligned texture, not Bayer!)
                    // The ExecuteMergeFrequency will handle all the intermediate steps
                    // Now also accumulates normalized mismatch into totalMismatchTexture for deconvolution
                    // Note: For uniformExp, we check if exposures are equal using the same logic as hasExposureBias
                    var uniformExp = hasExposureBias
                        ? (altImage.ExposureBias == refImage.ExposureBias ? 1 : 0)
                        : (Math.Abs(altImage.IsoExposureTime - refImage.IsoExposureTime) < 0.001f ? 1 : 0);
                    // expDiff for merge uses (comp - ref), opposite of prepare (ref - comp)
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
                // DEBUG: Check finalTextureFT before deconvolution (use TOTAL to avoid zero-padding confusion)
                {
                    var preDec = finalTextureFt.GetData<float>();
                    double preDecTotal = 0;
                    foreach (var t in preDec)
                    {
                        preDecTotal += Math.Abs(t);
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: Before deconvolution: TOTAL={preDecTotal:F2}, mean={preDecTotal/preDec.Length:F4}");
                }

                // Deconvolute with accumulated mismatch
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Deconvolution...");
                ExecuteDeconvoluteFrequency(finalTextureFt, totalMismatchTexture, nTilesX, nTilesY, tileSizeMerge);

                // DEBUG: Check finalTextureFT after deconvolution (use TOTAL to avoid zero-padding confusion)
                {
                    var postDec = finalTextureFt.GetData<float>();
                    double postDecTotal = 0;
                    foreach (var t in postDec)
                    {
                        postDecTotal += Math.Abs(t);
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After deconvolution: TOTAL={postDecTotal:F2}, mean={postDecTotal/postDec.Length:F4}");
                    if (postDecTotal < 0.01)
                    {
                        Console.WriteLine($"[WARNING] Deconvolution produced near-zero output!");
                    }
                }

                // Backward FFT
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Backward FFT...");
                using var outputTextureRgba = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteBackwardFft(finalTextureFt, outputTextureRgba, input.Images.Count, tileSizeMerge);

                // DEBUG: Check backward FFT output and decode shader debug info
                {
                    var backFftData = outputTextureRgba.GetData<float>();

                    // Decode debug info from corner pixels (16×16 region)
                    Console.WriteLine("[DEBUG] === Shader Debug Info (backward_fft) ===");
                    Console.WriteLine("[DEBUG] First 16×16 pixels encode: R=threadX, G=threadY, B=nTilesX, A=nTilesY (as raw floats)");

                    // Read corner pixel (0,0) which should have nTilesX/nTilesY info
                    var stride = rgbaWidth * 4; // RGBA = 4 channels
                    var threadX00 = backFftData[0];
                    var threadY00 = backFftData[1];
                    var shaderNTilesX = backFftData[2];
                    var shaderNTilesY = backFftData[3];
                    Console.WriteLine($"[DEBUG] Pixel(0,0): threadID=({threadX00:F0},{threadY00:F0}), shader_nTilesX={shaderNTilesX:F0}, shader_nTilesY={shaderNTilesY:F0}");
                    Console.WriteLine($"[DEBUG] Dispatched: {rgbaWidth/tileSizeMerge}x{rgbaHeight/tileSizeMerge} threads (for {rgbaWidth}x{rgbaHeight} texture, tileSize={tileSizeMerge})");

                    // Check if any threads beyond (128,96) executed
                    var foundBeyond128 = false;
                    int maxThreadX = 0, maxThreadY = 0;
                    for (var y = 0; y < 16 && y < rgbaHeight; y++)
                    {
                        for (var x = 0; x < 16 && x < rgbaWidth; x++)
                        {
                            var idx = (y * rgbaWidth + x) * 4;
                            var threadX = backFftData[idx + 0];
                            var threadY = backFftData[idx + 1];
                            maxThreadX = Math.Max(maxThreadX, (int)threadX);
                            maxThreadY = Math.Max(maxThreadY, (int)threadY);
                            
                            if (threadX >= 128 || threadY >= 96)
                            {
                                foundBeyond128 = true;
                            }
                        }
                    }
                    Console.WriteLine($"[DEBUG] Max thread IDs in debug region: ({maxThreadX}, {maxThreadY})");
                    if (!foundBeyond128)
                    {
                        Console.WriteLine("[DEBUG] WARNING: No threads with X>=128 or Y>=96 found in debug region!");
                    }

                    // Use TOTAL sum to avoid zero-padding confusion
                    double backFftTotal = 0;
                    foreach (var t in backFftData)
                    {
                        backFftTotal += Math.Abs(t);
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After backward_fft: TOTAL={backFftTotal:F2}, mean={backFftTotal/backFftData.Length:F4}");
                    if (backFftTotal < 0.01)
                    {
                        Console.WriteLine($"[WARNING] Backward FFT produced near-zero output!");
                    }
                }

                // DEBUG DUMP: Merged RGBA output (after backward FFT, before reduce_artifacts)
                _debugHelper.DumpRgbaTexture(outputTextureRgba, $"step_4_iter{iteration}_merged_before_reduce", refImage);

                // Reduce tile border artifacts
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Reducing artifacts...");
                var bayerTilesX = nTilesX;
                var bayerTilesY = nTilesY;

                // DEBUG: Analyze tile border values BEFORE reduce_artifacts
                {
                    var preArtifactData = outputTextureRgba.GetData<float>();
                    _debugHelper.AnalyzeTileBorders(preArtifactData, rgbaWidth, rgbaHeight, tileSizeMerge, iteration, "BEFORE (merged)");

                    // Also analyze reference texture for comparison
                    var refData = rgbaRefTexture.GetData<float>();
                    _debugHelper.AnalyzeTileBorders(refData, rgbaWidth, rgbaHeight, tileSizeMerge, iteration, "REFERENCE");
                }

                // reduce_artifacts_tile_border: FIXED - now uses tile-based dispatch matching Swift
                // Previously caused 8x8 grid pattern due to per-pixel dispatch model mismatch
                ExecuteReduceArtifacts(outputTextureRgba, rgbaRefTexture, bayerTilesX, bayerTilesY, tileSizeMerge, refImage.BlackLevel);

                // DEBUG: Analyze tile border values AFTER reduce_artifacts
                {
                    var artifactData = outputTextureRgba.GetData<float>();
                    _debugHelper.AnalyzeTileBorders(artifactData, rgbaWidth, rgbaHeight, tileSizeMerge, iteration, "AFTER");

                    // Use TOTAL sum to avoid zero-padding confusion
                    double artifactTotal = 0;
                    foreach (var t in artifactData)
                    {
                        artifactTotal += Math.Abs(t);
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After reduce_artifacts: TOTAL={artifactTotal:F2}, mean={artifactTotal/artifactData.Length:F4}");
                }

                // DEBUG DUMP: Merged RGBA output (after reduce_artifacts)
                _debugHelper.DumpRgbaTexture(outputTextureRgba, $"step_5_iter{iteration}_merged_after_reduce", refImage);

                // Convert RGBA → Bayer
                // Bayer dimensions are 2x RGBA dimensions
                var bayerWidth = rgbaWidth * 2;
                var bayerHeight = rgbaHeight * 2;
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Converting to Bayer ({bayerWidth}x{bayerHeight})...");
                using var outputTextureBayer = new VulkanImage(_ctx, (uint)bayerWidth, (uint)bayerHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteConvertToBayer(outputTextureRgba, outputTextureBayer, refImage.CfaPattern);

                // DEBUG: Check convert_to_bayer output (use TOTAL to avoid zero-padding confusion)
                {
                    var bayerData = outputTextureBayer.GetData<float>();
                    double bayerTotal = 0;
                    foreach (var t in bayerData)
                    {
                        bayerTotal += Math.Abs(t);
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After convert_to_bayer: TOTAL={bayerTotal:F2}, mean={bayerTotal/bayerData.Length:F4}");
                    if (bayerTotal < 0.01)
                    {
                        Console.WriteLine($"[WARNING] Convert to Bayer produced near-zero output!");
                    }
                }

                // DEBUG DUMP: Bayer output right after convert_to_bayer (before exposure correction/accumulation)
                // This isolates whether 16-pixel mirroring is introduced by FFT/merge or by convert_to_bayer
                _debugHelper.DumpTexture(outputTextureBayer, $"step_5b_iter{iteration}_bayer_after_convert", refImage, bayerWidth, bayerHeight, 0);

                // Add to final accumulator
                // Swift does: crop_texture(convert_to_bayer(output_texture), pad_left-crop_merge_x, ...)
                // This crops the Bayer output to remove iteration-specific padding before adding to accumulator.
                //
                // Crop amounts (Swift: frequency.swift line 204):
                //   cropLeft = pad_left - crop_merge_x = (padAlignX + shiftLeft) - cropMergeX
                //   cropRight = pad_right - crop_merge_x = (padAlignX + shiftRight) - cropMergeX
                //   cropTop = pad_top - crop_merge_y = (padAlignY + shiftTop) - cropMergeY
                //   cropBottom = pad_bottom - crop_merge_y = (padAlignY + shiftBottom) - cropMergeY
                //
                // Since padMergeX = padAlignX - cropMergeX (computed outside loop), we have:
                //   cropLeft = padMergeX + shiftLeft
                //   cropRight = padMergeX + shiftRight
                //   etc.
                var cropLeft = padMergeX + shiftLeft;
                var cropRight = padMergeX + shiftRight;
                var cropTop = padMergeY + shiftTop;
                var cropBottom = padMergeY + shiftBottom;

                // Now that convert_to_rgba uses cropMergeX/cropMergeY (constant), the Bayer output
                // contains the full processed region. Swift then crops to get back to original image size.
                //
                // Swift crop: crop_texture(bayer, cropLeft, cropRight, cropTop, cropBottom)
                // where cropLeft = padMergeX + shiftLeft, etc.
                //
                // The cropped region is:
                //   x: from cropLeft to (bayerWidth - cropRight) = width
                //   y: from cropTop to (bayerHeight - cropBottom) = height
                //
                // We read from source region [cropLeft, cropTop] with size (width x height)
                // and write to accumulator at fixed position [padAlignX, padAlignY]

                // Verify crop math: bayerWidth - cropLeft - cropRight should equal original width
                var croppedWidth = bayerWidth - cropLeft - cropRight;
                var croppedHeight = bayerHeight - cropTop - cropBottom;
                Console.WriteLine($"[DEBUG] Iteration {iteration}: Crop: left={cropLeft}, right={cropRight}, top={cropTop}, bottom={cropBottom}");
                Console.WriteLine($"[DEBUG] Iteration {iteration}: BayerSize={bayerWidth}x{bayerHeight}, CroppedSize={croppedWidth}x{croppedHeight}, ExpectedSize={width}x{height}");

                if (croppedWidth != width || croppedHeight != height)
                {
                    Console.WriteLine($"[WARNING] Crop size mismatch! Expected {width}x{height}, got {croppedWidth}x{croppedHeight}");
                }

                var iterOutput = outputTextureBayer.GetData<float>();

                // DEBUG: Check iteration output
                double iterSum = 0;
                for (var i = 0; i < Math.Min(iterOutput.Length, 100000); i++)
                {
                    iterSum += iterOutput[i];
                }
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} output: sum={iterSum:F2}, mean={iterSum/Math.Min(iterOutput.Length, 100000):F2}");

                // DEBUG: Track per-iteration values at specific positions to verify window sum = 1.0
                {
                    // Track a few specific RGBA positions across all 4 iterations
                    // These are FINAL output positions (after crop), so they should align across iterations
                    var trackY = 60; // Final RGBA Y position to track

                    // Store values in static-like storage for cross-iteration tracking
                    // We'll track RGBA positions 0-7 to cover a full tile width
                    var iterKey = $"iter{iteration}";
                    Console.WriteLine($"[WEIGHT_SUM] Iteration {iteration}: cropLeft={cropLeft} Bayer = {cropLeft/2} RGBA");

                    // For each final RGBA X position 0-7, find where it comes from in this iteration
                    for (var finalX = 0; finalX < 8; finalX++)
                    {
                        // The final RGBA position maps to Bayer position (finalX*2, trackY*2)
                        // in the accumulator. But we need to find it in THIS iteration's output.

                        // In accumulator: destination is at (padAlignX + finalX*2, padAlignY + trackY*2) in Bayer
                        // In this iteration's Bayer output: source is at (cropLeft + finalX*2, cropTop + trackY*2)
                        var srcBayerX = cropLeft + finalX * 2;
                        var srcBayerY = cropTop + trackY * 2;

                        // Get the 2x2 Bayer block sum (RGBA equivalent)
                        double p0 = iterOutput[srcBayerY * bayerWidth + srcBayerX];
                        double p1 = iterOutput[srcBayerY * bayerWidth + srcBayerX + 1];
                        double p2 = iterOutput[(srcBayerY+1) * bayerWidth + srcBayerX];
                        double p3 = iterOutput[(srcBayerY+1) * bayerWidth + srcBayerX + 1];
                        var rgbaSum = p0 + p1 + p2 + p3;

                        // What tile-relative position does this come from?
                        // In RGBA space: srcRgbaX = srcBayerX/2 - cropMergeX
                        var srcRgbaX = srcBayerX / 2; // Position in RGBA texture (before any crop)
                        var tileRelX = srcRgbaX % 8;  // Position within 8x8 RGBA tile

                        Console.WriteLine($"[WEIGHT_SUM]   FinalX={finalX}: srcBayer={srcBayerX}, srcRgba={srcRgbaX}, tileRel={tileRelX}, sum={rgbaSum:F1}");
                    }
                }

                var accData = finalAccumulator.GetData<float>();

                // DEBUG: Check what GetData returns at start of iteration
                {
                    var dataStartIdx = padAlignY * accWidth + padAlignX;
                    double preSum = 0;
                    var samples = Math.Min(10000, accData.Length - dataStartIdx);
                    for (var i = 0; i < samples; i++)
                    {
                        preSum += Math.Abs(accData[dataStartIdx + i]);
                    }
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: GetData BEFORE accumulation: sum={preSum:F2}");
                }

                // Copy CROPPED region from iteration output to accumulator
                // Source: read from [cropLeft, cropTop] in Bayer output
                // Dest: write to [padAlignX, padAlignY] in accumulator
                var pixelsUpdated = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        // Read from cropped region of source
                        var srcX = cropLeft + x;
                        var srcY = cropTop + y;
                        var srcIdx = srcY * bayerWidth + srcX;

                        // Write to fixed position in accumulator
                        var dstIdx = (y + padAlignY) * accWidth + (x + padAlignX);

                        if (srcIdx >= iterOutput.Length || dstIdx >= accData.Length)
                        {
                            continue;
                        }
                        accData[dstIdx] += iterOutput[srcIdx];
                        pixelsUpdated++;
                    }
                }
                finalAccumulator.SetData(accData);
                Console.WriteLine($"[VulkanComputePipeline] Updated {pixelsUpdated} pixels in accumulator");
                
                // DEBUG: Verify accumulator has data after SetData
                {
                    var verifyData = finalAccumulator.GetData<float>();
                    
                    // Sample from DATA REGION (after padding offset), not from start (which is padding)
                    var dataStartIdx = padAlignY * accWidth + padAlignX;
                    double verifySum = 0;
                    var samples = Math.Min(10000, verifyData.Length - dataStartIdx);
                    for (var i = 0; i < samples; i++)
                    {
                        verifySum += Math.Abs(verifyData[dataStartIdx + i]);
                    }
                    Console.WriteLine($"[DEBUG] After SetData: accumulator data region sum={verifySum:F2} (at offset {dataStartIdx})");
                    
                    // Also verify first few actual values
                    if (samples > 0)
                    {
                        Console.WriteLine($"[DEBUG] First 5 values at data region: {verifyData[dataStartIdx]:F4}, {verifyData[dataStartIdx+1]:F4}, {verifyData[dataStartIdx+2]:F4}, {verifyData[dataStartIdx+3]:F4}, {verifyData[dataStartIdx+4]:F4}");
                    }
                    
                    // Check CPU array at same location
                    double cpuSum = 0;
                    for (var i = 0; i < samples; i++)
                    {
                        cpuSum += Math.Abs(accData[dataStartIdx + i]);
                    }
                    Console.WriteLine($"[DEBUG] CPU accData data region sum={cpuSum:F2}");
                }

                // Cleanup ref pyramid
                foreach (var lvl in refPyramid)
                {
                    if (lvl != preparedRef) lvl.Dispose();
                }

                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} complete");
            }

            // Use finalAccumulator as the merged result
            Console.WriteLine("[VulkanComputePipeline] All 4 iterations complete");

            // DEBUG: Check accumulator BEFORE noise estimation
            {
                var preNoiseData = finalAccumulator.GetData<float>();
                var dataStartIdx = padAlignY * accWidth + padAlignX;
                double preNoiseSum = 0;
                var samples = Math.Min(10000, preNoiseData.Length - dataStartIdx);
                for (var i = 0; i < samples; i++)
                {
                    preNoiseSum += Math.Abs(preNoiseData[dataStartIdx + i]);
                }
                Console.WriteLine($"[DEBUG] Accumulator BEFORE noise estimation: sum={preNoiseSum:F2}");
            }

            estimatedNoiseSd = ExecuteNoiseEstimationGpu(finalAccumulator, refImage.MosaicPatternWidth);

            // DEBUG: Check accumulator AFTER noise estimation
            {
                var postNoiseData = finalAccumulator.GetData<float>();
                var dataStartIdx = padAlignY * accWidth + padAlignX;
                double postNoiseSum = 0;
                var samples = Math.Min(10000, postNoiseData.Length - dataStartIdx);
                for (var i = 0; i < samples; i++)
                {
                    postNoiseSum += Math.Abs(postNoiseData[dataStartIdx + i]);
                }
                Console.WriteLine($"[DEBUG] Accumulator AFTER noise estimation: sum={postNoiseSum:F2}");
            }

            // Download result from final accumulator
            Console.WriteLine($"[VulkanComputePipeline] Downloading from finalAccumulator: Width={finalAccumulator.Width}, Height={finalAccumulator.Height}");
            floatData = finalAccumulator.GetData<float>();
            Console.WriteLine($"[VulkanComputePipeline] Downloaded {floatData.Length} floats (expected {finalAccumulator.Width * finalAccumulator.Height})");

            // DEBUG: Check if data is all zeros
            // Check data region only (skip padding which is all zeros)
            var dataStartIdx2 = padAlignY * accWidth + padAlignX;
            double sum = 0;
            double absSum = 0;
            var min = double.MaxValue;
            var max = double.MinValue;
            var dataRegionSize = Math.Min(width * height, floatData.Length - dataStartIdx2);
            for (var i = 0; i < dataRegionSize; i++)
            {
                var val = floatData[dataStartIdx2 + i];
                sum += val;
                absSum += Math.Abs(val);
                
                if (val < min)
                {
                    min = val;
                }

                if (val > max)
                {
                    max = val;
                }
            }
            Console.WriteLine($"[VulkanComputePipeline] FinalAccumulator stats (data region): sum={sum:F2}, absSum={absSum:F2}, min={min:F2}, max={max:F2}, mean={sum/dataRegionSize:F2}");

            // DEBUG: Analyze tile boundary patterns in final Bayer accumulator
            // Check if there are systematic low/high values at tile boundaries
            // Bayer tile boundaries are at multiples of (tile_size_merge * 2) = 16 pixels
            _debugHelper.AnalyzeBayerTileBoundaries(floatData, accWidth, accHeight, padAlignX, padAlignY, tileSizeMerge * 2);

            // Update dimensions for exposure correction and cropping
            outWidth = accWidth;
            outHeight = accHeight;
            pad = padAlignX; // Update pad for cropping later (using X padding, should equal Y for square padding)
        }
        else
        {
            // Spatial
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
            // Spatial mode: merge loop
            Console.WriteLine($"[VulkanComputePipeline] Estimated Noise SD: {estimatedNoiseSd:F4}");

            for (var i = 0; i < input.Images.Count; i++)
            {
                if (i == input.ReferenceFrameIndex)
                {
                    continue;
                }

                Console.WriteLine($"[VulkanComputePipeline] Aligning Image {i}...");
                var altImage = input.Images[i];

                // 1. Upload Alternate
                 using var rawAlt = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint,
                    ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                 rawAlt.SetData(altImage.Data);

                 // 2. Prepare Alternate
                 using var preparedAlt = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                 ExecutePrepare(rawAlt, preparedAlt, altImage, pad, pad);

                 // 3. Pyramid Alternate (Spatial Loop)
                 var altPyramid = new List<VulkanImage>();
                 
                 var l0AltSw = (int)preparedAlt.Width / 2;
                 var l0AltSh = (int)preparedAlt.Height / 2;
                 
                 if (l0AltSw % 2 != 0)
                 {
                     l0AltSw++;
                 }

                 if (l0AltSh % 2 != 0)
                 {
                     l0AltSh++;
                 }
                 
                 var altLevel0S = new VulkanImage(_ctx, (uint)l0AltSw, (uint)l0AltSh, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                 ExecuteAvgPool(preparedAlt, altLevel0S, 2, altImage);
                 altPyramid.Add(altLevel0S);

                 var currW = l0AltSw;
                 var currH = l0AltSh;
                 for (var val = 1; val < 4; val++)
                 {
                    var nW = currW / 2;
                    if (nW % 2 != 0)
                    {
                        nW++;
                    }
                    
                    var nH = currH / 2;
                    if (nH % 2 != 0)
                    {
                        nH++;
                    }
                    var lvl = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(altPyramid[val-1], lvl, 2, altImage);
                    altPyramid.Add(lvl);
                    currW = nW; currH = nH;
                 }

                 // 4. Align
                 var alignment = new VulkanImage(_ctx, (uint)tileInfo.NTilesX, (uint)tileInfo.NTilesY, Format.R16G16B16A16Sint,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                 ExecuteAlignmentSearch(pyramid, altPyramid, alignment, tileInfo, 2);
                 disposables.Add(alignment);

                 // 5. Warp
                 Console.WriteLine($"[VulkanComputePipeline] Warping Image {i}...");
                 var warpedAlt = new VulkanImage(_ctx, preparedAlt.Width, preparedAlt.Height, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                 ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo, pad, pad);

                 // 6. Merge (Spatial only)
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
             // For frequency mode, preparedTexture might be wrong size, so allocate a new texture
             using var exposureTexture = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
             exposureTexture.SetData(floatData);
             ExecuteExposureCorrection(exposureTexture, options.ExposureControl, refImage);
             floatData = exposureTexture.GetData<float>();

             // DEBUG: Dump after exposure correction
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

        Console.WriteLine($"[VulkanComputePipeline] Cropping: width={width}, height={height}, outWidth={outWidth}, outHeight={outHeight}, pad={pad}, floatData.Length={floatData.Length}");
        Console.WriteLine($"[VulkanComputePipeline] Expected size: {outWidth * outHeight}, Actual size: {floatData.Length}");

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
    
    // Implement EnsurePreparePipeline
    private DescriptorSetLayout _prepareLayout;

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

    // NOTE: EnsureAlignPipeline, EnsureMergePipeline, EnsureNoiseEstPipeline removed - kernels now in sub-pipelines

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
        EnsureConversionPipeline();
        _conversionHelper.ConvertToRgba(bayerInput, rgbaOutput, cfaPattern, _kernelConvertToRgba!, _conversionLayout, cropX, cropY);
    }

    private void ExecuteConvertToBayer(VulkanImage rgbaInput, VulkanImage bayerOutput, int[] cfaPattern)
    {
        EnsureConversionPipeline();
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
        EnsurePreparePipeline();
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

