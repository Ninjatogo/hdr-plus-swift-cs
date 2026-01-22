using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace BurstPhoto.Rendering.Implementations;

public unsafe class VulkanComputePipeline : IComputePipeline
{
    private readonly VulkanContext _ctx;
    private readonly VulkanShaderCompiler _compiler;
    private readonly VulkanDescriptorManager _descriptors;
    
    // Shader Cache
    private ComputeKernel? _kernelPrepareBayer;
    private ComputeKernel? _kernelAvgPool;
    private ComputeKernel? _kernelTileDiff;
    private ComputeKernel? _kernelTileDiff25;
    private ComputeKernel? _kernelTileDiffExposure25;
    private ComputeKernel? _kernelFindBest;
    private ComputeKernel? _kernelWarp;
    private ComputeKernel? _kernelColorDiff;
    private ComputeKernel? _kernelMergeWeight;
    private ComputeKernel? _kernelAddWeighted;
    private ComputeKernel? _kernelAddWeightOnly;
    private ComputeKernel? _kernelAddExposure;
    private ComputeKernel? _kernelAddHighlights;
    private ComputeKernel? _kernelBlurMosaic;
    private ComputeKernel? _kernelColorDiffSuperpixel;
    private ComputeKernel? _kernelSumColumns;
    private ComputeKernel? _kernelSumRows;
    
    private DescriptorSetLayout _noiseEstLayout;
    
    private DescriptorSetLayout _alignLayout;
    private DescriptorSetLayout _mergeLayout;
    private DescriptorSetLayout _accumLayout;
    private DescriptorSetLayout _accumHighLayout; // For add_texture_highlights (needs extra binding u13)

    // Frequency Merge Kernels
    private ComputeKernel? _kernelRms;
    private ComputeKernel? _kernelAbsDiff; // Added
    private ComputeKernel? _kernelMismatch;
    private ComputeKernel? _kernelHighlightsNorm;
    private ComputeKernel? _kernelNormalizeMismatch;
    private ComputeKernel? _kernelForwardFft;
    private ComputeKernel? _kernelBackwardFft;
    private ComputeKernel? _kernelMergeFrequency;
    private ComputeKernel? _kernelDeconvoluteFrequency;
    private ComputeKernel? _kernelArtifactsTileBorder;
    
    private DescriptorSetLayout _frequencyLayout;

    // Bayer <-> RGBA Conversion Kernels (for FFT pipeline)
    private ComputeKernel? _kernelConvertToRgba;
    private ComputeKernel? _kernelConvertToBayer;
    private DescriptorSetLayout _conversionLayout;
    
    // Constants
    private const int TILE_SIZE_DEFAULT = 32; 
    
    // Debug: Set to true to save intermediate textures as DNG files
    public bool EnableDebugDump { get; set; } = false;
    private string _debugOutputDir = "DebugOutput";

    public VulkanComputePipeline(VulkanContext ctx)
    {
        _ctx = ctx;
        _compiler = new VulkanShaderCompiler();
        // Increase descriptor pool size for 4-iteration frequency domain merge
        // Each iteration creates ~20-30 descriptor sets (pyramids, textures, etc.)
        _descriptors = new VulkanDescriptorManager(_ctx, maxSets: 500);
    }
    
    /// <summary>
    /// Dumps a VulkanImage to a DNG file for debugging purposes.
    /// For single-channel (R32Sfloat) textures, outputs directly.
    /// For multi-channel (RGBA) textures, extracts the first channel.
    /// </summary>
    private void DebugDump(VulkanImage image, string stepName, RawImage refMeta, int outWidth, int outHeight, int pad)
    {
        if (!EnableDebugDump) return;
        
        try
        {
            // Ensure output directory exists
            if (!Directory.Exists(_debugOutputDir))
            {
                Directory.CreateDirectory(_debugOutputDir);
            }
            
            string outputPath = Path.Combine(_debugOutputDir, $"{stepName}.dng");
            Console.WriteLine($"[DebugDump] Saving {stepName} to {outputPath}...");
            
            // Get float data from the image
            float[] floatData;
            bool isRgba = image.Format == Format.R32G32B32A32Sfloat;
            
            if (isRgba)
            {
                // For RGBA textures (like FFT results), extract just the first channel
                var rgba = image.GetData<float>();
                int pixelCount = (int)(image.Width * image.Height);
                floatData = new float[pixelCount];
                for (int i = 0; i < pixelCount; i++)
                {
                    floatData[i] = rgba[i * 4]; // Take R channel only
                }
            }
            else
            {
                floatData = image.GetData<float>();
            }
            
            // Convert float to ushort, cropping to original dimensions
            int width = refMeta.Width;
            int height = refMeta.Height;
            var outputData = new ushort[width * height];
            
            int srcWidth = (int)image.Width;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = (y + pad) * srcWidth + (x + pad);
                    int dstIdx = y * width + x;
                    
                    if (srcIdx >= 0 && srcIdx < floatData.Length)
                    {
                        float val = floatData[srcIdx];
                        outputData[dstIdx] = (ushort)Math.Clamp(val, 0, 65535);
                    }
                }
            }
            
            // Create a RawImage for the DNG writer
            var debugImage = new RawImage
            {
                Width = width,
                Height = height,
                Data = outputData,
                MosaicPatternWidth = refMeta.MosaicPatternWidth,
                WhiteLevel = refMeta.WhiteLevel,
                BlackLevel = refMeta.BlackLevel,
                ExposureBias = refMeta.ExposureBias,
                IsoExposureTime = refMeta.IsoExposureTime,
                ColorFactors = refMeta.ColorFactors,
                SourcePath = refMeta.SourcePath, // Critical for DngSdkWriter
                CfaPattern = refMeta.CfaPattern,
                ColorMatrix1 = refMeta.ColorMatrix1,
                ColorMatrix2 = refMeta.ColorMatrix2,
                CalibrationIlluminant1 = refMeta.CalibrationIlluminant1,
                CalibrationIlluminant2 = refMeta.CalibrationIlluminant2,
                AsShotNeutral = refMeta.AsShotNeutral,
                CameraMake = refMeta.CameraMake,
                CameraModel = refMeta.CameraModel,
                IsBayerData = refMeta.IsBayerData
            };
            
            // Write using DngSdkWriter
            using var writer = new DngSdkWriter();
            writer.Write(outputPath, debugImage);
            
            Console.WriteLine($"[DebugDump] Saved {stepName} successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DebugDump] Error saving {stepName}: {ex.Message}");
        }
    }

    public async Task<RawImage> ProcessAsync(RenderingInput input, ProcessingOptions options, ProcessingProgress progress)
    {
        Console.WriteLine("[VulkanComputePipeline] Starting processing...");
        
        // Enable debug dump if requested
        EnableDebugDump = options.EnableDebugDump;
        if (EnableDebugDump)
        {
            Console.WriteLine("[VulkanComputePipeline] DEBUG DUMP ENABLED - intermediate DNGs will be saved to DebugOutput/");
        }
        
        // 1. Compile Shaders
        // 1. Shaders compiled on demand
        // CompileShaders(); removed
        
        // 2. Setup Reference Frame
        var refImage = input.Images[input.ReferenceFrameIndex];
        int width = refImage.Width;
        int height = refImage.Height;
        
        // Calculate Padded Dimensions for Alignment
        int tileSize = ProcessingOptions.GetTileSizePixels(options.TileSize);
        int pad;
        int outWidth = 0;
        int outHeight = 0;

        bool isFrequency = options.Merging == MergingAlgorithm.HigherQuality;

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
            
        // 4. Upload Reference Frame
        Console.WriteLine("[VulkanComputePipeline] Uploading Reference Frame...");
        rawTexture.SetData(refImage.Data);
        
        // 5. Execute Prepare Pass
        Console.WriteLine("[VulkanComputePipeline] Executing Prepare Pass...");
        ExecutePrepare(rawTexture, preparedTexture, refImage, pad, pad);
        
        // DEBUG: Dump after Prepare
        DebugDump(preparedTexture, "step_1_prepare", refImage, outWidth, outHeight, pad);
        
        progress.ProgressInt += 50_000_000;

        // 5b. Alignment Pyramid
        Console.WriteLine("[VulkanComputePipeline] Generating Alignment Pyramid...");
        var pyramid = new List<VulkanImage>();
        
        // Level 0: Downsampled from preparedTexture by 2
        int l0W = (int)preparedTexture.Width / 2;
        int l0H = (int)preparedTexture.Height / 2;
        if (l0W % 2 != 0) l0W++;
        if (l0H % 2 != 0) l0H++;
        
        var level0 = new VulkanImage(_ctx, (uint)l0W, (uint)l0H, Format.R32Sfloat, 
             ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
        ExecuteAvgPool(preparedTexture, level0, 2, refImage);
        pyramid.Add(level0);
        
        int currentW = l0W;
        int currentH = l0H;
        
        // Create 3 more levels
        for (int i = 1; i < 4; i++)
        {
            int nextW = currentW / 2; if (nextW % 2 != 0) nextW++;
            int nextH = currentH / 2; if (nextH % 2 != 0) nextH++;
            
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
        VulkanImage? pixelAccum = null;
        VulkanImage? weightAccum = null;

        float estimatedNoiseSd = 0;
        float[] floatData;
        
        if (isFrequency)
        {
            Console.WriteLine("[VulkanComputePipeline] === FREQUENCY DOMAIN MERGE (4-ITERATION) ===");

            EnsureMergeFrequencyPipeline();
            EnsureConversionPipeline();

            // CRITICAL: Swift hardcodes tile_size_merge = 8 for FFT merging
            const int tile_size_merge = 8;

            // Calculate alignment padding (from frequency.swift lines 80-97)
            // The tile factor accounts for pyramid downscaling
            int[] downscaleFactors = new[] { refImage.MosaicPatternWidth, 2, 2, 2 }; // Mosaic, then 3 levels of 2x
            int tileFactor = tileSize * downscaleFactors.Aggregate(1, (a, b) => a * b);

            int padAlignX = (int)Math.Ceiling((float)(width + tile_size_merge) / tileFactor);
            padAlignX = (padAlignX * tileFactor - width - tile_size_merge) / 2;

            int padAlignY = (int)Math.Ceiling((float)(height + tile_size_merge) / tileFactor);
            padAlignY = (padAlignY * tileFactor - height - tile_size_merge) / 2;

            // Calculate merge padding (smaller margin for FFT processing)
            int cropMergeX = (int)Math.Floor((float)padAlignX / (2 * tile_size_merge));
            cropMergeX = cropMergeX * 2 * tile_size_merge;
            int cropMergeY = (int)Math.Floor((float)padAlignY / (2 * tile_size_merge));
            cropMergeY = cropMergeY * 2 * tile_size_merge;

            int padMergeX = padAlignX - cropMergeX;
            int padMergeY = padAlignY - cropMergeY;

            Console.WriteLine($"[VulkanComputePipeline] Padding: Align=({padAlignX},{padAlignY}), Merge=({padMergeX},{padMergeY}), Crop=({cropMergeX},{cropMergeY})");

            // Allocate final accumulator (Bayer domain, accumulates 4 iteration outputs)
            int accWidth = width + 2 * padAlignX;
            int accHeight = height + 2 * padAlignY;
            Console.WriteLine($"[VulkanComputePipeline] Creating finalAccumulator: {accWidth}x{accHeight} (pad={padAlignX},{padAlignY})");
            var finalAccumulator = new VulkanImage(_ctx, (uint)accWidth, (uint)accHeight, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            FillWithZeros(finalAccumulator);
            Console.WriteLine($"[VulkanComputePipeline] finalAccumulator created: Width={finalAccumulator.Width}, Height={finalAccumulator.Height}");
            disposables.Add(finalAccumulator);

            // === 4-ITERATION LOOP (Swift: frequency.swift line 108) ===
            for (int iteration = 1; iteration <= 4; iteration++)
            {
                Console.WriteLine($"\n[VulkanComputePipeline] === ITERATION {iteration}/4 ===");

                // Calculate shift values (Swift: frequency.swift lines 111-120)
                int shiftLeft   = (iteration % 2 == 0) ? tile_size_merge : 0;
                int shiftRight  = (iteration % 2 == 1) ? tile_size_merge : 0;
                int shiftTop    = (iteration < 3) ? tile_size_merge : 0;
                int shiftBottom = (iteration >= 3) ? tile_size_merge : 0;

                int padLeft   = padAlignX + shiftLeft;
                int padRight  = padAlignX + shiftRight;
                int padTop    = padAlignY + shiftTop;
                int padBottom = padAlignY + shiftBottom;

                int iterOutWidth = width + padLeft + padRight;
                int iterOutHeight = height + padTop + padBottom;

                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Shift=({shiftLeft-shiftRight},{shiftTop-shiftBottom}), Pad=({padLeft},{padRight},{padTop},{padBottom}), Size={iterOutWidth}x{iterOutHeight}");

                // Prepare reference with iteration-specific padding
                using var preparedRef = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);

                // DEBUG: Check raw input data before prepare
                {
                    int rawSum = 0;
                    int sampleSize = Math.Min(refImage.Data.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) rawSum += refImage.Data[i];
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: Raw input data: sum(first {sampleSize})={rawSum}, mean={rawSum/(double)sampleSize:F2}");
                }

                rawTexture.SetData(refImage.Data); // Re-upload to ensure clean state
                ExecutePrepare(rawTexture, preparedRef, refImage, padLeft, padTop);

                // DEBUG: Check prepared reference (sample from middle to avoid padding)
                {
                    float[] prepData = preparedRef.GetData<float>();
                    int startIdx = prepData.Length / 4; // Start from 25% into the image
                    int sampleSize = Math.Min(10000, prepData.Length - startIdx);
                    double prepSum = 0;
                    for (int i = 0; i < sampleSize; i++) prepSum += Math.Abs(prepData[startIdx + i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare: sum(mid {sampleSize})={prepSum:F2}, mean={prepSum/sampleSize:F4}");
                    // Also check specific region that should contain data (skip padding)
                    int rowStart = (padTop + height/2) * iterOutWidth + (padLeft + width/2);
                    double centerSum = 0;
                    for (int i = 0; i < Math.Min(1000, prepData.Length - rowStart); i++) centerSum += Math.Abs(prepData[rowStart + i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare (center region): sum={centerSum:F2}, mean={centerSum/1000.0:F4}");
                    if (prepSum < 0.01) Console.WriteLine($"[WARNING] Prepare produced near-zero output!");
                }

                // RGBA dimensions: Swift uses (BayerWidth - 2*cropMergeX) / 2
                // This crops out the padding border before superpixel packing
                int rgbaWidth = (iterOutWidth - 2 * cropMergeX) / 2;
                int rgbaHeight = (iterOutHeight - 2 * cropMergeY) / 2;
                int ftWidth = rgbaWidth * 2; // Complex storage (Real + Imaginary)
                int ftHeight = rgbaHeight;

                // Convert reference Bayer → RGBA
                using var rgbaRefTexture = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                Console.WriteLine($"[DEBUG] Iteration {iteration}: Converting reference to RGBA...");
                // Reference uses padLeft/padTop since that's where prepare placed the data
                ExecuteConvertToRgba(preparedRef, rgbaRefTexture, refImage.CfaPattern, padLeft, padTop);

                // DEBUG: Check RGBA conversion output (sample from middle)
                {
                    float[] rgbaData = rgbaRefTexture.GetData<float>();
                    int startIdx = rgbaData.Length / 4;
                    int sampleSize = Math.Min(10000, rgbaData.Length - startIdx);
                    double rgbaSum = 0;
                    for (int i = 0; i < sampleSize; i++) rgbaSum += Math.Abs(rgbaData[startIdx + i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After convert_to_rgba: sum(mid {sampleSize})={rgbaSum:F2}, mean={rgbaSum/sampleSize:F4}");
                    if (rgbaSum < 0.01) Console.WriteLine($"[WARNING] RGBA conversion produced near-zero output!");
                }

                // Build reference pyramid (Frequency Loop)
                var refPyramid = new List<VulkanImage>();
                int l0RefW = (int)preparedRef.Width / 2;
                int l0RefH = (int)preparedRef.Height / 2;
                if (l0RefW % 2 != 0) l0RefW++;
                if (l0RefH % 2 != 0) l0RefH++;
                
                var refLevel0 = new VulkanImage(_ctx, (uint)l0RefW, (uint)l0RefH, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteAvgPool(preparedRef, refLevel0, 2, refImage);
                refPyramid.Add(refLevel0);
                
                int currW = l0RefW, currH = l0RefH;
                for (int lvl = 1; lvl < 4; lvl++)
                {
                    int nW = currW / 2; if (nW % 2 != 0) nW++;
                    int nH = currH / 2; if (nH % 2 != 0) nH++;
                    var levelImg = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(refPyramid[lvl - 1], levelImg, 2, refImage);
                    refPyramid.Add(levelImg);
                    currW = nW; currH = nH;
                }

                // Calculate RMS per iteration (tile grid size)
                int nTilesX = (iterOutWidth - 2 * cropMergeX) / (2 * tile_size_merge);
                int nTilesY = (iterOutHeight - 2 * cropMergeY) / (2 * tile_size_merge);

                using var rmsTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                // Calculate RMS from reference RGBA texture
                ExecuteCalculateRms(rgbaRefTexture, rmsTexture, nTilesX, nTilesY, tile_size_merge);

                // Initialize total mismatch texture for this iteration
                using var totalMismatchTexture = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
                FillWithZeros(totalMismatchTexture);

                // Forward FFT on reference
                using var refFT = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                Console.WriteLine($"[DEBUG] Iteration {iteration}: Running forward FFT on reference...");
                ExecuteForwardFft(rgbaRefTexture, refFT, tile_size_merge, rgbaWidth, rgbaHeight);

                // DEBUG: Check FFT output
                {
                    float[] fftData = refFT.GetData<float>();
                    double fftSum = 0;
                    int sampleSize = Math.Min(fftData.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) fftSum += Math.Abs(fftData[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After forward_fft: sum(first {sampleSize})={fftSum:F2}, mean={fftSum/sampleSize:F4}");
                    if (fftSum < 0.01) Console.WriteLine($"[WARNING] Forward FFT produced near-zero output!");
                }

                // Initialize frequency domain accumulator
                using var finalTextureFT = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
                ExecuteCopyImage(refFT, finalTextureFT, ftWidth, ftHeight);

                // Estimate noise for this iteration
                estimatedNoiseSd = ExecuteNoiseEstimationGPU(preparedRef, refImage.MosaicPatternWidth);
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Estimated Noise SD = {estimatedNoiseSd:F4}");

                // === COMPARISON LOOP ===
                for (int compIdx = 0; compIdx < input.Images.Count; compIdx++)
                {
                    if (compIdx == input.ReferenceFrameIndex) continue;

                    var altImage = input.Images[compIdx];
                    Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Processing comparison image {compIdx}...");

                    // Prepare comparison frame with iteration-specific padding
                    using var rawAlt = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint,
                        ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                    rawAlt.SetData(altImage.Data);

                    using var preparedAlt = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    float expDiff = (float)(refImage.ExposureBias - altImage.ExposureBias);
                    // TODO: ExecutePrepare needs exposure bias parameter
                    ExecutePrepare(rawAlt, preparedAlt, altImage, padLeft, padTop);

                    // Build comparison pyramid (Frequency Loop)
                    var altPyramid = new List<VulkanImage>();
                    int l0AltW = (int)preparedAlt.Width / 2;
                    int l0AltH = (int)preparedAlt.Height / 2;
                    if (l0AltW % 2 != 0) l0AltW++;
                    if (l0AltH % 2 != 0) l0AltH++;
                    
                    var altLevel0 = new VulkanImage(_ctx, (uint)l0AltW, (uint)l0AltH, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    ExecuteAvgPool(preparedAlt, altLevel0, 2, altImage);
                    altPyramid.Add(altLevel0);
                    
                    currW = l0AltW; currH = l0AltH;
                    for (int lvl = 1; lvl < 4; lvl++)
                    {
                        int nW = currW / 2; if (nW % 2 != 0) nW++;
                        int nH = currH / 2; if (nH % 2 != 0) nH++;
                        var levelImg = new VulkanImage(_ctx, (uint)nW, (uint)nH, Format.R32Sfloat,
                            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                        ExecuteAvgPool(altPyramid[lvl - 1], levelImg, 2, altImage);
                        altPyramid.Add(levelImg);
                        currW = nW; currH = nH;
                    }

                    // Align and warp
                    using var alignment = new VulkanImage(_ctx, (uint)tileInfo.NTilesX, (uint)tileInfo.NTilesY, Format.R16G16B16A16Sint,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                    ExecuteAlignmentSearch(refPyramid, altPyramid, alignment, tileInfo, 2);

                    using var warpedAlt = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                    
                    // DEBUG: Check preparedAlt BEFORE warp
                    {
                        float[] prepAltData = preparedAlt.GetData<float>();
                        int dataStartIdx = padTop * iterOutWidth + padLeft;
                        double prepAltSum = 0;
                        int samples = Math.Min(1000, prepAltData.Length - dataStartIdx);
                        if (dataStartIdx >= 0 && dataStartIdx < prepAltData.Length)
                        {
                            for (int idx = 0; idx < samples; idx++) 
                                prepAltSum += Math.Abs(prepAltData[dataStartIdx + idx]);
                        }
                        Console.WriteLine($"[WARP DEBUG] preparedAlt BEFORE warp (at data region): sum={prepAltSum:F2}, mean={prepAltSum/samples:F4}");
                        if (prepAltSum < 0.01)
                        {
                            Console.WriteLine($"[WARP DEBUG] ❌ ERROR: preparedAlt is EMPTY before warp!");
                        }
                    }
                    
                    ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo);

                    // DEBUG: Check warpedAlt before RGBA conversion
                    {
                        float[] warpData = warpedAlt.GetData<float>();
                        double warpSum = 0;
                        int warpSamples = Math.Min(warpData.Length, 1000);
                        for (int i = 0; i < warpSamples; i++) warpSum += Math.Abs(warpData[i]);
                        
                        // Also check at the data region offset
                        int dataStartIdx = padTop * iterOutWidth + padLeft;
                        double warpDataSum = 0;
                        int samples = Math.Min(1000, warpData.Length - dataStartIdx);
                        if (dataStartIdx >= 0 && dataStartIdx < warpData.Length)
                        {
                            for (int idx = 0; idx < samples; idx++) 
                                warpDataSum += Math.Abs(warpData[dataStartIdx + idx]);
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
                    // Comparison uses padLeft/padTop - same as reference since warp preserves data location
                    ExecuteConvertToRgba(warpedAlt, alignedTextureRgba, refImage.CfaPattern, padLeft, padTop);

                    // DEBUG: Check alignedTextureRgba after RGBA conversion
                    {
                        float[] rgbaData = alignedTextureRgba.GetData<float>();
                        double rgbaSum = 0;
                        int rgbaSamples = Math.Min(rgbaData.Length, 1000);
                        for (int i = 0; i < rgbaSamples; i++) rgbaSum += Math.Abs(rgbaData[i]);
                        Console.WriteLine($"[WARP DEBUG] alignedTextureRgba AFTER convert: sum={rgbaSum:F2}, mean={rgbaSum/rgbaSamples:F4}");
                    }

                    // Calculate exposure factor
                    double exposureFactor = Math.Pow(2.0, (altImage.ExposureBias - refImage.ExposureBias) / 100.0);

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
                    using var alignedTextureFT = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat,
                        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

                    // Call modified merge (we'll pass RGBA aligned texture, not Bayer!)
                    // The ExecuteMergeFrequency will handle all the intermediate steps
                    ExecuteMergeFrequency(refFT, rgbaRefTexture, alignedTextureRgba, null!, finalTextureFT,
                        refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd, expDiff, tileSize, refImage.MosaicPatternWidth, 1);

                    // Cleanup alt pyramid
                    foreach (var lvl in altPyramid) if (lvl != preparedAlt) lvl.Dispose();
                }

                // Post-iteration processing
                // DEBUG: Check finalTextureFT before deconvolution
                {
                    float[] preDec = finalTextureFT.GetData<float>();
                    double preDecSum = 0;
                    int sampleSize = Math.Min(preDec.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) preDecSum += Math.Abs(preDec[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: Before deconvolution: sum(first {sampleSize})={preDecSum:F2}, mean={preDecSum/sampleSize:F4}");
                }

                // Deconvolute with accumulated mismatch
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Deconvolution...");
                ExecuteDeconvoluteFrequency(finalTextureFT, totalMismatchTexture, nTilesX, nTilesY, tile_size_merge);

                // DEBUG: Check finalTextureFT after deconvolution
                {
                    float[] postDec = finalTextureFT.GetData<float>();
                    double postDecSum = 0;
                    int sampleSize = Math.Min(postDec.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) postDecSum += Math.Abs(postDec[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After deconvolution: sum(first {sampleSize})={postDecSum:F2}, mean={postDecSum/sampleSize:F4}");
                    if (postDecSum < 0.01) Console.WriteLine($"[WARNING] Deconvolution produced near-zero output!");
                }

                // Backward FFT
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Backward FFT...");
                using var outputTextureRgba = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteBackwardFft(finalTextureFT, outputTextureRgba, input.Images.Count, tile_size_merge);

                // DEBUG: Check backward FFT output
                {
                    float[] backFftData = outputTextureRgba.GetData<float>();
                    double backFftSum = 0;
                    int sampleSize = Math.Min(backFftData.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) backFftSum += Math.Abs(backFftData[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After backward_fft: sum(first {sampleSize})={backFftSum:F2}, mean={backFftSum/sampleSize:F4}");
                    if (backFftSum < 0.01) Console.WriteLine($"[WARNING] Backward FFT produced near-zero output!");
                }

                // Reduce tile border artifacts
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Reducing artifacts...");
                int bayerTilesX = nTilesX;
                int bayerTilesY = nTilesY;
                ExecuteReduceArtifacts(outputTextureRgba, rgbaRefTexture, bayerTilesX, bayerTilesY, tile_size_merge * 2, refImage.BlackLevel);

                // DEBUG: Check after artifact reduction
                {
                    float[] artifactData = outputTextureRgba.GetData<float>();
                    double artifactSum = 0;
                    int sampleSize = Math.Min(artifactData.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) artifactSum += Math.Abs(artifactData[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After reduce_artifacts: sum(first {sampleSize})={artifactSum:F2}, mean={artifactSum/sampleSize:F4}");
                }

                // Convert RGBA → Bayer
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Converting to Bayer...");
                using var outputTextureBayer = new VulkanImage(_ctx, (uint)iterOutWidth, (uint)iterOutHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteConvertToBayer(outputTextureRgba, outputTextureBayer, refImage.CfaPattern);

                // DEBUG: Check convert_to_bayer output
                {
                    float[] bayerData = outputTextureBayer.GetData<float>();
                    double bayerSum = 0;
                    int sampleSize = Math.Min(bayerData.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) bayerSum += Math.Abs(bayerData[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After convert_to_bayer: sum(first {sampleSize})={bayerSum:F2}, mean={bayerSum/sampleSize:F4}");
                    if (bayerSum < 0.01) Console.WriteLine($"[WARNING] Convert to Bayer produced near-zero output!");
                }

                // Crop and add to final accumulator
                float[] iterOutput = outputTextureBayer.GetData<float>();

                // DEBUG: Check iteration output
                double iterSum = 0;
                for (int i = 0; i < Math.Min(iterOutput.Length, 100000); i++) iterSum += iterOutput[i];
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} output: sum={iterSum:F2}, mean={iterSum/Math.Min(iterOutput.Length, 100000):F2}");

                // Crop from iteration output (which has iteration-specific padding) to final accumulator coordinates
                float[] accData = finalAccumulator.GetData<float>();
                int pixelsUpdated = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int srcIdx = (y + padTop) * iterOutWidth + (x + padLeft);
                        int dstIdx = (y + padAlignY) * accWidth + (x + padAlignX);
                        accData[dstIdx] += iterOutput[srcIdx] / 4.0f; // Normalize by 4 iterations
                        pixelsUpdated++;
                    }
                }
                finalAccumulator.SetData(accData);
                Console.WriteLine($"[VulkanComputePipeline] Updated {pixelsUpdated} pixels in accumulator");

                // Cleanup ref pyramid
                foreach (var lvl in refPyramid) if (lvl != preparedRef) lvl.Dispose();

                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} complete");
            }

            // Use finalAccumulator as the merged result
            Console.WriteLine("[VulkanComputePipeline] All 4 iterations complete");
            estimatedNoiseSd = ExecuteNoiseEstimationGPU(finalAccumulator, refImage.MosaicPatternWidth);

            // Download result from final accumulator
            Console.WriteLine($"[VulkanComputePipeline] Downloading from finalAccumulator: Width={finalAccumulator.Width}, Height={finalAccumulator.Height}");
            floatData = finalAccumulator.GetData<float>();
            Console.WriteLine($"[VulkanComputePipeline] Downloaded {floatData.Length} floats (expected {finalAccumulator.Width * finalAccumulator.Height})");

            // DEBUG: Check if data is all zeros
            double sum = 0;
            double min = double.MaxValue;
            double max = double.MinValue;
            for (int i = 0; i < Math.Min(floatData.Length, 1000000); i++)
            {
                sum += floatData[i];
                if (floatData[i] < min) min = floatData[i];
                if (floatData[i] > max) max = floatData[i];
            }
            Console.WriteLine($"[VulkanComputePipeline] FinalAccumulator stats: sum={sum:F2}, min={min:F2}, max={max:F2}, mean={sum/Math.Min(floatData.Length, 1000000):F2}");

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
            float[] ones = new float[outWidth * outHeight];
            Array.Fill(ones, 1.0f);
            weightAccum.SetData(ones);
            
            disposables.Add(pixelAccum);
            disposables.Add(weightAccum);
            
            estimatedNoiseSd = ExecuteNoiseEstimationGPU(preparedTexture, refImage.MosaicPatternWidth);
            // Spatial mode: merge loop
            Console.WriteLine($"[VulkanComputePipeline] Estimated Noise SD: {estimatedNoiseSd:F4}");

            for (int i = 0; i < input.Images.Count; i++)
            {
                if (i == input.ReferenceFrameIndex) continue;

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
                 
                 int l0AltSW = (int)preparedAlt.Width / 2;
                 int l0AltSH = (int)preparedAlt.Height / 2;
                 if (l0AltSW % 2 != 0) l0AltSW++;
                 if (l0AltSH % 2 != 0) l0AltSH++;
                 
                 var altLevel0S = new VulkanImage(_ctx, (uint)l0AltSW, (uint)l0AltSH, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                 ExecuteAvgPool(preparedAlt, altLevel0S, 2, altImage);
                 altPyramid.Add(altLevel0S);

                 int currW = l0AltSW;
                 int currH = l0AltSH;
                 for (int val = 1; val < 4; val++)
                 {
                    int nW = currW / 2; if (nW % 2 != 0) nW++;
                    int nH = currH / 2; if (nH % 2 != 0) nH++;
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
                 ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo);

                 // 6. Merge (Spatial only)
                 Console.WriteLine($"[VulkanComputePipeline] Merging Image {i}...");
                 float expDiff = (float)(refImage.ExposureBias - altImage.ExposureBias);
                 ExecuteMerge(preparedTexture, warpedAlt, weightAccum!, pixelAccum!, refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd, expDiff);

                 // Cleanup Alt Pyramid
                 foreach(var p in altPyramid) if(p!=preparedAlt) p.Dispose();
                 warpedAlt.Dispose();
            }

            // Cleanup pyramid levels
            for(int i=1; i<pyramid.Count; i++) disposables.Add(pyramid[i]);

            // DEBUG: Dump after all merges complete
            DebugDump(pixelAccum!, "step_3_merge_accum_spatial", refImage, outWidth, outHeight, pad);

            // Normalize: result = pixelAccum / weightAccum
            float[] pixAcc = pixelAccum!.GetData<float>();
            float[] wAcc = weightAccum!.GetData<float>();
            floatData = new float[pixAcc.Length];
            for (int i = 0; i < pixAcc.Length; i++)
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
             DebugDump(exposureTexture, "step_6_exposure", refImage, outWidth, outHeight, pad);
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
        
        float factor16Bit = 1.0f;
        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            float maxVal = refImage.WhiteLevel;
            factor16Bit = (float)Math.Pow(2.0, 16.0 - Math.Ceiling(Math.Log2(maxVal)));
        }

        Console.WriteLine($"[VulkanComputePipeline] Cropping: width={width}, height={height}, outWidth={outWidth}, outHeight={outHeight}, pad={pad}, floatData.Length={floatData.Length}");
        Console.WriteLine($"[VulkanComputePipeline] Expected size: {outWidth * outHeight}, Actual size: {floatData.Length}");

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcIdx = (y + pad) * outWidth + (x + pad);
                int dstIdx = y * width + x;
                float val = floatData[srcIdx] * factor16Bit;
                outputImage.Data[dstIdx] = (ushort)Math.Clamp(val, 0, 65535);
            }
        }
        
        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            outputImage.WhiteLevel = (int)(refImage.WhiteLevel * factor16Bit);
            if (outputImage.WhiteLevel > 65535) outputImage.WhiteLevel = 65535;
        }
        
        foreach(var d in disposables) d.Dispose();
        
        return outputImage;
    }
    
    // Implement EnsurePreparePipeline
    private DescriptorSetLayout _prepareLayout;
    
    private void EnsurePreparePipeline()
    {
        if (_kernelPrepareBayer != null) return;

        // Create Layout
        // Layout for prepare_texture_bayer shader
        // Shader uses: InTextureUint (t1→Binding2), AuxTextureFloat (t3→Binding4), BlackLevels (t5→Binding6)
        _prepareLayout = _descriptors.CreateLayout(new[]
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // b0
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t0 (unused)
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t1 InTextureUint
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t2 (unused)
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t3 AuxTextureFloat
            new DescriptorSetLayoutBinding { Binding = 5, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t4 MeanTextureBuffer (unused)
            new DescriptorSetLayoutBinding { Binding = 6, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t5 BlackLevels
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // u10 OutTextureFloat
            new DescriptorSetLayoutBinding { Binding = 11, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // u11 (unused)
            new DescriptorSetLayoutBinding { Binding = 12, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }  // u12 (unused)
        });
        
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string shaderPath = Path.Combine(baseDir, "Shaders", "TextureOps.hlsl");
        string constantsPath = Path.Combine(baseDir, "Shaders", "Constants.hlsli");
        
        string source = File.ReadAllText(shaderPath);
        
        // Manual #include resolution
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // Rename entry point to CSMain to avoid Vulkan/Shaderc issues
        source = source.Replace("void prepare_texture_bayer(", "void CSMain(");
        
        var prepareSpirv = _compiler.Compile(source, "CSMain");
        _kernelPrepareBayer = new ComputeKernel(_ctx, _prepareLayout, prepareSpirv, "CSMain", 16, 16, 1);
    }

    private void EnsureConversionPipeline()
    {
        if (_kernelConvertToRgba != null) return;

        // Layout for conversion kernels (convert_to_rgba, convert_to_bayer)
        // Using same offset convention as rest of codebase: tN -> Binding N+1
        // b0: TextureParams (Binding 0)
        // t0: InTextureFloat -> Binding 1
        // t2: InTextureRGBA -> Binding 3
        // u10: OutTextureFloat -> Binding 10
        // u12: OutTextureRGBA -> Binding 12
        _conversionLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // b0
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t0
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t2
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // u10
            new DescriptorSetLayoutBinding { Binding = 12, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }  // u12
        });
        
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string shaderPath = Path.Combine(baseDir, "Shaders", "TextureOps.hlsl");
        string constantsPath = Path.Combine(baseDir, "Shaders", "Constants.hlsli");
        
        string source = File.ReadAllText(shaderPath);
        
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // convert_to_rgba: Bayer -> RGBA (superpixels)
        string sourceToRgba = source.Replace("void convert_to_rgba(", "void CSMain(");
        var toRgbaSpirv = _compiler.Compile(sourceToRgba, "CSMain");
        _kernelConvertToRgba = new ComputeKernel(_ctx, _conversionLayout, toRgbaSpirv, "CSMain", 16, 16, 1);
        
        // convert_to_bayer: RGBA -> Bayer
        string sourceToBayer = source.Replace("void convert_to_bayer(", "void CSMain(");
        var toBayerSpirv = _compiler.Compile(sourceToBayer, "CSMain");
        _kernelConvertToBayer = new ComputeKernel(_ctx, _conversionLayout, toBayerSpirv, "CSMain", 16, 16, 1);
    }

    private void EnsureAlignPipeline()
    {
        if (_kernelAvgPool != null) return;

        // Create Layout for Align.hlsl kernels (avg_pool uses b0, t0, u10)
        _alignLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            // t1, t2, u10 are also used in other kernels, but for avg_pool we need at least b0, t0(1), u10(10).
            // We can define the full union of bindings for the layout if we share it, or make specific layouts.
            // For simplicity, let's define the superset used by Align kernels, or at least enough for avg_pool.
            // Align.hlsl has:
            // b0: AlignParams
            // t0: InTexture / RefTexture / InTileDiff (3D) -> Binding 1
            // t1: CompTexture -> Binding 2
            // t2: PrevAlignment -> Binding 3
            // u10: OutTexture / TileDiff / OutAlignment / PrevCorrected -> Binding 10
            
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }
        });
        
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string shaderPath = Path.Combine(baseDir, "Shaders", "Align.hlsl");
        string constantsPath = Path.Combine(baseDir, "Shaders", "Constants.hlsli");
        
        string source = File.ReadAllText(shaderPath);
        
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // entry point rename for avg_pool
        string sourceAvg = source.Replace("void avg_pool(", "void CSMain(");
        
        var avgSpirv = _compiler.Compile(sourceAvg, "CSMain");
        _kernelAvgPool = new ComputeKernel(_ctx, _alignLayout, avgSpirv, "CSMain", 16, 16, 1);
        
        // compute_tile_differences (generic 3D dispatch)
        string sourceTileDiff = source.Replace("void compute_tile_differences(", "void CSMain(");
        var tileDiffSpirv = _compiler.Compile(sourceTileDiff, "CSMain");
        // [numthreads(8, 8, 4)]
        _kernelTileDiff = new ComputeKernel(_ctx, _alignLayout, tileDiffSpirv, "CSMain", 8, 8, 4);
        
        // compute_tile_differences25 (optimized for search_dist=2, 2D dispatch)
        string sourceTileDiff25 = source.Replace("void compute_tile_differences25(", "void CSMain(");
        var tileDiff25Spirv = _compiler.Compile(sourceTileDiff25, "CSMain");
        // [numthreads(16, 16, 1)]
        _kernelTileDiff25 = new ComputeKernel(_ctx, _alignLayout, tileDiff25Spirv, "CSMain", 16, 16, 1);
        
        // compute_tile_differences_exposure25 (optimized with exposure correction, 2D dispatch)
        string sourceTileDiffExp25 = source.Replace("void compute_tile_differences_exposure25(", "void CSMain(");
        var tileDiffExp25Spirv = _compiler.Compile(sourceTileDiffExp25, "CSMain");
        // [numthreads(16, 16, 1)]
        _kernelTileDiffExposure25 = new ComputeKernel(_ctx, _alignLayout, tileDiffExp25Spirv, "CSMain", 16, 16, 1);
        
        // find_best_tile_alignment
        string sourceFindBest = source.Replace("void find_best_tile_alignment(", "void CSMain(");
        var findBestSpirv = _compiler.Compile(sourceFindBest, "CSMain");
        // [numthreads(16, 16, 1)]
        _kernelFindBest = new ComputeKernel(_ctx, _alignLayout, findBestSpirv, "CSMain", 16, 16, 1);
        
        // warp_texture_bayer
        string sourceWarp = source.Replace("void warp_texture_bayer(", "void CSMain(");
        var warpSpirv = _compiler.Compile(sourceWarp, "CSMain");
        // [numthreads(16, 16, 1)]
        _kernelWarp = new ComputeKernel(_ctx, _alignLayout, warpSpirv, "CSMain", 16, 16, 1);

        // upsample_alignment
        string sourceUpsample = source.Replace("void upsample_alignment(", "void CSMain(");
        var upsampleSpirv = _compiler.Compile(sourceUpsample, "CSMain");
        // [numthreads(16, 16, 1)]
        _kernelUpsampleAlignment = new ComputeKernel(_ctx, _alignLayout, upsampleSpirv, "CSMain", 16, 16, 1);

        // correct_upsampling_error
        string sourceCorrect = source.Replace("void correct_upsampling_error(", "void CSMain(");
        var correctSpirv = _compiler.Compile(sourceCorrect, "CSMain");
        // [numthreads(16, 16, 1)]
        _kernelCorrectUpsamplingError = new ComputeKernel(_ctx, _alignLayout, correctSpirv, "CSMain", 16, 16, 1);
    }

    private void EnsureMergePipeline()
    {
        if (_kernelColorDiff != null) return;

        // Create Layout for MergeSpatial.hlsl kernels
        // b0: SpatialParams
        // t0: RefTexture (Binding 1)
        // t1: CompTexture (Binding 2)
        // t2: InDiff (Binding 3)
        // u10: OutDiff / OutWeight (Binding 10)
        _mergeLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }
        });
        
        // Create Layout for TextureOps.hlsl accumulation kernels (add_texture_weighted, add_weight_only)
        // b0: TextureParams (Binding 0) - may not be used but needed for layout compatibility
        // t0: InTextureFloat (Binding 1) - warped frame
        // t3: AuxTextureFloat (Binding 4) - weight texture  
        // u10: OutTextureFloat (Binding 10) - accumulator (RW)
        _accumLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }
        });
        
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string shaderPath = Path.Combine(baseDir, "Shaders", "MergeSpatial.hlsl");
        string constantsPath = Path.Combine(baseDir, "Shaders", "Constants.hlsli");
        
        string source = File.ReadAllText(shaderPath);
        
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // color_difference
        string sourceColorDiff = source.Replace("void color_difference(", "void CSMain(");
        var colorDiffSpirv = _compiler.Compile(sourceColorDiff, "CSMain");
        _kernelColorDiff = new ComputeKernel(_ctx, _mergeLayout, colorDiffSpirv, "CSMain", 16, 16, 1);
        
        // compute_merge_weight
        string sourceMergeW = source.Replace("void compute_merge_weight(", "void CSMain(");
        var mergeWSpirv = _compiler.Compile(sourceMergeW, "CSMain");
        _kernelMergeWeight = new ComputeKernel(_ctx, _mergeLayout, mergeWSpirv, "CSMain", 16, 16, 1);
        
        // Load TextureOps.hlsl for accumulation kernels
        string textureOpsPath = Path.Combine(baseDir, "Shaders", "TextureOps.hlsl");
        string textureOpsSource = File.ReadAllText(textureOpsPath);
        
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            textureOpsSource = textureOpsSource.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // add_texture_weighted
        string sourceAddWeighted = textureOpsSource.Replace("void add_texture_weighted(", "void CSMain(");
        var addWeightedSpirv = _compiler.Compile(sourceAddWeighted, "CSMain");
        _kernelAddWeighted = new ComputeKernel(_ctx, _accumLayout, addWeightedSpirv, "CSMain", 16, 16, 1);
        
        // add_weight_only
        string sourceAddWeightOnly = textureOpsSource.Replace("void add_weight_only(", "void CSMain(");
        var addWeightOnlySpirv = _compiler.Compile(sourceAddWeightOnly, "CSMain");
        _kernelAddWeightOnly = new ComputeKernel(_ctx, _accumLayout, addWeightOnlySpirv, "CSMain", 16, 16, 1);
        
        // Layout for add_texture_highlights
        // b0, t0, t3, u10 (PixelAccum), u13 (WeightAccum Binding 13)
        _accumHighLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 13, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }
        });

        // add_texture_exposure (Uses standard _accumLayout)
        string sourceAddExposure = textureOpsSource.Replace("void add_texture_exposure(", "void CSMain(");
        var addExposureSpirv = _compiler.Compile(sourceAddExposure, "CSMain");
        _kernelAddExposure = new ComputeKernel(_ctx, _accumLayout, addExposureSpirv, "CSMain", 16, 16, 1); // Uses u10

        // add_texture_highlights (Uses _accumHighLayout)
        string sourceAddHighlights = textureOpsSource.Replace("void add_texture_highlights(", "void CSMain(");
        var addHighlightsSpirv = _compiler.Compile(sourceAddHighlights, "CSMain");
        _kernelAddHighlights = new ComputeKernel(_ctx, _accumHighLayout, addHighlightsSpirv, "CSMain", 16, 16, 1); // Uses u10 and u13
    }

    private void EnsureNoiseEstPipeline()
    {
        if (_kernelBlurMosaic != null) return;
        
        // Layout for noise estimation kernels:
        // b0: TextureParams
        // t0: InTextureFloat (original)
        // t3: AuxTextureFloat (for color_diff: blurred texture)
        // u10: OutTextureFloat
        _noiseEstLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }
        });
        
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string shaderPath = Path.Combine(baseDir, "Shaders", "TextureOps.hlsl");
        string constantsPath = Path.Combine(baseDir, "Shaders", "Constants.hlsli");
        
        string source = File.ReadAllText(shaderPath);
        
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // blur_mosaic_texture
        string sourceBlur = source.Replace("void blur_mosaic_texture(", "void CSMain(");
        var blurSpirv = _compiler.Compile(sourceBlur, "CSMain");
        _kernelBlurMosaic = new ComputeKernel(_ctx, _noiseEstLayout, blurSpirv, "CSMain", 16, 16, 1);
        
        // color_difference_superpixel
        string sourceColorDiff = source.Replace("void color_difference_superpixel(", "void CSMain(");
        var colorDiffSpirv = _compiler.Compile(sourceColorDiff, "CSMain");
        _kernelColorDiffSuperpixel = new ComputeKernel(_ctx, _noiseEstLayout, colorDiffSpirv, "CSMain", 16, 16, 1);
        
        // sum_rect_columns_float (reduces Y dimension)
        string sourceSumCols = source.Replace("void sum_rect_columns_float(", "void CSMain(");
        var sumColsSpirv = _compiler.Compile(sourceSumCols, "CSMain");
        _kernelSumColumns = new ComputeKernel(_ctx, _noiseEstLayout, sumColsSpirv, "CSMain", 16, 16, 1);
        
        // sum_row_to_buffer (reduces X dimension)
        string sourceSumRows = source.Replace("void sum_row_to_buffer(", "void CSMain(");
        var sumRowsSpirv = _compiler.Compile(sourceSumRows, "CSMain");
        _kernelSumRows = new ComputeKernel(_ctx, _noiseEstLayout, sumRowsSpirv, "CSMain", 16, 16, 1);
    }

    private void EnsureMergeFrequencyPipeline()
    {
        if (_kernelMergeFrequency != null) return;

        // Check if the required Vulkan feature is supported
        if (!_ctx.SupportsStorageImageWriteWithoutFormat)
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════");
            Console.WriteLine("║ ❌ FREQUENCY DOMAIN PIPELINE UNAVAILABLE");
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

        Console.WriteLine("[Pipeline] Initializing Frequency Domain (HigherQuality) pipeline...");
        Console.WriteLine("[Pipeline] Using new modular shader architecture (no string replacement)");

        // IMPORTANT: Bindings must match [[vk::binding]] attributes in shaders!
        // FrequencyCommon.hlsli uses: t1→Binding1, t2→Binding2, t3→Binding3, t4→Binding4, t5→Binding5, u10→Binding10
        _frequencyLayout = _descriptors.CreateLayout(new[]
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // b0
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t1 RefTexture
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t2 AlignedTexture
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t3 AuxTexture0 (RMS)
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t4 AuxTexture1 (Mismatch)
            new DescriptorSetLayoutBinding { Binding = 5, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t5 AuxTexture2 (Highlights)
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }  // u10 OutputTexture
        });

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string frequencyDir = Path.Combine(baseDir, "Shaders", "Frequency");

        // Compile each shader from its own file (no string replacement!)
        // This eliminates the fragile string replacement approach and ensures clean compilation

        // calculate_abs_diff_rgba
        Console.WriteLine("[Pipeline]   Compiling calculate_abs_diff_rgba.hlsl...");
        string absPath = Path.Combine(frequencyDir, "calculate_abs_diff_rgba.hlsl");
        byte[] spirvAbs = _compiler.CompileFile(absPath);
        Console.WriteLine($"[Pipeline]   ✓ calculate_abs_diff_rgba compiled successfully ({spirvAbs.Length} bytes SPIR-V)");
        _kernelAbsDiff = new ComputeKernel(_ctx, _frequencyLayout, spirvAbs, "CSMain", 16, 16, 1);

        // calculate_rms_rgba
        Console.WriteLine("[Pipeline]   Compiling calculate_rms_rgba.hlsl...");
        string rmsPath = Path.Combine(frequencyDir, "calculate_rms_rgba.hlsl");
        byte[] spirvRms = _compiler.CompileFile(rmsPath);
        Console.WriteLine($"[Pipeline]   ✓ calculate_rms_rgba compiled successfully ({spirvRms.Length} bytes SPIR-V)");
        _kernelRms = new ComputeKernel(_ctx, _frequencyLayout, spirvRms, "CSMain", 16, 16, 1);

        // calculate_mismatch_rgba
        Console.WriteLine("[Pipeline]   Compiling calculate_mismatch_rgba.hlsl...");
        string misPath = Path.Combine(frequencyDir, "calculate_mismatch_rgba.hlsl");
        byte[] spirvMis = _compiler.CompileFile(misPath);
        Console.WriteLine($"[Pipeline]   ✓ calculate_mismatch_rgba compiled successfully ({spirvMis.Length} bytes SPIR-V)");
        _kernelMismatch = new ComputeKernel(_ctx, _frequencyLayout, spirvMis, "CSMain", 16, 16, 1);

        // calculate_highlights_norm_rgba
        Console.WriteLine("[Pipeline]   Compiling calculate_highlights_norm_rgba.hlsl...");
        string highPath = Path.Combine(frequencyDir, "calculate_highlights_norm_rgba.hlsl");
        byte[] spirvHigh = _compiler.CompileFile(highPath);
        Console.WriteLine($"[Pipeline]   ✓ calculate_highlights_norm_rgba compiled successfully ({spirvHigh.Length} bytes SPIR-V)");
        _kernelHighlightsNorm = new ComputeKernel(_ctx, _frequencyLayout, spirvHigh, "CSMain", 16, 16, 1);

        // normalize_mismatch
        Console.WriteLine("[Pipeline]   Compiling normalize_mismatch.hlsl...");
        string normPath = Path.Combine(frequencyDir, "normalize_mismatch.hlsl");
        byte[] spirvNorm = _compiler.CompileFile(normPath);
        Console.WriteLine($"[Pipeline]   ✓ normalize_mismatch compiled successfully ({spirvNorm.Length} bytes SPIR-V)");
        _kernelNormalizeMismatch = new ComputeKernel(_ctx, _frequencyLayout, spirvNorm, "CSMain", 16, 16, 1);

        // reduce_artifacts_tile_border
        Console.WriteLine("[Pipeline]   Compiling reduce_artifacts_tile_border.hlsl...");
        string artPath = Path.Combine(frequencyDir, "reduce_artifacts_tile_border.hlsl");
        byte[] spirvArt = _compiler.CompileFile(artPath);
        Console.WriteLine($"[Pipeline]   ✓ reduce_artifacts_tile_border compiled successfully ({spirvArt.Length} bytes SPIR-V)");
        _kernelArtifactsTileBorder = new ComputeKernel(_ctx, _frequencyLayout, spirvArt, "CSMain", 16, 16, 1);

        // forward_fft
        Console.WriteLine("[Pipeline]   Compiling forward_fft.hlsl...");
        string fwdPath = Path.Combine(frequencyDir, "forward_fft.hlsl");
        byte[] spirvFwd = _compiler.CompileFile(fwdPath);
        Console.WriteLine($"[Pipeline]   ✓ forward_fft compiled successfully ({spirvFwd.Length} bytes SPIR-V)");
        _kernelForwardFft = new ComputeKernel(_ctx, _frequencyLayout, spirvFwd, "CSMain", 16, 16, 1);
        Console.WriteLine("[Pipeline]   ✓ forward_fft kernel created");

        // backward_fft
        Console.WriteLine("[Pipeline]   Compiling backward_fft.hlsl...");
        string bwdPath = Path.Combine(frequencyDir, "backward_fft.hlsl");
        byte[] spirvBwd = _compiler.CompileFile(bwdPath);
        Console.WriteLine($"[Pipeline]   ✓ backward_fft compiled successfully ({spirvBwd.Length} bytes SPIR-V)");
        _kernelBackwardFft = new ComputeKernel(_ctx, _frequencyLayout, spirvBwd, "CSMain", 16, 16, 1);
        Console.WriteLine("[Pipeline]   ✓ backward_fft kernel created");

        // merge_frequency_domain
        Console.WriteLine("[Pipeline]   Compiling merge_frequency_domain.hlsl...");
        string mergePath = Path.Combine(frequencyDir, "merge_frequency_domain.hlsl");
        byte[] spirvMerge = _compiler.CompileFile(mergePath);
        Console.WriteLine($"[Pipeline]   ✓ merge_frequency_domain compiled successfully ({spirvMerge.Length} bytes SPIR-V)");
        _kernelMergeFrequency = new ComputeKernel(_ctx, _frequencyLayout, spirvMerge, "CSMain", 16, 16, 1);

        // deconvolute_frequency_domain
        Console.WriteLine("[Pipeline]   Compiling deconvolute_frequency_domain.hlsl...");
        string deconvPath = Path.Combine(frequencyDir, "deconvolute_frequency_domain.hlsl");
        byte[] spirvDeconv = _compiler.CompileFile(deconvPath);
        Console.WriteLine($"[Pipeline]   ✓ deconvolute_frequency_domain compiled successfully ({spirvDeconv.Length} bytes SPIR-V)");
        _kernelDeconvoluteFrequency = new ComputeKernel(_ctx, _frequencyLayout, spirvDeconv, "CSMain", 16, 16, 1);

        Console.WriteLine("[Pipeline] ✓ All frequency domain shaders compiled successfully!");
    }
    
    private void ExecuteMergeFrequency(VulkanImage refFT, VulkanImage refPyramid0, VulkanImage aligned, VulkanImage weightAccum, VulkanImage pixelAccumFT, 
        float whiteLevel, float blackLevel, double noiseReduction, float noiseSd, float exposureDiff, int tileSize, int mosaicPatternWidth, int uniformExposure)
    {
        EnsureMergeFrequencyPipeline();
        
        int width = (int)refPyramid0.Width;
        int height = (int)refPyramid0.Height;
        
        // CRITICAL FIX: Swift hardcodes tile_size_merge = 8 for FFT merging
        // See frequency.swift line 35: "let tile_size_merge = Int(8)"
        const int tile_size_merge = 8;
        
        // Calculate tile grid dimensions (Swift: tile_info_merge.n_tiles_x/y)
        int nTilesX = width / (2 * tile_size_merge);
        int nTilesY = height / (2 * tile_size_merge);
        
        float noise = noiseSd;
        
        // CRITICAL FIX: Use Swift's robustness formula
        // See frequency.swift lines 50-54
        bool isUniformExposure = (uniformExposure == 1);
        double robustness_rev = 0.5 * ((isUniformExposure ? 26.5 : 28.5) - Math.Round(noiseReduction));
        double robustness_norm = Math.Pow(2.0, -robustness_rev + 7.5);
        double read_noise = Math.Pow(Math.Pow(2.0, -robustness_rev + 10.0), 1.6);
        double max_motion_norm = Math.Max(1.0, Math.Pow(1.3, 11.0 - robustness_rev));
        
        float exposureFactor = (float)Math.Pow(2.0, exposureDiff);
        
        var freqParams = new FrequencyParams
        {
            RobustnessNorm = (float)robustness_norm,
            ReadNoise = (float)read_noise,
            MaxMotionNorm = (float)max_motion_norm, 
            TileSize = tile_size_merge, // Use fixed merge tile size, not alignment tile size
            UniformExposure = uniformExposure,
            NumTextures = 1, 
            ExposureFactor = exposureFactor,
            WhiteLevel = whiteLevel,
            BlackLevelMean = blackLevel,
            MeanMismatch = 0.01f, // Initial placeholder
            Padding0 = 0, Padding1 = 0
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), 
             BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });

        // CRITICAL FIX: RMS, Mismatch, Highlights textures are at TILE GRID size, not full image size
        // See frequency.swift line 133: rms_texture has dimensions tile_info.n_tiles_x, tile_info.n_tiles_y
        using var texDiff = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit); // AbsDiff - full size for mismatch calculation
        using var texRms = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit); // Tile-level RMS
        using var texMismatch = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit); // Tile-level Mismatch
        using var texHighlights = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit); // Tile-level Highlights
        
        // AlignedFT needs 2x width for complex storage
        int ftWidth = width * 2;
        using var texAlignedFT = new VulkanImage(_ctx, (uint)ftWidth, (uint)height, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

        // Transition
        var cmd = _ctx.BeginSingleTimeCommands();
        texDiff.TransitionLayout(ImageLayout.General, cmd);
        texRms.TransitionLayout(ImageLayout.General, cmd);
        texMismatch.TransitionLayout(ImageLayout.General, cmd);
        texHighlights.TransitionLayout(ImageLayout.General, cmd);
        texAlignedFT.TransitionLayout(ImageLayout.General, cmd);
        _ctx.EndSingleTimeCommands(cmd);

        // Helper to dispatch non-FFT kernels (per-pixel dispatch)
        void DispatchPixel(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage t1 = null, VulkanImage t2 = null, VulkanImage t3 = null, VulkanImage t4 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if(t0!=null) _descriptors.UpdateImage(set, 1, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t1!=null) _descriptors.UpdateImage(set, 2, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t2!=null) _descriptors.UpdateImage(set, 3, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t3!=null) _descriptors.UpdateImage(set, 4, t3.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t4!=null) _descriptors.UpdateImage(set, 5, t4.View, ImageLayout.General, DescriptorType.SampledImage);
            if(u0!=null) _descriptors.UpdateImage(set, 10, u0.View, ImageLayout.General, DescriptorType.StorageImage);
            
            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
            
            // Per-pixel dispatch: ceil(pixels / 16)
            uint groupsX = (uint)Math.Ceiling((double)width / 16.0);
            uint groupsY = (uint)Math.Ceiling((double)height / 16.0);
            kernel.Dispatch(cmd2, groupsX, groupsY, 1);
            
            _ctx.EndSingleTimeCommands(cmd2);
        }
        
        // Helper to dispatch FFT kernels (per-tile dispatch)
        void DispatchTile(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage t1 = null, VulkanImage t2 = null, VulkanImage t3 = null, VulkanImage t4 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if(t0!=null) _descriptors.UpdateImage(set, 1, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t1!=null) _descriptors.UpdateImage(set, 2, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t2!=null) _descriptors.UpdateImage(set, 3, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t3!=null) _descriptors.UpdateImage(set, 4, t3.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t4!=null) _descriptors.UpdateImage(set, 5, t4.View, ImageLayout.General, DescriptorType.SampledImage);
            if(u0!=null) _descriptors.UpdateImage(set, 10, u0.View, ImageLayout.General, DescriptorType.StorageImage);
            
            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
            
            // Per-tile dispatch: num_tiles = width/tileSize, groups = ceil(num_tiles/16)
            uint groupsX = (uint)Math.Ceiling((double)width / tile_size_merge / 16.0);
            uint groupsY = (uint)Math.Ceiling((double)height / tile_size_merge / 16.0);
            kernel.Dispatch(cmd2, groupsX, groupsY, 1);
            
            _ctx.EndSingleTimeCommands(cmd2);
        }
        
        // Helper to dispatch tile-grid kernels (for RMS, Mismatch, Highlights)
        void DispatchTileGrid(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage t1 = null, VulkanImage t2 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if(t0!=null) _descriptors.UpdateImage(set, 1, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t1!=null) _descriptors.UpdateImage(set, 2, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t2!=null) _descriptors.UpdateImage(set, 3, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if(u0!=null) _descriptors.UpdateImage(set, 10, u0.View, ImageLayout.General, DescriptorType.StorageImage);
            
            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
            
            // Dispatch for tile grid (nTilesX * nTilesY threads)
            uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
            uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
            kernel.Dispatch(cmd2, groupsX, groupsY, 1);
            
            _ctx.EndSingleTimeCommands(cmd2);
        }

        // 1. Abs Diff (full image size dispatch)
        // t0=Ref, t1=Aligned. u0=Diff
        DispatchPixel(_kernelAbsDiff!, texDiff, refPyramid0, aligned);
        
        // 2. RMS (tile grid dispatch - reads from reference, writes per tile)
        // t0=RefPyramid0. u0=Rms
        DispatchTileGrid(_kernelRms!, texRms, refPyramid0);
        
        // 3. Mismatch (tile grid dispatch - reads absdiff and rms)
        // t0=AbsDiff, t1=RMS. u0=Mismatch
        DispatchTileGrid(_kernelMismatch!, texMismatch, texDiff, texRms);
        
        // 4. Mean Mismatch (CPU Readback for now)
        float[] misData = texMismatch.GetData<float>(); 
        double sum = 0;
        for(int k=0; k<misData.Length; k+=4) sum += misData[k];
        float mean = (float)(sum / (misData.Length/4));
        if (mean < 1e-6f) mean = 1e-6f;
        
        freqParams.MeanMismatch = mean * 2.0f; 
        paramBuffer.SetData(new[] { freqParams });
        
        // 5. Normalize Mismatch (tile grid dispatch)
        DispatchTileGrid(_kernelNormalizeMismatch!, texMismatch, texMismatch);
        
        // 6. Highlights (tile grid dispatch)
        // t0=Aligned, t1=Mismatch(?). u0=Highlights. Swift passes aligned_texture here.
        DispatchTileGrid(_kernelHighlightsNorm!, texHighlights, aligned);
        
        // 7. Forward FFT Aligned (per-tile FFT dispatch)
        // DEBUG: Check input to FFT
        {
            float[] inputData = aligned.GetData<float>();
            double inputSum = 0;
            int inputSamples = Math.Min(inputData.Length, 1000);
            for (int i = 0; i < inputSamples; i++) inputSum += Math.Abs(inputData[i]);
            Console.WriteLine($"[FFT DEBUG] BEFORE FFT: aligned texture sum={inputSum:F2}, mean={inputSum/inputSamples:F4}, samples={inputSamples}, total_size={inputData.Length}");
            Console.WriteLine($"[FFT DEBUG] Input dimensions: {aligned.Width}x{aligned.Height}, format={aligned.Format}");
            Console.WriteLine($"[FFT DEBUG] Output dimensions: {texAlignedFT.Width}x{texAlignedFT.Height}, format={texAlignedFT.Format}");
        }

        DispatchTile(_kernelForwardFft!, texAlignedFT, aligned);

        // DEBUG: Check output from FFT
        {
            float[] outputData = texAlignedFT.GetData<float>();
            double outputSum = 0;
            int outputSamples = Math.Min(outputData.Length, 1000);
            for (int i = 0; i < outputSamples; i++) outputSum += Math.Abs(outputData[i]);
            Console.WriteLine($"[FFT DEBUG] AFTER FFT: texAlignedFT sum={outputSum:F2}, mean={outputSum/outputSamples:F4}, samples={outputSamples}");
            if (outputSum < 0.01) Console.WriteLine($"[FFT DEBUG] ❌ FFT OUTPUT IS ZERO!");
        }

        // 8. Merge Frequency (per-tile dispatch)
        // u0=AccumFT, t0=RefFT, t1=AlignedFT, t2=RMS, t3=Mismatch, t4=Highlights
        DispatchTile(_kernelMergeFrequency!, pixelAccumFT, refFT, texAlignedFT, texRms, texMismatch, texHighlights);
    }

    // Helper: Fill texture with zeros
    private void FillWithZeros(VulkanImage texture)
    {
        int channels = texture.Format == Format.R32Sfloat ? 1 : 4; // R32 or RGBA32
        int size = (int)(texture.Width * texture.Height * channels);
        texture.SetData(new float[size]);
    }

    // Helper: Add source texture to accumulator (CPU-based for now)
    private void AddTexture(VulkanImage source, VulkanImage accumulator, float weight = 1.0f)
    {
        float[] srcData = source.GetData<float>();
        float[] accData = accumulator.GetData<float>();
        for (int i = 0; i < srcData.Length; i++)
        {
            accData[i] += srcData[i] * weight;
        }
        accumulator.SetData(accData);
    }

    // Helper: Calculate mean of texture (for mismatch normalization)
    private float TextureMean(VulkanImage texture)
    {
        float[] data = texture.GetData<float>();
        double sum = 0;
        int channels = texture.Format == Format.R32Sfloat ? 1 : 4;
        for (int i = 0; i < data.Length; i += channels)
            sum += data[i]; // Use first channel only
        return (float)(sum / (data.Length / channels));
    }

    private void ExecuteBackwardFft(VulkanImage inputFT, VulkanImage outputSpatial, int numTextures, int tileSize)
    {
        EnsureMergeFrequencyPipeline();
        
        int width = (int)outputSpatial.Width;
        int height = (int)outputSpatial.Height;
        int nTilesX = width / tileSize;
        int nTilesY = height / tileSize;
        
        Console.WriteLine($"[ExecuteBackwardFft] InputFT: {inputFT.Width}x{inputFT.Height}, Output: {width}x{height}");
        Console.WriteLine($"[ExecuteBackwardFft] TileSize={tileSize}, NumTextures={numTextures}, Tiles={nTilesX}x{nTilesY}");
        
        var freqParams = new FrequencyParams
        {
            TileSize = tileSize,
            NumTextures = numTextures,
            Padding0 = 0
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), 
             BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });
        
        var cmd = _ctx.BeginSingleTimeCommands();
        
        // CRITICAL: Transition images to correct layout before dispatch
        inputFT.TransitionLayout(ImageLayout.General, cmd);
        outputSpatial.TransitionLayout(ImageLayout.General, cmd);
        
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, inputFT.View, ImageLayout.General, DescriptorType.SampledImage); // t1 = InputFT (RefTexture)
        _descriptors.UpdateImage(set, 10, outputSpatial.View, ImageLayout.General, DescriptorType.StorageImage); // u10 = Output
        
        _kernelBackwardFft!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelBackwardFft.PipelineLayout, 0, 1, &set, 0, null);
        
        // Dispatch groups
        uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
        uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
        
        Console.WriteLine($"[ExecuteBackwardFft] Dispatching {groupsX}x{groupsY} groups ({nTilesX}x{nTilesY} threads)");
        _kernelBackwardFft.Dispatch(cmd, groupsX, groupsY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }
    
    private void ExecuteForwardFft(VulkanImage input, VulkanImage output, int tileSize, int width, int height)
    {
       Console.WriteLine($"[EXEC FFT] *** ExecuteForwardFft CALLED ***");
       Console.WriteLine($"[EXEC FFT] About to start try block");

       try
       {
           Console.WriteLine($"[EXEC FFT] Inside try block, about to call EnsureMergeFrequencyPipeline");
           EnsureMergeFrequencyPipeline();
           Console.WriteLine($"[EXEC FFT] Pipeline initialized");

           // Calculate dispatch dimensions
           int nTilesX = width / tileSize;
           int nTilesY = height / tileSize;

           Console.WriteLine($"║ [2/9] Configuration:");
           Console.WriteLine($"║       Input texture:  {input.Width}x{input.Height} (format: {input.Format})");
           Console.WriteLine($"║       Output texture: {output.Width}x{output.Height} (format: {output.Format})");
           Console.WriteLine($"║       TileSize: {tileSize}");
           Console.WriteLine($"║       Spatial dimensions: {width}x{height}");
           Console.WriteLine($"║       Tile grid: {nTilesX}x{nTilesY} tiles");
           Console.WriteLine($"║       Expected threads: {nTilesX * nTilesY}");

           Console.WriteLine($"║ [3/9] Creating parameter buffer...");
           var freqParams = new FrequencyParams { TileSize = tileSize };
           using var pb = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
           pb.SetData(new[] { freqParams });
           Console.WriteLine($"║       ✓ Parameter buffer created (TileSize={tileSize})");

           Console.WriteLine($"║ [4/9] Beginning command buffer...");
           var cmd = _ctx.BeginSingleTimeCommands();
           Console.WriteLine($"║       ✓ Command buffer created");

           Console.WriteLine($"║ [5/9] Transitioning image layouts...");
           input.TransitionLayout(ImageLayout.General, cmd);
           output.TransitionLayout(ImageLayout.General, cmd);
           Console.WriteLine($"║       ✓ Layouts transitioned to General");

           // Create dummy images for unused descriptor bindings (2, 3, 4, 5)
           // Vulkan requires ALL bindings in a layout to be updated before use
           using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat,
               ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
           dummyTex.TransitionLayout(ImageLayout.General, cmd);

           Console.WriteLine($"║ [6/9] Setting up descriptors...");
           var set = _descriptors.Allocate(_frequencyLayout);
           _descriptors.UpdateBuffer(set, 0, pb.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
           _descriptors.UpdateImage(set, 1, input.View, ImageLayout.General, DescriptorType.SampledImage); // t1 = RefTexture (input)
           _descriptors.UpdateImage(set, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // t2 = unused (AlignedTexture)
           _descriptors.UpdateImage(set, 3, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // t3 = unused (AuxTexture0)
           _descriptors.UpdateImage(set, 4, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // t4 = unused (AuxTexture1)
           _descriptors.UpdateImage(set, 5, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // t5 = unused (AuxTexture2)
           _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage); // u10 = OutputTexture
           Console.WriteLine($"║       ✓ Descriptors bound:");
           Console.WriteLine($"║         - Binding 0: UniformBuffer (FrequencyParams)");
           Console.WriteLine($"║         - Binding 1: SampledImage (input RGBA)");
           Console.WriteLine($"║         - Bindings 2-5: SampledImage (dummy, unused)");
           Console.WriteLine($"║         - Binding 10: StorageImage (output FT, double width)");

           Console.WriteLine($"║ [7/9] Binding pipeline...");
           _kernelForwardFft!.BindPipeline(cmd);
           _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelForwardFft.PipelineLayout, 0, 1, &set, 0, null);
           Console.WriteLine($"║       ✓ Pipeline and descriptor sets bound");

           // Dispatch 1 thread per tile. Group size (16,16).
           uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
           uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
           uint totalGroups = groupsX * groupsY;
           uint totalThreads = groupsX * groupsY * 16 * 16;

           Console.WriteLine($"║ [8/9] Dispatching compute shader:");
           Console.WriteLine($"║       Workgroups: {groupsX}x{groupsY} = {totalGroups} groups");
           Console.WriteLine($"║       Threads: {groupsX * 16}x{groupsY * 16} = {totalThreads} threads");
           Console.WriteLine($"║       Active threads (within bounds): {nTilesX}x{nTilesY} = {nTilesX * nTilesY}");
           Console.WriteLine($"║       Workgroup size: 16x16x1 = 256 threads/group");

           Console.WriteLine($"║       >>> DISPATCHING NOW <<<");
           _kernelForwardFft.Dispatch(cmd, groupsX, groupsY, 1);
           Console.WriteLine($"║       ✓ Dispatch command recorded");

           // CRITICAL: Add memory barrier to ensure shader writes are visible before readback
           // This barrier ensures the compute shader's writes to OutputTexture are complete
           // and visible to subsequent transfer operations (like GetData's CmdCopyImageToBuffer)
           Console.WriteLine($"║       Adding memory barrier for shader write visibility...");
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
           Console.WriteLine($"║       ✓ Memory barrier added");

           Console.WriteLine($"║ [9/9] Executing command buffer and waiting for completion...");
           _ctx.EndSingleTimeCommands(cmd);
           Console.WriteLine($"║       ✓ GPU execution COMPLETE (QueueWaitIdle returned)");

           Console.WriteLine($"╠════════════════════════════════════════════════════════════════");
           Console.WriteLine($"║ [FFT DEBUG] ExecuteForwardFft EXIT - SUCCESS");
           Console.WriteLine($"╚════════════════════════════════════════════════════════════════\n");
       }
       catch (Exception ex)
       {
           Console.WriteLine($"║");
           Console.WriteLine($"╠════════════════════════════════════════════════════════════════");
           Console.WriteLine($"║ ❌ EXCEPTION in ExecuteForwardFft:");
           Console.WriteLine($"║ Type: {ex.GetType().Name}");
           Console.WriteLine($"║ Message: {ex.Message}");
           Console.WriteLine($"║ Stack: {ex.StackTrace}");
           Console.WriteLine($"╚════════════════════════════════════════════════════════════════\n");
           throw;
       }
    }

    private void ExecuteCopyImage(VulkanImage src, VulkanImage dst, int width, int height)
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
    
    /// <summary>
    /// Converts Bayer (R32Sfloat) texture to RGBA (R32G32B32A32Sfloat) superpixels for FFT processing.
    /// Output dimensions are half of input (2x2 Bayer -> 1 RGBA pixel).
    /// </summary>
    private void ExecuteConvertToRgba(VulkanImage bayerInput, VulkanImage rgbaOutput, int[] cfaPattern, int cropX = 0, int cropY = 0)
    {
        EnsureConversionPipeline();

        // === PRE-SHADER DATA VALIDATION ===
        // Read input texture BEFORE any GPU operations to verify data is valid
        {
            float[] inputData = bayerInput.GetData<float>();
            int sampleCount = Math.Min(inputData.Length, 1000);
            double inputSum = 0;
            for (int i = 0; i < sampleCount; i++) inputSum += Math.Abs(inputData[i]);
            
            // Also sample from the expected data region (after cropX/cropY offset)
            int dataStartIdx = cropY * (int)bayerInput.Width + cropX;
            double dataRegionSum = 0;
            int dataRegionSamples = Math.Min(1000, inputData.Length - dataStartIdx);
            if (dataStartIdx >= 0 && dataStartIdx < inputData.Length)
            {
                for (int i = 0; i < dataRegionSamples; i++) 
                    dataRegionSum += Math.Abs(inputData[dataStartIdx + i]);
            }
            
            Console.WriteLine($"[CONVERT_RGBA] === PRE-SHADER VALIDATION ===");
            Console.WriteLine($"[CONVERT_RGBA] Input: {bayerInput.Width}x{bayerInput.Height}, Output: {rgbaOutput.Width}x{rgbaOutput.Height}");
            Console.WriteLine($"[CONVERT_RGBA] CropX={cropX}, CropY={cropY}");
            Console.WriteLine($"[CONVERT_RGBA] Input data (first {sampleCount}): sum={inputSum:F2}, mean={inputSum/sampleCount:F4}");
            Console.WriteLine($"[CONVERT_RGBA] Input data (at offset cropY*W+cropX): sum={dataRegionSum:F2}, mean={dataRegionSum/dataRegionSamples:F4}");
            Console.WriteLine($"[CONVERT_RGBA] Input layout before transition: {bayerInput.CurrentLayout}");
            
            if (inputSum < 0.01 && dataRegionSum < 0.01)
            {
                Console.WriteLine($"[CONVERT_RGBA] ❌ ERROR: Input texture is EMPTY before shader execution!");
            }
        }

        // Determine CFA pattern index (simplified: use first element as indicator)
        int cfaIndex = 0;
        if (cfaPattern.Length >= 4)
        {
            // RGGB=0, GRBG=1, GBRG=2, BGGR=3
            // Pattern array is [R,G,G,B] positions mapping
            if (cfaPattern[0] == 0) cfaIndex = 0; // R at 0,0 -> RGGB
            else if (cfaPattern[0] == 1 && cfaPattern[1] == 0) cfaIndex = 1; // GRBG
            else if (cfaPattern[0] == 1 && cfaPattern[2] == 0) cfaIndex = 2; // GBRG
            else if (cfaPattern[0] == 2) cfaIndex = 3; // BGGR
        }

        // Pass padding offsets to shader (matches Swift's crop_x/crop_y parameters)
        var texParams = new TextureParams { CfaPattern = cfaIndex, PadLeft = cropX, PadTop = cropY };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData(new[] { texParams });
        
        Console.WriteLine($"[CONVERT_RGBA] TextureParams: CfaPattern={cfaIndex}, PadLeft={cropX}, PadTop={cropY}");

        // Dummy textures for unused bindings
        using var dummyRgba = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        using var dummyFloat = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.StorageBit);

        var cmd = _ctx.BeginSingleTimeCommands();
        bayerInput.TransitionLayout(ImageLayout.General, cmd);
        rgbaOutput.TransitionLayout(ImageLayout.General, cmd);
        dummyRgba.TransitionLayout(ImageLayout.General, cmd);
        dummyFloat.TransitionLayout(ImageLayout.General, cmd);

        // Add memory barrier to ensure bayerInput writes from prepare stage are visible
        var memoryBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmd,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit,
            0, 1, &memoryBarrier, 0, null, 0, null);

        var set = _descriptors.Allocate(_conversionLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, bayerInput.View, ImageLayout.General, DescriptorType.SampledImage);   // t0 - Bayer input
        _descriptors.UpdateImage(set, 3, dummyRgba.View, ImageLayout.General, DescriptorType.SampledImage);    // t2 - unused
        _descriptors.UpdateImage(set, 10, dummyFloat.View, ImageLayout.General, DescriptorType.StorageImage);  // u10 - unused
        _descriptors.UpdateImage(set, 12, rgbaOutput.View, ImageLayout.General, DescriptorType.StorageImage);  // u12 - RGBA output

        _kernelConvertToRgba!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelConvertToRgba.PipelineLayout, 0, 1, &set, 0, null);

        // Dispatch: one thread per OUTPUT pixel (RGBA dimensions)
        uint groupsX = (uint)Math.Ceiling((double)rgbaOutput.Width / 16.0);
        uint groupsY = (uint)Math.Ceiling((double)rgbaOutput.Height / 16.0);
        Console.WriteLine($"[DEBUG] ExecuteConvertToRgba: Input={bayerInput.Width}x{bayerInput.Height}, Output={rgbaOutput.Width}x{rgbaOutput.Height}, CropX={cropX}, CropY={cropY}, Dispatch={groupsX}x{groupsY}");
        _kernelConvertToRgba.Dispatch(cmd, groupsX, groupsY, 1);

        _ctx.EndSingleTimeCommands(cmd);
    }
    
    /// <summary>
    /// Converts RGBA (R32G32B32A32Sfloat) superpixels back to Bayer (R32Sfloat) pattern.
    /// Output dimensions are double the input (1 RGBA pixel -> 2x2 Bayer).
    /// </summary>
    private void ExecuteConvertToBayer(VulkanImage rgbaInput, VulkanImage bayerOutput, int[] cfaPattern)
    {
        EnsureConversionPipeline();
        
        // Determine CFA pattern index
        int cfaIndex = 0;
        if (cfaPattern.Length >= 4)
        {
            if (cfaPattern[0] == 0) cfaIndex = 0;
            else if (cfaPattern[0] == 1 && cfaPattern[1] == 0) cfaIndex = 1;
            else if (cfaPattern[0] == 1 && cfaPattern[2] == 0) cfaIndex = 2;
            else if (cfaPattern[0] == 2) cfaIndex = 3;
        }
        
        var texParams = new TextureParams { CfaPattern = cfaIndex };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData(new[] { texParams });
        
        // Dummy textures for unused bindings
        using var dummyFloat = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        using var dummyRgba = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit);
        
        var cmd = _ctx.BeginSingleTimeCommands();
        rgbaInput.TransitionLayout(ImageLayout.General, cmd);
        bayerOutput.TransitionLayout(ImageLayout.General, cmd);
        dummyFloat.TransitionLayout(ImageLayout.General, cmd);
        dummyRgba.TransitionLayout(ImageLayout.General, cmd);
        
        var set = _descriptors.Allocate(_conversionLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, dummyFloat.View, ImageLayout.General, DescriptorType.SampledImage);   // t0 - unused
        _descriptors.UpdateImage(set, 3, rgbaInput.View, ImageLayout.General, DescriptorType.SampledImage);    // t2 - RGBA input
        _descriptors.UpdateImage(set, 10, bayerOutput.View, ImageLayout.General, DescriptorType.StorageImage); // u10 - Bayer output
        _descriptors.UpdateImage(set, 12, dummyRgba.View, ImageLayout.General, DescriptorType.StorageImage);   // u12 - unused
        
        _kernelConvertToBayer!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelConvertToBayer.PipelineLayout, 0, 1, &set, 0, null);
        
        // Dispatch: one thread per OUTPUT pixel (Bayer dimensions)
        uint groupsX = (uint)Math.Ceiling((double)bayerOutput.Width / 16.0);
        uint groupsY = (uint)Math.Ceiling((double)bayerOutput.Height / 16.0);
        _kernelConvertToBayer.Dispatch(cmd, groupsX, groupsY, 1);
        
        _ctx.EndSingleTimeCommands(cmd);
    }
    
    /// <summary>
    /// Calculates RMS (root mean square) values per tile from the RGBA reference texture.
    /// Output is a tile-grid sized texture with per-tile RMS values.
    /// </summary>
    private void ExecuteCalculateRms(VulkanImage rgbaInput, VulkanImage rmsOutput, int nTilesX, int nTilesY, int tileSize)
    {
        EnsureMergeFrequencyPipeline();
        
        var freqParams = new FrequencyParams { TileSize = tileSize };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });
        
        var cmd = _ctx.BeginSingleTimeCommands();
        rgbaInput.TransitionLayout(ImageLayout.General, cmd);
        rmsOutput.TransitionLayout(ImageLayout.General, cmd);
        
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        // calculate_rms_rgba shader bindings:
        // t0 (Binding 1) = RGBA reference texture (reads pixel values)
        // u10 = RMS output texture (writes squared values per tile)
        _descriptors.UpdateImage(set, 1, rgbaInput.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 10, rmsOutput.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelRms!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelRms.PipelineLayout, 0, 1, &set, 0, null);
        
        // Dispatch one thread per tile
        uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
        uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
        Console.WriteLine($"[ExecuteCalculateRms] Input: {rgbaInput.Width}x{rgbaInput.Height}, Output: {rmsOutput.Width}x{rmsOutput.Height}, Dispatch: {groupsX}x{groupsY}");
        _kernelRms.Dispatch(cmd, groupsX, groupsY, 1);
        
        _ctx.EndSingleTimeCommands(cmd);
    }
    
    private void ExecuteDeconvoluteFrequency(VulkanImage finalTextureFT, VulkanImage mismatchTexture, int nTilesX, int nTilesY, int tileSize)
    {
        EnsureMergeFrequencyPipeline();
        
        var freqParams = new FrequencyParams { TileSize = tileSize };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), 
             BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });
        
        var cmd = _ctx.BeginSingleTimeCommands();
        finalTextureFT.TransitionLayout(ImageLayout.General, cmd);
        mismatchTexture.TransitionLayout(ImageLayout.General, cmd);
        
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        // deconvolute_frequency_domain shader bindings:
        // t0 (Binding 1) = mismatch texture (reads per-tile mismatch values)
        // u10 = final_texture_ft (read-write for in-place deconvolution)
        _descriptors.UpdateImage(set, 1, mismatchTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 10, finalTextureFT.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelDeconvoluteFrequency!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelDeconvoluteFrequency.PipelineLayout, 0, 1, &set, 0, null);
        
        // Dispatch per tile grid
        uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
        uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
        _kernelDeconvoluteFrequency.Dispatch(cmd, groupsX, groupsY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }
    
    private void ExecuteReduceArtifacts(VulkanImage outputTexture, VulkanImage refTexture, int nTilesX, int nTilesY, int tileSize, int[] blackLevel)
    {
        EnsureMergeFrequencyPipeline();
        
        // Create FrequencyParams with black levels
        // The shader expects blackLevel in buffer slots 1-4, but we pack into FrequencyParams
        var freqParams = new FrequencyParams 
        { 
            TileSize = tileSize,
            // Pack black levels into available fields
            BlackLevelMean = (blackLevel[0] + blackLevel[1] + blackLevel[2] + blackLevel[3]) / 4.0f
        };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });
        
        var cmd = _ctx.BeginSingleTimeCommands();
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        // reduce_artifacts_tile_border uses:
        // texture(0) = out_texture (read_write)
        // texture(1) = ref_texture (read)
        _descriptors.UpdateImage(set, 1, refTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 10, outputTexture.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelArtifactsTileBorder!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelArtifactsTileBorder.PipelineLayout, 0, 1, &set, 0, null);
        
        // Dispatch per tile grid
        uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
        uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
        _kernelArtifactsTileBorder.Dispatch(cmd, groupsX, groupsY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }
    private void ExecuteAvgPool(VulkanImage input, VulkanImage output, int scale, RawImage rawInfo)
    {
        EnsureAlignPipeline();
        
        var alignParams = new AlignParams
        {
            Scale = scale,
            BlackLevel = 0.0f, // Already subtracted in prepare? Align.hlsl subtracts BlackLevel again? 
            // In prepare_texture_bayer, we subtract black level and normalize. 
            // The input to avg_pool is "Bayer Image" (float).
            // If Prepare converted to float and subtracted BL, then avg_pool BL should be 0.
            FactorRed = 1.0f, FactorGreen = 1.0f, FactorBlue = 1.0f,
            DownscaleFactor = 0, TileSize = 0, SearchDist = 0, WeightSSD = 0, 
            HalfTileSize = 0, NumTilesX = 0, NumTilesY = 0, UniformExposure = 0
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { alignParams });
        
        // Dummy images for unused bindings (CompTexture, PrevAlignment)
        using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyTex.SetData(new float[] { 0 });
        
         // 3. Command Buffer
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
        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        
        var set = _descriptors.Allocate(_alignLayout);
        
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // CompTexture
        _descriptors.UpdateImage(set, 3, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // PrevAlignment
        _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelAvgPool!.BindPipeline(cmdBuffer);
        
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAvgPool.PipelineLayout, 0, 1, &set, 0, null);
        
        _kernelAvgPool.Dispatch(cmdBuffer, output.Width, output.Height, 1);
        
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

    private void ExecuteAlignmentSearch(List<VulkanImage> refPyramid, List<VulkanImage> compPyramid, VulkanImage alignmentOut, TileInfo baseTileInfo, int scale)
    {
        EnsureAlignPipeline();
        
        int numLevels = Math.Min(refPyramid.Count, compPyramid.Count);
        
        Console.WriteLine($"[Align] ExecuteAlignmentSearch: Levels={numLevels}, BaseTileSize={baseTileInfo.TileSize}");
        
        // Calculate tile sizes for each level
        // Level 0: baseTileInfo.TileSize
        // Level 1: max(Level0/2, 8)
        int[] tileSizes = new int[numLevels];
        tileSizes[0] = baseTileInfo.TileSize;
        for(int i=1; i<numLevels; i++)
        {
             tileSizes[i] = Math.Max(tileSizes[i-1]/2, 8);
        }
        
        VulkanImage? prevAlignment = null;
        
        // Loop from coarsest to finest
        for (int level = numLevels - 1; level >= 0; level--)
        {
            var refLayer = refPyramid[level];
            var compLayer = compPyramid[level];
            int tileSize = tileSizes[level];
            int searchDist = 2; // Always 2 for pyramid search
            
            // Calculate tile grid dimensions for this level
            // Formula: width / (tileSize/2) - 1
            int nTilesX = (int)refLayer.Width / (tileSize / 2) - 1;
            int nTilesY = (int)refLayer.Height / (tileSize / 2) - 1;
            
            if (nTilesX < 1) nTilesX = 1;
            if (nTilesY < 1) nTilesY = 1;
            
            Console.WriteLine($"[Align] Level {level}: {refLayer.Width}x{refLayer.Height}, TileSize={tileSize}, Grid={nTilesX}x{nTilesY}");
            
            // Determine Output Image for this level
            VulkanImage currentAlignment;
            bool isLastLevel = (level == 0);
            
            if (isLastLevel)
            {
                currentAlignment = alignmentOut; // Use final output 
            }
            else
            {
                // Allocate temp alignment for this level
                currentAlignment = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
            }
            
            // We need a command buffer for this level's operations
            // We'll execute one CB per level to simplify barriers and resource management
            
            var allocInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _ctx.CommandPool, CommandBufferCount = 1 };
            _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);
            var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);
            
            // Function-scope Disposables
            var levelDisposables = new List<IDisposable>();
            
            // 1. Prepare "Previous Alignment" for this level
            // If Level == Max (Coarsest), create Zero alignment.
            // If Level < Max, Upsample prevAlignment from (Level+1).
            
            VulkanImage prevAlignmentForStep; // The input to correction step
            
            if (prevAlignment == null)
            {
                // Coarsest Level: Create Zeros
                var zeroAlign = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                levelDisposables.Add(zeroAlign);
                
                int totalTiles = nTilesX * nTilesY;
                short[] zeros = new short[totalTiles * 4];
                zeroAlign.SetData(zeros); 
                zeroAlign.TransitionLayout(ImageLayout.General, cmdBuffer);
                
                prevAlignmentForStep = zeroAlign;
            }
            else
            {
                // Upsample from previous coarser level
                // Upsampled size must match current level grid (nTilesX, nTilesY)
                var upsampled = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
                levelDisposables.Add(upsampled);
                
                prevAlignment.TransitionLayout(ImageLayout.General, cmdBuffer);
                upsampled.TransitionLayout(ImageLayout.General, cmdBuffer);
                
                // Dispatch upsample_alignment
                var setUp = _descriptors.Allocate(_alignLayout);
                // upsample uses PrevAlignment(t2) as Input, OutAlignment(u10) as Output
                // We need dummy buffers for others (AlignParams etc)
                // Use a dummy params buffer
                var dummyParams = new AlignParams();
                using var pBuff = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
                pBuff.SetData(new[] { dummyParams });
                
                using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
                dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);
                
                _descriptors.UpdateBuffer(setUp, 0, pBuff.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
                _descriptors.UpdateImage(setUp, 1, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(setUp, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
                _descriptors.UpdateImage(setUp, 3, prevAlignment.View, ImageLayout.General, DescriptorType.SampledImage); // Input
                _descriptors.UpdateImage(setUp, 10, upsampled.View, ImageLayout.General, DescriptorType.StorageImage);    // Output
                
                _kernelUpsampleAlignment!.BindPipeline(cmdBuffer);
                _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelUpsampleAlignment.PipelineLayout, 0, 1, &setUp, 0, null);
                
                // Dispatch over OUTPUT size
                uint gX = (uint)Math.Ceiling(nTilesX / 16.0);
                uint gY = (uint)Math.Ceiling(nTilesY / 16.0);
                _kernelUpsampleAlignment.Dispatch(cmdBuffer, gX, gY, 1);
                
                // Barrier
                var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
                _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
                
                prevAlignmentForStep = upsampled;
            }
            
            // 2. Correct Upsampling Error (or refine zeros)
            // Reads prevAlignmentForStep, writes to "corrected"
            // We need a temp texture for "Corrected"
            var corrected = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
            levelDisposables.Add(corrected);
            corrected.TransitionLayout(ImageLayout.General, cmdBuffer);
            
            // Params
            int downscaleFactor = 2; // For alignment, each level is 2x scale relative to next?
            // Actually, in Swift `downscale_factors` array is [2, 2, 2...]
            // The `DownscaleFactor` param in shader scales the vector values.
            // If we are at level L, and vector is from L+1 (upsampled), the vector value (pixels) is in L+1 units.
            // To convert to L units, we mul by 2.
            // But if it's Level Max (zeros), factor doesn't matter (0*2=0).
            // Yes, use 2.
            
            var alignParams = new AlignParams
            {
                TileSize = tileSize,
                DownscaleFactor = 2, 
                NumTilesX = nTilesX,
                NumTilesY = nTilesY,
                UniformExposure = 0,
                // Others unused by correct_upsampling
            };
            
            using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
            paramBuffer.SetData(new[] { alignParams });
            
            refLayer.TransitionLayout(ImageLayout.General, cmdBuffer);
            compLayer.TransitionLayout(ImageLayout.General, cmdBuffer);
            
            var setCorrect = _descriptors.Allocate(_alignLayout);
            _descriptors.UpdateBuffer(setCorrect, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setCorrect, 1, refLayer.View, ImageLayout.General, DescriptorType.SampledImage); // Ref
            _descriptors.UpdateImage(setCorrect, 2, compLayer.View, ImageLayout.General, DescriptorType.SampledImage); // Comp
            _descriptors.UpdateImage(setCorrect, 3, prevAlignmentForStep.View, ImageLayout.General, DescriptorType.SampledImage); // Prev
            _descriptors.UpdateImage(setCorrect, 10, corrected.View, ImageLayout.General, DescriptorType.StorageImage); // Output (PrevCorrected)
            
            _kernelCorrectUpsamplingError!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelCorrectUpsamplingError.PipelineLayout, 0, 1, &setCorrect, 0, null);
            
            uint gXC = (uint)Math.Ceiling(nTilesX / 16.0);
            uint gYC = (uint)Math.Ceiling(nTilesY / 16.0);
            _kernelCorrectUpsamplingError.Dispatch(cmdBuffer, gXC, gYC, 1);
            
            // Barrier
            var barrier2 = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier2, 0, null, 0, null);
            
            // 3. Compute Tile Differences
            // Reads: Ref, Comp, Corrected(as Prev). Writes: TileDiff.
            // SearchDist = 2 -> nPos2D = 25.
            int nPos2D = 25;
            
            var tileDiff = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, (uint)nPos2D, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit, ImageViewType.Type3D);
            levelDisposables.Add(tileDiff);
            tileDiff.TransitionLayout(ImageLayout.General, cmdBuffer);
            
            // Update params if needed (SearchDist)
            alignParams.SearchDist = 2;
            alignParams.WeightSSD = 1; // Default
            paramBuffer.SetData(new[] { alignParams }); // Update buffer content
            
            var setDiff = _descriptors.Allocate(_alignLayout);
            _descriptors.UpdateBuffer(setDiff, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setDiff, 1, refLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setDiff, 2, compLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setDiff, 3, corrected.View, ImageLayout.General, DescriptorType.SampledImage); // Use CORRECTED as prev
            _descriptors.UpdateImage(setDiff, 10, tileDiff.View, ImageLayout.General, DescriptorType.StorageImage);
            
            // Optimized kernel (25)
            // Use _kernelTileDiff25 or _kernelTileDiffExposure25
            var kernelDiff = _kernelTileDiff25!; // Assuming uniform exposure for now
            kernelDiff.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernelDiff.PipelineLayout, 0, 1, &setDiff, 0, null);
            
            kernelDiff.Dispatch(cmdBuffer, gXC, gYC, 1);
            
            // Barrier
            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier2, 0, null, 0, null);
            
            // 4. Find Best Alignment
            // Reads: TileDiff, Corrected(as Prev). Writes: currentAlignment.
            
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
             _descriptors.UpdateImage(setFind, 2, dummyComp2.View, ImageLayout.General, DescriptorType.SampledImage); // Dummy
             
            _descriptors.UpdateImage(setFind, 3, corrected.View, ImageLayout.General, DescriptorType.SampledImage); // Prev (Corrected)
            _descriptors.UpdateImage(setFind, 10, currentAlignment.View, ImageLayout.General, DescriptorType.StorageImage); // Output
            
            _kernelFindBest!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelFindBest.PipelineLayout, 0, 1, &setFind, 0, null);
            
            _kernelFindBest.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);
            
            // End CB
            _ctx.Vk.EndCommandBuffer(cmdBuffer);
            
            var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmdBuffer };
            _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
            _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);
            
            _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
            
            // Cleanup
            foreach(var d in levelDisposables) d.Dispose();
            
            // Update prevAlignment for next iteration
            if (prevAlignment != null && prevAlignment != alignmentOut) prevAlignment.Dispose();
            prevAlignment = currentAlignment;
        }
    }
    
    private void ExecuteWarp(VulkanImage altImage, VulkanImage output, VulkanImage alignment, TileInfo tileInfo)
    {
        Console.WriteLine($"╔════════════════════════════════════════════════════════════════");
        Console.WriteLine($"║ [WARP DEBUG] ExecuteWarp CALLED");
        Console.WriteLine($"╠════════════════════════════════════════════════════════════════");
        Console.WriteLine($"║ [1/8] Input Configuration:");
        Console.WriteLine($"║       altImage (input):  {altImage.Width}x{altImage.Height} (format: {altImage.Format})");
        Console.WriteLine($"║       output:            {output.Width}x{output.Height} (format: {output.Format})");
        Console.WriteLine($"║       alignment:         {alignment.Width}x{alignment.Height} (format: {alignment.Format})");
        Console.WriteLine($"║       TileInfo: TileSize={tileInfo.TileSize}, NTilesX={tileInfo.NTilesX}, NTilesY={tileInfo.NTilesY}");

        // Check altImage input data
        Console.WriteLine($"║ [2/8] Checking input texture data...");
        {
            float[] altData = altImage.GetData<float>();
            double sum1000 = 0, sumMid = 0;
            int midStart = altData.Length / 2;
            int samples = Math.Min(1000, altData.Length);
            for (int i = 0; i < samples; i++) sum1000 += Math.Abs(altData[i]);
            for (int i = 0; i < samples && midStart + i < altData.Length; i++) sumMid += Math.Abs(altData[midStart + i]);
            Console.WriteLine($"║       altImage data (first 1000): sum={sum1000:F2}, mean={sum1000/samples:F4}");
            Console.WriteLine($"║       altImage data (mid 1000):   sum={sumMid:F2}, mean={sumMid/samples:F4}");
            if (sum1000 < 0.01 && sumMid < 0.01)
                Console.WriteLine($"║       ❌ ERROR: altImage appears EMPTY!");
            else
                Console.WriteLine($"║       ✓ altImage has data");
        }

        // Check alignment data
        Console.WriteLine($"║ [3/8] Checking alignment texture...");
        {
            // Alignment is R16G16B16A16Sint - need to read as shorts
            short[] alignData = alignment.GetData<short>();
            Console.WriteLine($"║       alignment data length: {alignData.Length} shorts ({alignData.Length/4} int4 values)");
            if (alignData.Length >= 8)
            {
                // Each int4 is 4 shorts: x, y, z, w
                Console.WriteLine($"║       First alignment vector: ({alignData[0]}, {alignData[1]}, {alignData[2]}, {alignData[3]})");
                int midIdx = (alignData.Length / 2) / 4 * 4; // Align to int4 boundary
                Console.WriteLine($"║       Mid alignment vector:   ({alignData[midIdx]}, {alignData[midIdx+1]}, {alignData[midIdx+2]}, {alignData[midIdx+3]})");
            }
            // Check if alignment has any non-zero values
            bool hasNonZero = false;
            for (int i = 0; i < Math.Min(alignData.Length, 1000) && !hasNonZero; i++)
                if (alignData[i] != 0) hasNonZero = true;
            Console.WriteLine($"║       Alignment has non-zero values: {hasNonZero}");
        }
        
        EnsureAlignPipeline();
         
        var alignParams = new AlignParams
        {
            Scale = 1,
            BlackLevel = 0.0f,
            FactorRed = 1.0f, FactorGreen = 1.0f, FactorBlue = 1.0f,
            // DownscaleFactor should match downscale_factor_array[0] from Swift
            // For Bayer images (mosaic_pattern_width=2), this is 2
            DownscaleFactor = 2,
            TileSize = tileInfo.TileSize, 
            SearchDist = 0, WeightSSD = 0,
            HalfTileSize = tileInfo.TileSize / 2,
            NumTilesX = tileInfo.NTilesX,
            NumTilesY = tileInfo.NTilesY,
            UniformExposure = 0
        };
        
        Console.WriteLine($"║ [4/8] AlignParams:");
        Console.WriteLine($"║       Scale={alignParams.Scale}, BlackLevel={alignParams.BlackLevel}");
        Console.WriteLine($"║       DownscaleFactor={alignParams.DownscaleFactor}");
        Console.WriteLine($"║       TileSize={alignParams.TileSize}, HalfTileSize={alignParams.HalfTileSize}");
        Console.WriteLine($"║       NumTilesX={alignParams.NumTilesX}, NumTilesY={alignParams.NumTilesY}");
        Console.WriteLine($"║       SearchDist={alignParams.SearchDist}, WeightSSD={alignParams.WeightSSD}");
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { alignParams });
        
        Console.WriteLine($"║ [5/8] Creating command buffer and transitioning layouts...");
        
        // Command Buffer
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
        
        // Transitions
        altImage.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer); 
        alignment.TransitionLayout(ImageLayout.General, cmdBuffer);
        Console.WriteLine($"║       ✓ Layouts transitioned to General");

        Console.WriteLine($"║ [6/8] Setting up descriptors...");
        var set = _descriptors.Allocate(_alignLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, altImage.View, ImageLayout.General, DescriptorType.SampledImage); // t0 In
        
        using var dummyComp = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyComp.TransitionLayout(ImageLayout.General, cmdBuffer);
        _descriptors.UpdateImage(set, 2, dummyComp.View, ImageLayout.General, DescriptorType.SampledImage); // t1 (unused)
        
        _descriptors.UpdateImage(set, 3, alignment.View, ImageLayout.General, DescriptorType.SampledImage); // t2 PrevAlignment
        _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage); // u10 Out
        Console.WriteLine($"║       ✓ Descriptors bound:");
        Console.WriteLine($"║         - Binding 0: UniformBuffer (AlignParams)");
        Console.WriteLine($"║         - Binding 1: SampledImage (altImage/InTexture)");
        Console.WriteLine($"║         - Binding 2: SampledImage (dummy/Comp)");
        Console.WriteLine($"║         - Binding 3: SampledImage (alignment/PrevAlignment)");
        Console.WriteLine($"║         - Binding 10: StorageImage (output/OutTexture)");
        
        _kernelWarp!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelWarp.PipelineLayout, 0, 1, &set, 0, null);
        
        Console.WriteLine($"║ [7/8] Dispatching warp shader...");
        // NOTE: ComputeKernel.Dispatch may divide by workgroup size internally
        // Check if this is correct - output.Width/Height are pixel counts, not workgroup counts
        uint dispatchX = output.Width;
        uint dispatchY = output.Height;
        Console.WriteLine($"║       Dispatch dimensions: {dispatchX}x{dispatchY}");
        Console.WriteLine($"║       Kernel workgroup size: 16x16x1");
        Console.WriteLine($"║       >>> DISPATCHING NOW <<<");
        
        _kernelWarp.Dispatch(cmdBuffer, dispatchX, dispatchY, 1);
        
        // Add memory barrier to ensure writes are visible
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit,
            0, 1, &barrier, 0, null, 0, null);
        Console.WriteLine($"║       ✓ Memory barrier added after dispatch");
        
        _ctx.Vk.EndCommandBuffer(cmdBuffer);
        
        Console.WriteLine($"║ [8/8] Executing command buffer...");
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer
        };
        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);
        Console.WriteLine($"║       ✓ GPU execution COMPLETE");
        
        // Verify output data
        {
            float[] outData = output.GetData<float>();
            double sumFirst = 0, sumMid = 0;
            int samples = Math.Min(1000, outData.Length);
            int midStart = outData.Length / 2;
            for (int i = 0; i < samples; i++) sumFirst += Math.Abs(outData[i]);
            for (int i = 0; i < samples && midStart + i < outData.Length; i++) sumMid += Math.Abs(outData[midStart + i]);
            Console.WriteLine($"║       Output data (first 1000): sum={sumFirst:F2}, mean={sumFirst/samples:F4}");
            Console.WriteLine($"║       Output data (mid 1000):   sum={sumMid:F2}, mean={sumMid/samples:F4}");
            if (sumFirst < 0.01 && sumMid < 0.01)
                Console.WriteLine($"║       ❌ OUTPUT IS ZERO!");
            else
                Console.WriteLine($"║       ✓ Output has data");
        }
        
        Console.WriteLine($"╚════════════════════════════════════════════════════════════════");
        
        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
    }
    
    /// <summary>
    /// Computes merge weight and performs weighted add: accumulator += warped * weight.
    /// For simplicity, we use the robustness-weighted spatial merge.
    /// </summary>
    /// <summary>
    /// Computes merge weight and performs weighted add: accumulator += warped * weight.
    /// Handles exposure differences (HDR merge) if exposureDiff != 0.
    /// </summary>
    private void ExecuteMerge(VulkanImage referenceFrame, VulkanImage warpedFrame, VulkanImage weightAccum, VulkanImage pixelAccum, float whiteLevel, float blackLevel, double noiseReduction, float noiseSd, float exposureDiff)
    {
        EnsureMergePipeline();
        
        // 1. Compute color difference (ref - warped) -> diff texture
        using var diffTex = new VulkanImage(_ctx, warpedFrame.Width, warpedFrame.Height, Format.R32Sfloat, 
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
        
        // 2. Compute merge weight from diff -> weight texture
        using var weightTex = new VulkanImage(_ctx, warpedFrame.Width, warpedFrame.Height, Format.R32Sfloat, 
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);

        // Calculate robustness using Swift formula
        float robustness = CalculateRobustness(noiseReduction);
        
        var spatialParams = new SpatialParams
        {
            WhiteLevel = whiteLevel,
            BlackLevel = blackLevel,
            Robustness = robustness,
            NoiseSd = noiseSd  // Use the estimated noise_sd from reference texture
        };
        
        Console.WriteLine($"[VulkanComputePipeline] Merge: Robustness={robustness:F4}, NoiseSd={noiseSd:F4} (NR={noiseReduction})");
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<SpatialParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { spatialParams });
        
        // Command Buffer
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
        _descriptors.UpdateImage(setDiff, 1, referenceFrame.View, ImageLayout.General, DescriptorType.SampledImage); // t0 Ref
        _descriptors.UpdateImage(setDiff, 2, warpedFrame.View, ImageLayout.General, DescriptorType.SampledImage); // t1 Comp
        _descriptors.UpdateImage(setDiff, 3, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage); // t2 InDiff (unused for color_difference)
        _descriptors.UpdateImage(setDiff, 10, diffTex.View, ImageLayout.General, DescriptorType.StorageImage); // u10 OutDiff
        
        _kernelColorDiff!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelColorDiff.PipelineLayout, 0, 1, &setDiff, 0, null);
        _kernelColorDiff.Dispatch(cmdBuffer, diffTex.Width, diffTex.Height, 1);
        
        // Barrier
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // --- Pass 2: compute_merge_weight ---
        var setWeight = _descriptors.Allocate(_mergeLayout);
        _descriptors.UpdateBuffer(setWeight, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<SpatialParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setWeight, 1, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage); // t0 (unused)
        _descriptors.UpdateImage(setWeight, 2, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage); // t1 (unused)
        _descriptors.UpdateImage(setWeight, 3, diffTex.View, ImageLayout.General, DescriptorType.SampledImage); // t2 InDiff
        _descriptors.UpdateImage(setWeight, 10, weightTex.View, ImageLayout.General, DescriptorType.StorageImage); // u10 OutWeight
        
        _kernelMergeWeight!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelMergeWeight.PipelineLayout, 0, 1, &setWeight, 0, null);
        _kernelMergeWeight.Dispatch(cmdBuffer, weightTex.Width, weightTex.Height, 1);
        
        // Barrier
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // Barrier
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // Barrier
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // --- Pass 3: Accumulation (Branching based on Exposure) ---
        
        // Logic check:
        // exposureDiff = Ref - Alt
        // If Ref=0, Alt=-2 (Darker) -> diff=+2. 
        // We want Highlight Recovery (using darker Alt to fix brighter Ref).
        // So diff > 0.1 => Alt is Darker => Highlight Recovery.
        bool isAltUnderexposed = exposureDiff > 0.1f;
        
        // If Ref=0, Alt=+2 (Brighter) -> diff=-2.
        // We want Add Texture Exposure (using brighter Alt, scaled down).
        // So diff < -0.1 => Alt is Brighter => Add Exposure.
        bool isAltOverexposed = exposureDiff < -0.1f;
        
        if (isAltUnderexposed)
        {
            // --- Highlight Recovery (Alt is Darker) ---
            // Use add_texture_highlights which updates BOTH Pixel and Weight accumulators simultaneously
            
            float scaleFactor = (float)Math.Pow(2.0, exposureDiff);
            
            var tParams = new TextureParams 
            { 
                 WhiteLevel = whiteLevel, BlackLevel = blackLevel, BlackLevelMean = 0,
                 ScaleFactor = scaleFactor,
                 ExposureDiff = (int)(exposureDiff * 100)
            };
            
            using var tParamBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
            tParamBuffer.SetData(new[] { tParams });
            
            var setAccumHigh = _descriptors.Allocate(_accumHighLayout);
            _descriptors.UpdateBuffer(setAccumHigh, 0, tParamBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setAccumHigh, 1, warpedFrame.View, ImageLayout.General, DescriptorType.SampledImage); // t0 Input
            _descriptors.UpdateImage(setAccumHigh, 4, weightTex.View, ImageLayout.General, DescriptorType.SampledImage);   // t3 Weight
            _descriptors.UpdateImage(setAccumHigh, 10, pixelAccum.View, ImageLayout.General, DescriptorType.StorageImage); // u10 Pixel Accum
            _descriptors.UpdateImage(setAccumHigh, 13, weightAccum.View, ImageLayout.General, DescriptorType.StorageImage); // u13 Weight Accum
            
            _kernelAddHighlights!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddHighlights.PipelineLayout, 0, 1, &setAccumHigh, 0, null);
            _kernelAddHighlights.Dispatch(cmdBuffer, pixelAccum.Width, pixelAccum.Height, 1);
        }
        else
        {
            // Standard (diff ~ 0) or Brighter (diff < 0) Path
            
            float scaleFactor = 1.0f;
            if (isAltOverexposed) scaleFactor = (float)Math.Pow(2.0, exposureDiff);
            
            var tParams = new TextureParams 
            { 
                 WhiteLevel = whiteLevel, BlackLevel = blackLevel, BlackLevelMean = 0,
                 ScaleFactor = scaleFactor
            };
            using var tParamBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
            tParamBuffer.SetData(new[] { tParams });
            
            var setPixelAccum = _descriptors.Allocate(_accumLayout);
            _descriptors.UpdateBuffer(setPixelAccum, 0, tParamBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer); // b0
            _descriptors.UpdateImage(setPixelAccum, 1, warpedFrame.View, ImageLayout.General, DescriptorType.SampledImage); // t0
            _descriptors.UpdateImage(setPixelAccum, 4, weightTex.View, ImageLayout.General, DescriptorType.SampledImage); // t3
            _descriptors.UpdateImage(setPixelAccum, 10, pixelAccum.View, ImageLayout.General, DescriptorType.StorageImage); // u10
            
            if (isAltOverexposed)
            {
                 // Use add_texture_exposure
                 _kernelAddExposure!.BindPipeline(cmdBuffer);
                 _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddExposure.PipelineLayout, 0, 1, &setPixelAccum, 0, null);
                 _kernelAddExposure.Dispatch(cmdBuffer, pixelAccum.Width, pixelAccum.Height, 1);
                 Console.WriteLine($"[VulkanComputePipeline] Merge: Add Exposure (Diff={exposureDiff:F2})");
            }
            else
            {
                 // Standard add_texture_weighted
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
    
    private void ExecutePrepare(VulkanImage input, VulkanImage output, RawImage rawInfo, int padLeft, int padTop)
    {
        // 1. Ensure Pipeline & Layout
        EnsurePreparePipeline();
        
        // 2. Create Params & Buffers
        var texParams = new TextureParams
        {
             WhiteLevel = rawInfo.WhiteLevel,
             BlackLevel = 0, 
             BlackLevelMean = 0.0f,
             ScaleFactor = 1.0f,
             CfaPattern = 0, // Assume RGGB
             Width = (int)output.Width,
             Height = (int)output.Height,
             InputWidth = (int)input.Width,
             InputHeight = (int)input.Height,
             
             PadLeft = padLeft,
             PadTop = padTop,
             ExposureDiff = 0,
             HotPixelThreshold = 1000.0f, 
             HotPixelMultiplicator = 1.0f,
             CorrectionStrength = 0.0f
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { texParams });
        
        float[] blackLevels = new float[4];
        if (rawInfo.BlackLevel.Length >= 4) {
             for(int i=0; i<4; i++) blackLevels[i] = (float)rawInfo.BlackLevel[i];
        } else if (rawInfo.BlackLevel.Length > 0) {
             for(int i=0; i<4; i++) blackLevels[i] = (float)rawInfo.BlackLevel[0];
        }
        
        using var blParams = new VulkanBuffer(_ctx, (ulong)(4 * sizeof(float)),
             BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        blParams.SetData(blackLevels);
        
        // MeanBuffer (Dummy for now)
        using var meanBuffer = new VulkanBuffer(_ctx, (ulong)sizeof(float), 
             BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        meanBuffer.SetData(new float[] { 0.0f });

        using var dummyWeight = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        dummyWeight.SetData(new float[] { 0.0f });
        
        using var dummyRGBA = new VulkanImage(_ctx, 1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        dummyRGBA.SetData(new byte[] { 0, 0, 0, 0 });
        
        using var dummyUint = new VulkanImage(_ctx, 1, 1, Format.R16Uint, ImageUsageFlags.StorageBit);
        
        // 3. Command Buffer
        // Use shared pool
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
        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer); 
        dummyWeight.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyRGBA.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyUint.TransitionLayout(ImageLayout.General, cmdBuffer);
        
        // Allocate Set
        var set = _descriptors.Allocate(_prepareLayout);
        
        // Update Descriptors
        // prepare_texture_bayer uses: InTextureUint (t1→Binding2), AuxTextureFloat (t3→Binding4), BlackLevels (t5→Binding6)
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, dummyWeight.View, ImageLayout.General, DescriptorType.SampledImage); // t0 (unused)
        _descriptors.UpdateImage(set, 2, input.View, ImageLayout.General, DescriptorType.SampledImage);        // t1 InTextureUint
        _descriptors.UpdateImage(set, 3, dummyRGBA.View, ImageLayout.General, DescriptorType.SampledImage);    // t2 (unused)
        _descriptors.UpdateImage(set, 4, dummyWeight.View, ImageLayout.General, DescriptorType.SampledImage);  // t3 AuxTextureFloat (hotpixel weight)
        _descriptors.UpdateBuffer(set, 5, meanBuffer.Handle, (ulong)sizeof(float), DescriptorType.StorageBuffer);       // t4 MeanTextureBuffer (unused)
        _descriptors.UpdateBuffer(set, 6, blParams.Handle, (ulong)(4*sizeof(float)), DescriptorType.StorageBuffer);      // t5 BlackLevels
        _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage);       // u10 OutTextureFloat
        _descriptors.UpdateImage(set, 11, dummyUint.View, ImageLayout.General, DescriptorType.StorageImage);   // u11 (unused)
        _descriptors.UpdateImage(set, 12, dummyRGBA.View, ImageLayout.General, DescriptorType.StorageImage);   // u12 (unused)
        
        // Bind Pipeline & Sets
        _kernelPrepareBayer!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelPrepareBayer.PipelineLayout, 0, 1, in set, 0, null);

        // Dispatch - CRITICAL: Use INPUT dimensions (like Metal), shader adds padding on write
        // Metal: threads_per_grid = MTLSize(width: in_texture.width, height: in_texture.height, depth: 1)
        uint dispatchW = (uint)input.Width;
        uint dispatchH = (uint)input.Height;

        uint groupX = (dispatchW + 15) / 16;
        uint groupY = (dispatchH + 15) / 16;

        _ctx.Vk.CmdDispatch(cmdBuffer, groupX, groupY, 1);
        
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





    // Implement EnsureExposurePipeline
    private DescriptorSetLayout _exposureLayout;
    
    private void EnsureExposurePipeline()
    {
        if (_kernelCorrectExposure != null) return;
        
        // Layout for exposure kernels (max_y, max_x need different layouts?)
        // correct_exposure:
        // b0: ExposureParams (Binding 0)
        // t0: FinalTexture (In/Out RW?) -> Metal uses [[texture(1)]] as read_write. HLSL u0.
        // t1: Blurred (In) -> Metal [[texture(0)]]
        // u0: FinalTexture (Out) -> Binding 10
        // ... + buffers
        
        // Let's check kernel mappings in Exposure.hlsl:
        // b0: ExposureParams
        // t0: InTexture (generic)
        // t1: InBlurred
        // t2: BlackLevelsMean (StructuredBuffer)
        // t3: MaxTextureBuffer (StructuredBuffer)
        // u0: OutTexture (RW)
        // u1: OutBuffer (RW)

        _exposureLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t0
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t1 (Map t1->Binding 4? No, usage is t1. Let's start bindings at 1)
            // Wait, Bindings in Layout must match HLSL registers?
            // In HLSL: t0, t1, t2, t3.
            // Silk.NET sets usually assume binding=index if not specified? 
            // Better to use Explicit bindings in HLSL or match indices.
            // My other kernels use LayoutBinding { Binding = 1 } for t0?
            // Let's revisit TextureOps.hlsl:
            // Texture2D<float> InTextureFloat  : register(t0); -> Binding?
            // Usually standard is: register(tN) maps to Binding N if space 0.
            
            // In my other Ensure... methods I mapped Binding 1 to t0?
            // _prepareLayout: Binding 1 (SampledImage). In TextureOps: register(t0).
            // So t0 maps to Binding 1? 
            // In Constants.hlsli or implied? 
            // Actually, Vulkan bindings are explicit. If I say "layout(binding=1)" in GLSL. 
            // In HLSL for SPIR-V, 'register(t0)' usually maps to Binding 0.
            // UNLESS I used a compiler option to shift bindings.
            // The existing C# code sets Binding 1 in layout.
            // Let's assume there's an offset shift or explicit mapping I missed, or I should stick to logic "Binding 1 is first texture".
            
            // Let's try to stick to the pattern used in other kernels.
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t0
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t1
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t2 BlackLevels
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t3 MaxBuffer
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // u0
            new DescriptorSetLayoutBinding { Binding = 11, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }  // u1
        });
        
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string shaderPath = Path.Combine(baseDir, "Shaders", "Exposure.hlsl");
        string constantsPath = Path.Combine(baseDir, "Shaders", "Constants.hlsli");
        
        string source = File.ReadAllText(shaderPath);
        
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // Compile correct_exposure
        string sourceCorrect = source.Replace("void correct_exposure(", "void CSMain(");
        var correctSpirv = _compiler.Compile(sourceCorrect, "CSMain");
        _kernelCorrectExposure = new ComputeKernel(_ctx, _exposureLayout, correctSpirv, "CSMain", 16, 16, 1);
        
        // correct_exposure_linear
        string sourceLinear = source.Replace("void correct_exposure_linear(", "void CSMain(");
        var linearSpirv = _compiler.Compile(sourceLinear, "CSMain");
        _kernelCorrectExposureLinear = new ComputeKernel(_ctx, _exposureLayout, linearSpirv, "CSMain", 16, 16, 1);
        
        // max_y
        string sourceMaxY = source.Replace("void max_y(", "void CSMain(");
        var maxYSpirv = _compiler.Compile(sourceMaxY, "CSMain");
        _kernelMaxY = new ComputeKernel(_ctx, _exposureLayout, maxYSpirv, "CSMain", 64, 1, 1);
        
        // max_x
        string sourceMaxX = source.Replace("void max_x(", "void CSMain(");
        var maxXSpirv = _compiler.Compile(sourceMaxX, "CSMain");
        _kernelMaxX = new ComputeKernel(_ctx, _exposureLayout, maxXSpirv, "CSMain", 1, 1, 1);
    }
    
    private ComputeKernel? _kernelCorrectExposure;
    private ComputeKernel? _kernelCorrectExposureLinear;
    private ComputeKernel? _kernelMaxY;
    private ComputeKernel? _kernelMaxX;

    /// <summary>
    /// Executes a Gaussian blur on the input texture.
    /// Can be used for noise estimation or exposure control.
    /// </summary>
    private void ExecuteBlur(VulkanImage input, VulkanImage output, int kernelSize, int mosaicPatternWidth, VulkanImage? intermediate = null)
    {
        EnsureNoiseEstPipeline();
        
        uint width = input.Width;
        uint height = input.Height;
        
        // Intermediate texture (X-blurred)
        // If not provided, we allocate one locally
        bool ownInter = false;
        if (intermediate == null)
        {
            intermediate = new VulkanImage(_ctx, width, height, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
            ownInter = true;
        }
        
        using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyTex.SetData(new float[] { 0 });
        
        // buffers
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
        
        using var paramBufferBlurX = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), 
             BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBufferBlurX.SetData(new[] { texParamsBlurX });
        
        using var paramBufferBlurY = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), 
             BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBufferBlurY.SetData(new[] { texParamsBlurY });
        
        // Cmd
        var allocInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _ctx.CommandPool, CommandBufferCount = 1 };
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);
        
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);
        
        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        output.TransitionLayout(ImageLayout.General, cmdBuffer);
        intermediate.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        
        // PASS 1: X Blur
        var setBlurX = _descriptors.Allocate(_noiseEstLayout);
        _descriptors.UpdateBuffer(setBlurX, 0, paramBufferBlurX.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setBlurX, 1, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setBlurX, 4, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setBlurX, 10, intermediate.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelBlurMosaic!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelBlurMosaic.PipelineLayout, 0, 1, &setBlurX, 0, null);
        _kernelBlurMosaic.Dispatch(cmdBuffer, width, height, 1);
        
        // Barrier
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
        
        // PASS 2: Y Blur
        var setBlurY = _descriptors.Allocate(_noiseEstLayout);
        _descriptors.UpdateBuffer(setBlurY, 0, paramBufferBlurY.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setBlurY, 1, intermediate.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setBlurY, 4, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setBlurY, 10, output.View, ImageLayout.General, DescriptorType.StorageImage);

        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelBlurMosaic.PipelineLayout, 0, 1, &setBlurY, 0, null);
        _kernelBlurMosaic.Dispatch(cmdBuffer, width, height, 1);
        
        _ctx.Vk.EndCommandBuffer(cmdBuffer);
        
        var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmdBuffer };
        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);
        
        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
        
        if (ownInter) intermediate.Dispose();
    }
    
    // Helper to reduce max value of texture
    private void ExecuteMaxReduction(VulkanImage input, VulkanBuffer outBuffer, int mosaicPatternWidth)
    {
        EnsureExposurePipeline();
        
        // 1. Max Y -> 1D texture
        // Output dimensions: (width, 1)
        using var maxYTex = new VulkanImage(_ctx, input.Width, 1, Format.R32Sfloat, 
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
            
        // Params? max_y takes b1=Width? 
        // HLSL: cbuffer ExposureParams : register(b0)
        // TextureWidth is field.
        
        var exParams = new ExposureParams
        {
            TextureWidth = (int)input.Width
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<ExposureParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData(new[] { exParams });
        
        using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit); // t1
        using var dummyBuff = new VulkanBuffer(_ctx, 4, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit); // t2, t3, u1
        
        var allocInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _ctx.CommandPool, CommandBufferCount = 1 };
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);
        
        input.TransitionLayout(ImageLayout.General, cmdBuffer);
        maxYTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        
        // Max Y
        var setMaxY = _descriptors.Allocate(_exposureLayout);
        _descriptors.UpdateBuffer(setMaxY, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<ExposureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setMaxY, 1, input.View, ImageLayout.General, DescriptorType.SampledImage); // t0
        _descriptors.UpdateImage(setMaxY, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // t1
        _descriptors.UpdateBuffer(setMaxY, 3, dummyBuff.Handle, 4, DescriptorType.StorageBuffer); // t2
        _descriptors.UpdateBuffer(setMaxY, 4, dummyBuff.Handle, 4, DescriptorType.StorageBuffer); // t3 
        _descriptors.UpdateImage(setMaxY, 10, maxYTex.View, ImageLayout.General, DescriptorType.StorageImage); // u0
        _descriptors.UpdateBuffer(setMaxY, 11, dummyBuff.Handle, 4, DescriptorType.StorageBuffer); // u1
        
        _kernelMaxY!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelMaxY.PipelineLayout, 0, 1, &setMaxY, 0, null);
        _ctx.Vk.CmdDispatch(cmdBuffer, input.Width, 1, 1);
        
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
        
        // Max X
        // Input: maxYTex (t0)
        // Output: outBuffer (u1)
        var setMaxX = _descriptors.Allocate(_exposureLayout);
         _descriptors.UpdateBuffer(setMaxX, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<ExposureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setMaxX, 1, maxYTex.View, ImageLayout.General, DescriptorType.SampledImage); // t0
        _descriptors.UpdateImage(setMaxX, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // t1
        _descriptors.UpdateBuffer(setMaxX, 3, dummyBuff.Handle, 4, DescriptorType.StorageBuffer); // t2
        _descriptors.UpdateBuffer(setMaxX, 4, dummyBuff.Handle, 4, DescriptorType.StorageBuffer); // t3
        _descriptors.UpdateImage(setMaxX, 10, dummyTex.View, ImageLayout.General, DescriptorType.StorageImage); // u0 (unused)
        _descriptors.UpdateBuffer(setMaxX, 11, outBuffer.Handle, 4, DescriptorType.StorageBuffer); // u1
        
        _kernelMaxX!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelMaxX.PipelineLayout, 0, 1, &setMaxX, 0, null);
        _ctx.Vk.CmdDispatch(cmdBuffer, 1, 1, 1);
        
        _ctx.Vk.EndCommandBuffer(cmdBuffer);
        
        var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmdBuffer };
        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);
        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
    }
    
    private void ExecuteExposureCorrection(VulkanImage image, ExposureControlOption option, RawImage metadata)
    {
        if (option == ExposureControlOption.Off) return;
        
        Console.WriteLine($"[VulkanComputePipeline] Executing Exposure Correction: {option}");
        EnsureExposurePipeline();
        
        bool isCurve = (option == ExposureControlOption.Curve0EV || option == ExposureControlOption.Curve1EV);
        float linearGain = (option == ExposureControlOption.Linear1EV || option == ExposureControlOption.Curve1EV) ? 2.0f : -1.0f; // -1 for FullRange? 
        // Swift: linearFullRange ? -1.0 : 2.0.
        // If LinearFullRange, Gain=-1.0 (flag to use max range).
        // If Linear1EV, Gain=2.0.
         if (option == ExposureControlOption.LinearFullRange) linearGain = -1.0f;
         if (option == ExposureControlOption.Linear1EV) linearGain = 2.0f;
         if (option == ExposureControlOption.Curve0EV)  linearGain = 1.0f; // Not used but sane default
         if (option == ExposureControlOption.Curve1EV)  linearGain = 2.0f;

        VulkanImage? blurredTex = null;
        using var maxBuffer = new VulkanBuffer(_ctx, 4, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit);
        
        // 1. Prepare Data
        if (isCurve)
        {
            // Blur for local luminance estimate
            // kernel_size dependent on mosaic
            int kSize = (metadata.MosaicPatternWidth == 2) ? 1 : 2; // Simple heurstic
            // Swift: if 6 -> 2, if 2 -> 1, else 1.
            
            blurredTex = new VulkanImage(_ctx, image.Width, image.Height, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
            ExecuteBlur(image, blurredTex, kSize, metadata.MosaicPatternWidth);
            
            // Calculate Max of BLURRED texture?
            // Swift: texture_max(final_texture_blurred)
            ExecuteMaxReduction(blurredTex, maxBuffer, metadata.MosaicPatternWidth);
        }
        else
        {
            // Linear: Calculate Max of ORIGINAL texture
            ExecuteMaxReduction(image, maxBuffer, metadata.MosaicPatternWidth);
        }
        
        // 2. Prepare Params
        // Need ColorFactorMean
        float colorMean = 1.0f;
        if (metadata.ColorFactors.Length >= 3)
        {
             // Approx mean
             colorMean = (float)((metadata.ColorFactors[0] + metadata.ColorFactors[1] + metadata.ColorFactors[2])/3.0);
        }
        
        // Black Level Mean array
        // Swift: if uniform exposure, mean of all black levels. Else black level of longest exposure.
        // Here we simplify: use metadata.BlackLevel
        float[] blArray = new float[4];
        float blMean = 0;
        float blMin = float.MaxValue;
        for(int i=0; i<4; i++) 
        {
             float v = (i < metadata.BlackLevel.Length) ? (float)metadata.BlackLevel[i] : (float)metadata.BlackLevel[0];
             blArray[i] = v;
             blMean += v;
             if (v < blMin) blMin = v;
        }
        blMean /= 4.0f;
        
        using var blParams = new VulkanBuffer(_ctx, (ulong)(4*sizeof(float)), BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit);
        blParams.SetData(blArray);
        
        var exParams = new ExposureParams
        {
            WhiteLevel = metadata.WhiteLevel,
            LinearGain = linearGain,
            ColorFactorMean = colorMean,
            BlackLevelMean = blMean,
            BlackLevelMin = blMin,
            ExposureBias = metadata.ExposureBias,
            TargetExposure = option.ToString().Contains("1EV") ? metadata.ExposureBias + 100 : metadata.ExposureBias, // +1EV
            // Logic for target exposure:
            // If option contains 1EV, target = bias + 100?
            // Actually Swift sets correction_stops = (target - bias)/100.
            // If Curve0EV, correction=0. If Curve1EV, correction=1.
            // So Target = Bias + 100 * stops.
            MosaicPatternWidth = metadata.MosaicPatternWidth,
            TextureWidth = (int)image.Width
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<ExposureParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData(new[] { exParams });
        
        // 3. Dispatch
        using var dummyBuff = new VulkanBuffer(_ctx, 4, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit);
        
        var allocInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _ctx.CommandPool, CommandBufferCount = 1 };
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);
        
        image.TransitionLayout(ImageLayout.General, cmdBuffer);
        if (blurredTex != null) blurredTex.TransitionLayout(ImageLayout.General, cmdBuffer);
        
        var set = _descriptors.Allocate(_exposureLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<ExposureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, image.View, ImageLayout.General, DescriptorType.SampledImage); // t0
        _descriptors.UpdateImage(set, 2, blurredTex != null ? blurredTex.View : image.View, ImageLayout.General, DescriptorType.SampledImage); // t1 (Blurred or Stub)
        _descriptors.UpdateBuffer(set, 3, blParams.Handle, (ulong)(4*sizeof(float)), DescriptorType.StorageBuffer); // t2 BL
        _descriptors.UpdateBuffer(set, 4, maxBuffer.Handle, 4, DescriptorType.StorageBuffer); // t3 Max
        _descriptors.UpdateImage(set, 10, image.View, ImageLayout.General, DescriptorType.StorageImage); // u0 (In/Out)
        _descriptors.UpdateBuffer(set, 11, dummyBuff.Handle, 4, DescriptorType.StorageBuffer); // u1 unused
        
        var kernel = isCurve ? _kernelCorrectExposure : _kernelCorrectExposureLinear;
        kernel!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);
        _ctx.Vk.CmdDispatch(cmdBuffer, image.Width, image.Height, 1);
        
        _ctx.Vk.EndCommandBuffer(cmdBuffer);
        
        var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmdBuffer };
        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);
        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
        
        blurredTex?.Dispose();
    }

    /// <summary>
    /// GPU-based noise estimation using blur -> difference -> reduction pipeline.
    /// Replaces CPU-based EstimateColorNoise for better performance.
    /// </summary>
    private float ExecuteNoiseEstimationGPU(VulkanImage inputTexture, int mosaicPatternWidth)
    {
        EnsureNoiseEstPipeline();
        
        uint width = inputTexture.Width;
        uint height = inputTexture.Height;
        int kernelSize = 4; // Blur kernel size (matching Swift default)
        
        // Allocate intermediate textures
        using var blurredXY = new VulkanImage(_ctx, width, height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
            
        // Use factored out ExecuteBlur
        ExecuteBlur(inputTexture, blurredXY, kernelSize, mosaicPatternWidth);
        
        // diffTexture is (width/mosaic, height/mosaic) - one value per superpixel
        uint diffW = width / (uint)mosaicPatternWidth;
        uint diffH = height / (uint)mosaicPatternWidth;
        using var diffTexture = new VulkanImage(_ctx, diffW, diffH, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
            
        // ... (Color Difference code remains similar but simplified) ...
        var texParams = new TextureParams { MosaicPatternWidth = mosaicPatternWidth, Width = (int)width, Height = (int)height }; // Dummy params for Diff pass
        
        using var paramBufferDiff = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBufferDiff.SetData(new[] { texParams });
        
        var allocInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, Level = CommandBufferLevel.Primary, CommandPool = _ctx.CommandPool, CommandBufferCount = 1 };
        _ctx.Vk.AllocateCommandBuffers(_ctx.Device, in allocInfo, out var cmdBuffer);
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _ctx.Vk.BeginCommandBuffer(cmdBuffer, in beginInfo);
        
        inputTexture.TransitionLayout(ImageLayout.General, cmdBuffer);
        blurredXY.TransitionLayout(ImageLayout.General, cmdBuffer);
        diffTexture.TransitionLayout(ImageLayout.General, cmdBuffer);
        
        // Color Difference
        var setDiff = _descriptors.Allocate(_noiseEstLayout);
        _descriptors.UpdateBuffer(setDiff, 0, paramBufferDiff.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setDiff, 1, inputTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setDiff, 4, blurredXY.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(setDiff, 10, diffTexture.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelColorDiffSuperpixel!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelColorDiffSuperpixel.PipelineLayout, 0, 1, &setDiff, 0, null);
        _kernelColorDiffSuperpixel.Dispatch(cmdBuffer, diffW, diffH, 1);
        
        _ctx.Vk.EndCommandBuffer(cmdBuffer);
        
        var submitInfo = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmdBuffer };
        _ctx.Vk.QueueSubmit(_ctx.ComputeQueue, 1, in submitInfo, default);
        _ctx.Vk.QueueWaitIdle(_ctx.ComputeQueue);
        _ctx.Vk.FreeCommandBuffers(_ctx.Device, _ctx.CommandPool, 1, in cmdBuffer);
        
        // Read back diff and sum (rest is same)
        float[] diffData = diffTexture.GetData<float>();
        double totalDiff = 0;
        for (int i = 0; i < diffData.Length; i++) totalDiff += diffData[i];
         
        float meanDiff = (float)(totalDiff / (width * height));
        float noiseSd = meanDiff * mosaicPatternWidth * mosaicPatternWidth;
        
        Console.WriteLine($"[VulkanComputePipeline] GPU Noise Estimation: totalDiff={totalDiff:F2}, noiseSd={noiseSd:F2}");
        return Math.Max(noiseSd, 1.0f);
    }

    private float CalculateRobustness(double noiseReduction)
    {
        return (float)noiseReduction;
    }

    private ComputeKernel? _kernelUpsampleAlignment;
    private ComputeKernel? _kernelCorrectUpsamplingError;

    public void Dispose()
    {
        _descriptors.Dispose();
        
        if (_prepareLayout.Handle != 0) _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _prepareLayout, null);
        if (_alignLayout.Handle != 0) _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _alignLayout, null);
        if (_mergeLayout.Handle != 0) _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _mergeLayout, null);
        if (_accumLayout.Handle != 0) _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _accumLayout, null);
        if (_accumHighLayout.Handle != 0) _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _accumHighLayout, null);
        if (_noiseEstLayout.Handle != 0) _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _noiseEstLayout, null);
        if (_exposureLayout.Handle != 0) _ctx.Vk.DestroyDescriptorSetLayout(_ctx.Device, _exposureLayout, null);
        
        _kernelPrepareBayer?.Dispose();
        _kernelAvgPool?.Dispose();
        _kernelTileDiff?.Dispose();
        _kernelTileDiff25?.Dispose();
        _kernelTileDiffExposure25?.Dispose();
        _kernelFindBest?.Dispose();
        _kernelWarp?.Dispose();
        _kernelUpsampleAlignment?.Dispose();
        _kernelCorrectUpsamplingError?.Dispose();
        _kernelColorDiff?.Dispose();
        _kernelMergeWeight?.Dispose();
        _kernelAddWeighted?.Dispose();
        _kernelAddWeightOnly?.Dispose();
        _kernelAddExposure?.Dispose();
        _kernelAddHighlights?.Dispose();
        _kernelBlurMosaic?.Dispose();
        _kernelColorDiffSuperpixel?.Dispose();
        _kernelCorrectExposure?.Dispose();
        _kernelCorrectExposureLinear?.Dispose();
        _kernelMaxY?.Dispose();
        _kernelMaxX?.Dispose();
    }
}

