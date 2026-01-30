using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using BurstPhoto.Rendering.Validation;
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
    private ComputeKernel? _kernelAvgPoolNormalization;
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
    
    // FFT Validation: Set to true to run mathematical validation tests
    public bool EnableFftValidation { get; set; } = false;
    private List<Validation.ValidationResult> _validationResults = new();

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
            
            // Write using LibTiffDngWriter to avoid native dependency for debug dumps
            var writer = new BurstPhoto.Core.Implementations.LibTiffDngWriter();
            writer.Write(outputPath, debugImage);
            
            Console.WriteLine($"[DebugDump] Saved {stepName} successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DebugDump] Error saving {stepName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug: Saves an RGBA texture as a Bayer-pattern DNG file.
    /// RGBA channels are mapped to 2x2 Bayer pattern (R->top-left, G1->top-right, G2->bottom-left, B->bottom-right).
    /// </summary>
    private void DebugDumpRgba(VulkanImage rgbaImage, string stepName, RawImage refMeta)
    {
        if (!EnableDebugDump) return;

        try
        {
            if (!Directory.Exists(_debugOutputDir))
            {
                Directory.CreateDirectory(_debugOutputDir);
            }

            string outputPath = Path.Combine(_debugOutputDir, $"{stepName}.dng");
            Console.WriteLine($"[DebugDump] Saving RGBA {stepName} to {outputPath}...");

            // Get RGBA data
            float[] rgbaData = rgbaImage.GetData<float>();
            int rgbaWidth = (int)rgbaImage.Width;
            int rgbaHeight = (int)rgbaImage.Height;

            // Convert RGBA to Bayer (2x dimensions)
            int bayerWidth = rgbaWidth * 2;
            int bayerHeight = rgbaHeight * 2;
            var outputData = new ushort[bayerWidth * bayerHeight];

            for (int y = 0; y < rgbaHeight; y++)
            {
                for (int x = 0; x < rgbaWidth; x++)
                {
                    int rgbaIdx = (y * rgbaWidth + x) * 4;
                    float r = rgbaData[rgbaIdx + 0];
                    float g1 = rgbaData[rgbaIdx + 1];
                    float g2 = rgbaData[rgbaIdx + 2];
                    float b = rgbaData[rgbaIdx + 3];

                    // Map to 2x2 Bayer pattern (RGGB)
                    int bx = x * 2;
                    int by = y * 2;
                    outputData[by * bayerWidth + bx] = (ushort)Math.Clamp(r, 0, 65535);           // R
                    outputData[by * bayerWidth + bx + 1] = (ushort)Math.Clamp(g1, 0, 65535);     // G1
                    outputData[(by + 1) * bayerWidth + bx] = (ushort)Math.Clamp(g2, 0, 65535);   // G2
                    outputData[(by + 1) * bayerWidth + bx + 1] = (ushort)Math.Clamp(b, 0, 65535); // B
                }
            }

            // Create a RawImage for the DNG writer
            var debugImage = new RawImage
            {
                Width = bayerWidth,
                Height = bayerHeight,
                Data = outputData,
                MosaicPatternWidth = 2,
                WhiteLevel = refMeta.WhiteLevel,
                BlackLevel = refMeta.BlackLevel,
                ExposureBias = refMeta.ExposureBias,
                IsoExposureTime = refMeta.IsoExposureTime,
                ColorFactors = refMeta.ColorFactors,
                SourcePath = refMeta.SourcePath,
                CfaPattern = refMeta.CfaPattern,
                ColorMatrix1 = refMeta.ColorMatrix1,
                ColorMatrix2 = refMeta.ColorMatrix2,
                CalibrationIlluminant1 = refMeta.CalibrationIlluminant1,
                CalibrationIlluminant2 = refMeta.CalibrationIlluminant2,
                AsShotNeutral = refMeta.AsShotNeutral,
                CameraMake = refMeta.CameraMake,
                CameraModel = refMeta.CameraModel,
                IsBayerData = true
            };

            var writer = new BurstPhoto.Core.Implementations.LibTiffDngWriter();
            writer.Write(outputPath, debugImage);

            Console.WriteLine($"[DebugDump] Saved RGBA {stepName} ({bayerWidth}x{bayerHeight} Bayer) successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DebugDump] Error saving RGBA {stepName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Debug: Saves alignment vectors as a visualization DNG.
    /// X displacement shown in R, Y displacement shown in G (scaled and biased to be visible).
    /// </summary>
    private void DebugDumpAlignment(VulkanImage alignment, string stepName, RawImage refMeta)
    {
        if (!EnableDebugDump) return;

        try
        {
            if (!Directory.Exists(_debugOutputDir))
            {
                Directory.CreateDirectory(_debugOutputDir);
            }

            string outputPath = Path.Combine(_debugOutputDir, $"{stepName}.dng");
            Console.WriteLine($"[DebugDump] Saving alignment {stepName} to {outputPath}...");

            // Get int16 alignment data (RGBA format: x, y, 0, 0)
            short[] alignData = alignment.GetData<short>();
            int alignWidth = (int)alignment.Width;
            int alignHeight = (int)alignment.Height;

            // Analyze alignment data
            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            for (int i = 0; i < alignData.Length; i += 4)
            {
                short x = alignData[i];
                short y = alignData[i + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            Console.WriteLine($"[DebugDump] Alignment range: X=[{minX}, {maxX}], Y=[{minY}, {maxY}]");

            // Create Bayer visualization (2x dimensions)
            int bayerWidth = alignWidth * 2;
            int bayerHeight = alignHeight * 2;
            var outputData = new ushort[bayerWidth * bayerHeight];

            // Scale factor: map alignment range to 0-65535
            float scaleX = (maxX != minX) ? 65535f / (maxX - minX) : 1f;
            float scaleY = (maxY != minY) ? 65535f / (maxY - minY) : 1f;

            for (int y = 0; y < alignHeight; y++)
            {
                for (int x = 0; x < alignWidth; x++)
                {
                    int alignIdx = (y * alignWidth + x) * 4;
                    short dx = alignData[alignIdx];
                    short dy = alignData[alignIdx + 1];

                    // Scale to visible range
                    ushort rVal = (ushort)Math.Clamp((dx - minX) * scaleX, 0, 65535);
                    ushort gVal = (ushort)Math.Clamp((dy - minY) * scaleY, 0, 65535);
                    ushort bVal = (ushort)32768; // Neutral

                    // Map to 2x2 Bayer pattern (RGGB)
                    int bx = x * 2;
                    int by = y * 2;
                    outputData[by * bayerWidth + bx] = rVal;           // R (dx)
                    outputData[by * bayerWidth + bx + 1] = gVal;       // G1 (dy)
                    outputData[(by + 1) * bayerWidth + bx] = gVal;     // G2 (dy)
                    outputData[(by + 1) * bayerWidth + bx + 1] = bVal; // B (neutral)
                }
            }

            var debugImage = new RawImage
            {
                Width = bayerWidth,
                Height = bayerHeight,
                Data = outputData,
                MosaicPatternWidth = 2,
                WhiteLevel = 65535,
                BlackLevel = refMeta.BlackLevel,
                ExposureBias = 0,
                IsoExposureTime = 1.0f,
                ColorFactors = refMeta.ColorFactors,
                SourcePath = refMeta.SourcePath,
                CfaPattern = refMeta.CfaPattern,
                ColorMatrix1 = refMeta.ColorMatrix1,
                ColorMatrix2 = refMeta.ColorMatrix2,
                CalibrationIlluminant1 = refMeta.CalibrationIlluminant1,
                CalibrationIlluminant2 = refMeta.CalibrationIlluminant2,
                AsShotNeutral = refMeta.AsShotNeutral,
                CameraMake = "Debug",
                CameraModel = "AlignmentVisualization",
                IsBayerData = true
            };

            var writer = new BurstPhoto.Core.Implementations.LibTiffDngWriter();
            writer.Write(outputPath, debugImage);

            Console.WriteLine($"[DebugDump] Saved alignment {stepName} ({bayerWidth}x{bayerHeight} Bayer) successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DebugDump] Error saving alignment {stepName}: {ex.Message}");
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

            // Calculate exposure correction factors (Swift: frequency.swift lines 38-47)
            // These account for the fact that exposure-bracketed bursts include images with different SNR
            double exposureCorr1 = 0.0;
            double exposureCorr2 = 0.0;
            int refExpBias = refImage.ExposureBias;
            float refIsoExpTime = refImage.IsoExposureTime;

            // Check if we have meaningful exposure bias values (not all zeros)
            bool hasExposureBias = input.Images.Any(img => img.ExposureBias != 0);

            Console.WriteLine($"[VulkanComputePipeline] Exposure data: refBias={refExpBias}, refIsoExpTime={refIsoExpTime:F4}, hasExposureBias={hasExposureBias}");
            for (int i = 0; i < input.Images.Count; i++)
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

                // DEBUG DUMP: Prepared reference Bayer texture (to compare with warped comparison Bayer)
                DebugDump(preparedRef, $"step_1b_iter{iteration}_prepared_ref_bayer", refImage, iterOutWidth, iterOutHeight, 0);

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
                    
                    // CHECK LEFT EDGE at mid height - this is exactly where RGBA mid samples from!
                    // RGBA pixel (0, 768) reads from Bayer (padLeft, padTop + 768*2) = (260, 1788)
                    int leftEdgeRow = padTop + (height / 2); // 252 + 1536 = 1788
                    int leftEdgeStart = leftEdgeRow * iterOutWidth + padLeft;
                    double leftEdgeSum = 0;
                    for (int i = 0; i < Math.Min(100, prepData.Length - leftEdgeStart); i++) leftEdgeSum += Math.Abs(prepData[leftEdgeStart + i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After prepare (LEFT EDGE row {leftEdgeRow}): sum={leftEdgeSum:F2}, mean={leftEdgeSum/100.0:F4}");
                    
                    if (prepSum < 0.01) Console.WriteLine($"[WARNING] Prepare produced near-zero output!");
                }

                // RGBA dimensions: Match Swift's calculation from frequency.swift line 125 and texture.swift line 343
                // Swift: convert_to_rgba(ref_texture, crop_merge_x, crop_merge_y)
                // Swift output size: (in_texture.width - 2*crop_x)/2, (in_texture.height - 2*crop_y)/2
                // This crops cropMergeX/Y from each side of the padded texture, then halves for RGBA packing
                int rgbaWidth = (iterOutWidth - 2 * cropMergeX) / 2;
                int rgbaHeight = (iterOutHeight - 2 * cropMergeY) / 2;
                Console.WriteLine($"[DEBUG] Iteration {iteration}: RGBA dimensions: {rgbaWidth}x{rgbaHeight} (from padded {iterOutWidth}x{iterOutHeight}, cropMerge={cropMergeX},{cropMergeY})");
                int ftWidth = rgbaWidth * 2; // Complex storage (Real + Imaginary)
                int ftHeight = rgbaHeight;

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
                    float[] rgbaData = rgbaRefTexture.GetData<float>();

                    // Mid sample (original)
                    int startIdx = rgbaData.Length / 4;
                    int sampleSize = Math.Min(10000, rgbaData.Length - startIdx);
                    double rgbaSumMid = 0;
                    for (int i = 0; i < sampleSize; i++) rgbaSumMid += Math.Abs(rgbaData[startIdx + i]);

                    // TOTAL sum (to see if ANY data exists)
                    double rgbaTotal = 0;
                    for (int i = 0; i < rgbaData.Length; i++) rgbaTotal += Math.Abs(rgbaData[i]);

                    // Find first non-zero row
                    int rgbaWidth_px = rgbaWidth;
                    int firstNonZeroRow = -1;
                    for (int row = 0; row < rgbaHeight && firstNonZeroRow < 0; row++)
                    {
                        double rowSum = 0;
                        int rowStart = row * rgbaWidth_px * 4;  // 4 floats per RGBA pixel
                        for (int col = 0; col < Math.Min(100, rgbaWidth_px); col++)
                        {
                            int idx = rowStart + col * 4;  // Sample R channel
                            if (idx < rgbaData.Length) rowSum += Math.Abs(rgbaData[idx]);
                        }
                        if (rowSum > 0.01) firstNonZeroRow = row;
                    }

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After convert_to_rgba: mid10k={rgbaSumMid:F2}, TOTAL={rgbaTotal:F2}, firstNonZeroRow={firstNonZeroRow}");
                    if (rgbaTotal < 0.01) Console.WriteLine($"[WARNING] RGBA conversion produced COMPLETELY ZERO output!");
                }

                // DEBUG DUMP: Reference RGBA texture (before FFT)
                DebugDumpRgba(rgbaRefTexture, $"step_2_iter{iteration}_ref_rgba", refImage);

                // FFT VALIDATION: Run round-trip test on first iteration only
                if (EnableFftValidation && iteration == 1)
                {
                    Console.WriteLine("\n[VulkanComputePipeline] Running FFT validation (first iteration)...");
                    var validationResults = RunFftRoundTripValidation(rgbaRefTexture, tile_size_merge);
                    _validationResults.AddRange(validationResults);
                    
                    // Check if round-trip failed - if so, provide diagnosis and optionally stop
                    var roundTripResult = validationResults.FirstOrDefault(r => r.TestName.Contains("Round-Trip"));
                    if (roundTripResult != null && !roundTripResult.Passed)
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

                // DEBUG: Check FFT output - comprehensive sampling
                {
                    float[] fftData = refFT.GetData<float>();

                    // First 10000 (may be in zero padding region)
                    double fftSumFirst = 0;
                    int sampleSize = Math.Min(fftData.Length, 10000);
                    for (int i = 0; i < sampleSize; i++) fftSumFirst += Math.Abs(fftData[i]);

                    // Mid-point sample
                    double fftSumMid = 0;
                    int midStart = fftData.Length / 2;
                    for (int i = 0; i < sampleSize && midStart + i < fftData.Length; i++) fftSumMid += Math.Abs(fftData[midStart + i]);

                    // TOTAL sum (to see if ANY data exists)
                    double fftTotal = 0;
                    for (int i = 0; i < fftData.Length; i++) fftTotal += Math.Abs(fftData[i]);

                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After forward_fft: first10k={fftSumFirst:F2}, mid10k={fftSumMid:F2}, TOTAL={fftTotal:F2}");
                    if (fftTotal < 0.01) Console.WriteLine($"[WARNING] Forward FFT produced COMPLETELY ZERO output!");
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
                        double ratio = refImage.IsoExposureTime / Math.Max(altImage.IsoExposureTime, 0.0001);
                        prepareExpDiff = (int)Math.Round(Math.Log2(ratio) * 100.0);
                    }
                    Console.WriteLine($"[VulkanComputePipeline] Comparison {compIdx}: prepareExpDiff={prepareExpDiff} centistops");
                    ExecutePrepare(rawAlt, preparedAlt, altImage, padLeft, padTop, prepareExpDiff);

                    // DEBUG DUMP: Prepared comparison Bayer texture (before warp, to compare with warped)
                    DebugDump(preparedAlt, $"step_1c_iter{iteration}_prepared_comp{compIdx}_bayer", refImage, iterOutWidth, iterOutHeight, 0);

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
                    bool isUniformExposure = (altImage.ExposureBias == refImage.ExposureBias);
                    ExecuteAlignmentSearch(refPyramid, altPyramid, alignment, iterTileInfo, 2, isUniformExposure);

                    // DEBUG DUMP: Alignment vectors visualization
                    DebugDumpAlignment(alignment, $"step_2a_iter{iteration}_alignment_comp{compIdx}", refImage);

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
                    
                    ExecuteWarp(preparedAlt, warpedAlt, alignment, iterTileInfo, padLeft, padTop);

                    // DEBUG DUMP: Warped Bayer texture (before RGBA conversion) to see if split is in warp
                    DebugDump(warpedAlt, $"step_2b_iter{iteration}_warped_comp{compIdx}_bayer", refImage, iterOutWidth, iterOutHeight, 0);

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
                    // CRITICAL: Use cropMergeX/cropMergeY as crop offset (same as reference)
                    // This ensures aligned texture uses the same spatial window as reference
                    ExecuteConvertToRgba(warpedAlt, alignedTextureRgba, refImage.CfaPattern, cropMergeX, cropMergeY);

                    // DEBUG: Check alignedTextureRgba after RGBA conversion
                    {
                        float[] rgbaData = alignedTextureRgba.GetData<float>();
                        double rgbaSum = 0;
                        double rgbaSumMid = 0;
                        int rgbaSamples = Math.Min(rgbaData.Length, 1000);
                        int midStart = rgbaData.Length / 2;
                        for (int i = 0; i < rgbaSamples; i++) rgbaSum += Math.Abs(rgbaData[i]);
                        for (int i = 0; i < rgbaSamples && midStart + i < rgbaData.Length; i++) rgbaSumMid += Math.Abs(rgbaData[midStart + i]);
                        Console.WriteLine($"[WARP DEBUG] alignedTextureRgba AFTER convert: first1000 sum={rgbaSum:F2}, mid1000 sum={rgbaSumMid:F2}");
                        Console.WriteLine($"[WARP DEBUG]   Total size={rgbaData.Length} floats ({rgbaWidth}x{rgbaHeight}x4)");
                    }

                    // DEBUG DUMP: Aligned comparison frame (after warp + convert to RGBA)
                    DebugDumpRgba(alignedTextureRgba, $"step_3_iter{iteration}_aligned_comp{compIdx}_rgba", refImage);

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
                    // Now also accumulates normalized mismatch into totalMismatchTexture for deconvolution
                    // Note: For uniformExp, we check if exposures are equal using the same logic as hasExposureBias
                    int uniformExp = hasExposureBias
                        ? (altImage.ExposureBias == refImage.ExposureBias ? 1 : 0)
                        : (Math.Abs(altImage.IsoExposureTime - refImage.IsoExposureTime) < 0.001f ? 1 : 0);
                    // expDiff for merge uses (comp - ref), opposite of prepare (ref - comp)
                    float expDiffForMerge = (float)(-prepareExpDiff);
                    ExecuteMergeFrequency(refFT, rgbaRefTexture, alignedTextureRgba, null!, finalTextureFT,
                        refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd, expDiffForMerge, tileSize, refImage.MosaicPatternWidth, uniformExp,
                        totalMismatchTexture, input.Images.Count, exposureCorr1 / exposureCorr2);

                    // Cleanup alt pyramid
                    foreach (var lvl in altPyramid) if (lvl != preparedAlt) lvl.Dispose();
                }

                // Post-iteration processing
                // DEBUG: Check finalTextureFT before deconvolution (use TOTAL to avoid zero-padding confusion)
                {
                    float[] preDec = finalTextureFT.GetData<float>();
                    double preDecTotal = 0;
                    for (int i = 0; i < preDec.Length; i++) preDecTotal += Math.Abs(preDec[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: Before deconvolution: TOTAL={preDecTotal:F2}, mean={preDecTotal/preDec.Length:F4}");
                }

                // Deconvolute with accumulated mismatch
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Deconvolution...");
                ExecuteDeconvoluteFrequency(finalTextureFT, totalMismatchTexture, nTilesX, nTilesY, tile_size_merge);

                // DEBUG: Check finalTextureFT after deconvolution (use TOTAL to avoid zero-padding confusion)
                {
                    float[] postDec = finalTextureFT.GetData<float>();
                    double postDecTotal = 0;
                    for (int i = 0; i < postDec.Length; i++) postDecTotal += Math.Abs(postDec[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After deconvolution: TOTAL={postDecTotal:F2}, mean={postDecTotal/postDec.Length:F4}");
                    if (postDecTotal < 0.01) Console.WriteLine($"[WARNING] Deconvolution produced near-zero output!");
                }

                // Backward FFT
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Backward FFT...");
                using var outputTextureRgba = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteBackwardFft(finalTextureFT, outputTextureRgba, input.Images.Count, tile_size_merge);

                // DEBUG: Check backward FFT output and decode shader debug info
                {
                    float[] backFftData = outputTextureRgba.GetData<float>();

                    // Decode debug info from corner pixels (16×16 region)
                    Console.WriteLine("[DEBUG] === Shader Debug Info (backward_fft) ===");
                    Console.WriteLine("[DEBUG] First 16×16 pixels encode: R=threadX, G=threadY, B=nTilesX, A=nTilesY (as raw floats)");

                    // Read corner pixel (0,0) which should have nTilesX/nTilesY info
                    int stride = rgbaWidth * 4; // RGBA = 4 channels
                    float threadX_0_0 = backFftData[0];
                    float threadY_0_0 = backFftData[1];
                    float shader_nTilesX = backFftData[2];
                    float shader_nTilesY = backFftData[3];
                    Console.WriteLine($"[DEBUG] Pixel(0,0): threadID=({threadX_0_0:F0},{threadY_0_0:F0}), shader_nTilesX={shader_nTilesX:F0}, shader_nTilesY={shader_nTilesY:F0}");
                    Console.WriteLine($"[DEBUG] Dispatched: {rgbaWidth/tile_size_merge}x{rgbaHeight/tile_size_merge} threads (for {rgbaWidth}x{rgbaHeight} texture, tileSize={tile_size_merge})");

                    // Check if any threads beyond (128,96) executed
                    bool foundBeyond128 = false;
                    int maxThreadX = 0, maxThreadY = 0;
                    for (int y = 0; y < 16 && y < rgbaHeight; y++) {
                        for (int x = 0; x < 16 && x < rgbaWidth; x++) {
                            int idx = (y * rgbaWidth + x) * 4;
                            float threadX = backFftData[idx + 0];
                            float threadY = backFftData[idx + 1];
                            maxThreadX = Math.Max(maxThreadX, (int)threadX);
                            maxThreadY = Math.Max(maxThreadY, (int)threadY);
                            if (threadX >= 128 || threadY >= 96) {
                                foundBeyond128 = true;
                            }
                        }
                    }
                    Console.WriteLine($"[DEBUG] Max thread IDs in debug region: ({maxThreadX}, {maxThreadY})");
                    if (!foundBeyond128) {
                        Console.WriteLine("[DEBUG] WARNING: No threads with X>=128 or Y>=96 found in debug region!");
                    }

                    // Use TOTAL sum to avoid zero-padding confusion
                    double backFftTotal = 0;
                    for (int i = 0; i < backFftData.Length; i++) backFftTotal += Math.Abs(backFftData[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After backward_fft: TOTAL={backFftTotal:F2}, mean={backFftTotal/backFftData.Length:F4}");
                    if (backFftTotal < 0.01) Console.WriteLine($"[WARNING] Backward FFT produced near-zero output!");
                }

                // DEBUG DUMP: Merged RGBA output (after backward FFT, before reduce_artifacts)
                DebugDumpRgba(outputTextureRgba, $"step_4_iter{iteration}_merged_before_reduce", refImage);

                // Reduce tile border artifacts
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Reducing artifacts...");
                int bayerTilesX = nTilesX;
                int bayerTilesY = nTilesY;

                // DEBUG: Analyze tile border values BEFORE reduce_artifacts
                {
                    float[] preArtifactData = outputTextureRgba.GetData<float>();
                    AnalyzeTileBorders(preArtifactData, rgbaWidth, rgbaHeight, tile_size_merge, iteration, "BEFORE (merged)");

                    // Also analyze reference texture for comparison
                    float[] refData = rgbaRefTexture.GetData<float>();
                    AnalyzeTileBorders(refData, rgbaWidth, rgbaHeight, tile_size_merge, iteration, "REFERENCE");
                }

                // reduce_artifacts_tile_border: FIXED - now uses tile-based dispatch matching Swift
                // Previously caused 8x8 grid pattern due to per-pixel dispatch model mismatch
                ExecuteReduceArtifacts(outputTextureRgba, rgbaRefTexture, bayerTilesX, bayerTilesY, tile_size_merge, refImage.BlackLevel);

                // DEBUG: Analyze tile border values AFTER reduce_artifacts
                {
                    float[] artifactData = outputTextureRgba.GetData<float>();
                    AnalyzeTileBorders(artifactData, rgbaWidth, rgbaHeight, tile_size_merge, iteration, "AFTER");

                    // Use TOTAL sum to avoid zero-padding confusion
                    double artifactTotal = 0;
                    for (int i = 0; i < artifactData.Length; i++) artifactTotal += Math.Abs(artifactData[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After reduce_artifacts: TOTAL={artifactTotal:F2}, mean={artifactTotal/artifactData.Length:F4}");
                }

                // DEBUG DUMP: Merged RGBA output (after reduce_artifacts)
                DebugDumpRgba(outputTextureRgba, $"step_5_iter{iteration}_merged_after_reduce", refImage);

                // Convert RGBA → Bayer
                // Bayer dimensions are 2x RGBA dimensions
                int bayerWidth = rgbaWidth * 2;
                int bayerHeight = rgbaHeight * 2;
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration}: Converting to Bayer ({bayerWidth}x{bayerHeight})...");
                using var outputTextureBayer = new VulkanImage(_ctx, (uint)bayerWidth, (uint)bayerHeight, Format.R32Sfloat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                ExecuteConvertToBayer(outputTextureRgba, outputTextureBayer, refImage.CfaPattern);

                // DEBUG: Check convert_to_bayer output (use TOTAL to avoid zero-padding confusion)
                {
                    float[] bayerData = outputTextureBayer.GetData<float>();
                    double bayerTotal = 0;
                    for (int i = 0; i < bayerData.Length; i++) bayerTotal += Math.Abs(bayerData[i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: After convert_to_bayer: TOTAL={bayerTotal:F2}, mean={bayerTotal/bayerData.Length:F4}");
                    if (bayerTotal < 0.01) Console.WriteLine($"[WARNING] Convert to Bayer produced near-zero output!");
                }

                // DEBUG DUMP: Bayer output right after convert_to_bayer (before exposure correction/accumulation)
                // This isolates whether 16-pixel mirroring is introduced by FFT/merge or by convert_to_bayer
                DebugDump(outputTextureBayer, $"step_5b_iter{iteration}_bayer_after_convert", refImage, bayerWidth, bayerHeight, 0);

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
                int cropLeft = padMergeX + shiftLeft;
                int cropRight = padMergeX + shiftRight;
                int cropTop = padMergeY + shiftTop;
                int cropBottom = padMergeY + shiftBottom;

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
                int croppedWidth = bayerWidth - cropLeft - cropRight;
                int croppedHeight = bayerHeight - cropTop - cropBottom;
                Console.WriteLine($"[DEBUG] Iteration {iteration}: Crop: left={cropLeft}, right={cropRight}, top={cropTop}, bottom={cropBottom}");
                Console.WriteLine($"[DEBUG] Iteration {iteration}: BayerSize={bayerWidth}x{bayerHeight}, CroppedSize={croppedWidth}x{croppedHeight}, ExpectedSize={width}x{height}");

                if (croppedWidth != width || croppedHeight != height)
                {
                    Console.WriteLine($"[WARNING] Crop size mismatch! Expected {width}x{height}, got {croppedWidth}x{croppedHeight}");
                }

                float[] iterOutput = outputTextureBayer.GetData<float>();

                // DEBUG: Check iteration output
                double iterSum = 0;
                for (int i = 0; i < Math.Min(iterOutput.Length, 100000); i++) iterSum += iterOutput[i];
                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} output: sum={iterSum:F2}, mean={iterSum/Math.Min(iterOutput.Length, 100000):F2}");

                // DEBUG: Track per-iteration values at specific positions to verify window sum = 1.0
                {
                    // Track a few specific RGBA positions across all 4 iterations
                    // These are FINAL output positions (after crop), so they should align across iterations
                    int trackY = 60; // Final RGBA Y position to track

                    // Store values in static-like storage for cross-iteration tracking
                    // We'll track RGBA positions 0-7 to cover a full tile width
                    string iterKey = $"iter{iteration}";
                    Console.WriteLine($"[WEIGHT_SUM] Iteration {iteration}: cropLeft={cropLeft} Bayer = {cropLeft/2} RGBA");

                    // For each final RGBA X position 0-7, find where it comes from in this iteration
                    for (int finalX = 0; finalX < 8; finalX++)
                    {
                        // The final RGBA position maps to Bayer position (finalX*2, trackY*2)
                        // in the accumulator. But we need to find it in THIS iteration's output.

                        // In accumulator: destination is at (padAlignX + finalX*2, padAlignY + trackY*2) in Bayer
                        // In this iteration's Bayer output: source is at (cropLeft + finalX*2, cropTop + trackY*2)
                        int srcBayerX = cropLeft + finalX * 2;
                        int srcBayerY = cropTop + trackY * 2;

                        // Get the 2x2 Bayer block sum (RGBA equivalent)
                        double p0 = iterOutput[srcBayerY * bayerWidth + srcBayerX];
                        double p1 = iterOutput[srcBayerY * bayerWidth + srcBayerX + 1];
                        double p2 = iterOutput[(srcBayerY+1) * bayerWidth + srcBayerX];
                        double p3 = iterOutput[(srcBayerY+1) * bayerWidth + srcBayerX + 1];
                        double rgbaSum = p0 + p1 + p2 + p3;

                        // What tile-relative position does this come from?
                        // In RGBA space: srcRgbaX = srcBayerX/2 - cropMergeX
                        int srcRgbaX = srcBayerX / 2; // Position in RGBA texture (before any crop)
                        int tileRelX = srcRgbaX % 8;  // Position within 8x8 RGBA tile

                        Console.WriteLine($"[WEIGHT_SUM]   FinalX={finalX}: srcBayer={srcBayerX}, srcRgba={srcRgbaX}, tileRel={tileRelX}, sum={rgbaSum:F1}");
                    }
                }

                float[] accData = finalAccumulator.GetData<float>();

                // DEBUG: Check what GetData returns at start of iteration
                {
                    int dataStartIdx = padAlignY * accWidth + padAlignX;
                    double preSum = 0;
                    int samples = Math.Min(10000, accData.Length - dataStartIdx);
                    for (int i = 0; i < samples; i++) preSum += Math.Abs(accData[dataStartIdx + i]);
                    Console.WriteLine($"[DEBUG] Iteration {iteration}: GetData BEFORE accumulation: sum={preSum:F2}");
                }

                // Copy CROPPED region from iteration output to accumulator
                // Source: read from [cropLeft, cropTop] in Bayer output
                // Dest: write to [padAlignX, padAlignY] in accumulator
                int pixelsUpdated = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Read from cropped region of source
                        int srcX = cropLeft + x;
                        int srcY = cropTop + y;
                        int srcIdx = srcY * bayerWidth + srcX;

                        // Write to fixed position in accumulator
                        int dstIdx = (y + padAlignY) * accWidth + (x + padAlignX);

                        if (srcIdx < iterOutput.Length && dstIdx < accData.Length)
                        {
                            accData[dstIdx] += iterOutput[srcIdx];
                            pixelsUpdated++;
                        }
                    }
                }
                finalAccumulator.SetData(accData);
                Console.WriteLine($"[VulkanComputePipeline] Updated {pixelsUpdated} pixels in accumulator");
                
                // DEBUG: Verify accumulator has data after SetData
                {
                    float[] verifyData = finalAccumulator.GetData<float>();
                    
                    // Sample from DATA REGION (after padding offset), not from start (which is padding)
                    int dataStartIdx = padAlignY * accWidth + padAlignX;
                    double verifySum = 0;
                    int samples = Math.Min(10000, verifyData.Length - dataStartIdx);
                    for (int i = 0; i < samples; i++) verifySum += Math.Abs(verifyData[dataStartIdx + i]);
                    Console.WriteLine($"[DEBUG] After SetData: accumulator data region sum={verifySum:F2} (at offset {dataStartIdx})");
                    
                    // Also verify first few actual values
                    if (samples > 0)
                    {
                        Console.WriteLine($"[DEBUG] First 5 values at data region: {verifyData[dataStartIdx]:F4}, {verifyData[dataStartIdx+1]:F4}, {verifyData[dataStartIdx+2]:F4}, {verifyData[dataStartIdx+3]:F4}, {verifyData[dataStartIdx+4]:F4}");
                    }
                    
                    // Check CPU array at same location
                    double cpuSum = 0;
                    for (int i = 0; i < samples; i++) cpuSum += Math.Abs(accData[dataStartIdx + i]);
                    Console.WriteLine($"[DEBUG] CPU accData data region sum={cpuSum:F2}");
                }

                // Cleanup ref pyramid
                foreach (var lvl in refPyramid) if (lvl != preparedRef) lvl.Dispose();

                Console.WriteLine($"[VulkanComputePipeline] Iteration {iteration} complete");
            }

            // Use finalAccumulator as the merged result
            Console.WriteLine("[VulkanComputePipeline] All 4 iterations complete");

            // DEBUG: Check accumulator BEFORE noise estimation
            {
                float[] preNoiseData = finalAccumulator.GetData<float>();
                int dataStartIdx = padAlignY * accWidth + padAlignX;
                double preNoiseSum = 0;
                int samples = Math.Min(10000, preNoiseData.Length - dataStartIdx);
                for (int i = 0; i < samples; i++) preNoiseSum += Math.Abs(preNoiseData[dataStartIdx + i]);
                Console.WriteLine($"[DEBUG] Accumulator BEFORE noise estimation: sum={preNoiseSum:F2}");
            }

            estimatedNoiseSd = ExecuteNoiseEstimationGPU(finalAccumulator, refImage.MosaicPatternWidth);

            // DEBUG: Check accumulator AFTER noise estimation
            {
                float[] postNoiseData = finalAccumulator.GetData<float>();
                int dataStartIdx = padAlignY * accWidth + padAlignX;
                double postNoiseSum = 0;
                int samples = Math.Min(10000, postNoiseData.Length - dataStartIdx);
                for (int i = 0; i < samples; i++) postNoiseSum += Math.Abs(postNoiseData[dataStartIdx + i]);
                Console.WriteLine($"[DEBUG] Accumulator AFTER noise estimation: sum={postNoiseSum:F2}");
            }

            // Download result from final accumulator
            Console.WriteLine($"[VulkanComputePipeline] Downloading from finalAccumulator: Width={finalAccumulator.Width}, Height={finalAccumulator.Height}");
            floatData = finalAccumulator.GetData<float>();
            Console.WriteLine($"[VulkanComputePipeline] Downloaded {floatData.Length} floats (expected {finalAccumulator.Width * finalAccumulator.Height})");

            // DEBUG: Check if data is all zeros
            // Check data region only (skip padding which is all zeros)
            int dataStartIdx2 = padAlignY * accWidth + padAlignX;
            double sum = 0;
            double absSum = 0;
            double min = double.MaxValue;
            double max = double.MinValue;
            int dataRegionSize = Math.Min(width * height, floatData.Length - dataStartIdx2);
            for (int i = 0; i < dataRegionSize; i++)
            {
                float val = floatData[dataStartIdx2 + i];
                sum += val;
                absSum += Math.Abs(val);
                if (val < min) min = val;
                if (val > max) max = val;
            }
            Console.WriteLine($"[VulkanComputePipeline] FinalAccumulator stats (data region): sum={sum:F2}, absSum={absSum:F2}, min={min:F2}, max={max:F2}, mean={sum/dataRegionSize:F2}");

            // DEBUG: Analyze tile boundary patterns in final Bayer accumulator
            // Check if there are systematic low/high values at tile boundaries
            // Bayer tile boundaries are at multiples of (tile_size_merge * 2) = 16 pixels
            AnalyzeBayerTileBoundaries(floatData, accWidth, accHeight, padAlignX, padAlignY, tile_size_merge * 2);

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
                 ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo, pad, pad);

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

        // avg_pool_normalization (with color factor normalization for level 0)
        string sourceAvgNorm = source.Replace("void avg_pool_normalization(", "void CSMain(");
        var avgNormSpirv = _compiler.Compile(sourceAvgNorm, "CSMain");
        _kernelAvgPoolNormalization = new ComputeKernel(_ctx, _alignLayout, avgNormSpirv, "CSMain", 16, 16, 1);

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
        float whiteLevel, float blackLevel, double noiseReduction, float noiseSd, float exposureDiff, int tileSize, int mosaicPatternWidth, int uniformExposure,
        VulkanImage? totalMismatchTexture = null, int totalImageCount = 1, double exposureCorrRatio = 1.0)
    {
        EnsureMergeFrequencyPipeline();
        
        int width = (int)refPyramid0.Width;
        int height = (int)refPyramid0.Height;
        
        // CRITICAL FIX: Swift hardcodes tile_size_merge = 8 for FFT merging
        // See frequency.swift line 35: "let tile_size_merge = Int(8)"
        const int tile_size_merge = 8;
        
        // Calculate tile grid dimensions (Swift: tile_info_merge.n_tiles_x/y)
        // width here is rgbaWidth (already halved from Bayer), so divide by tile_size_merge, NOT 2*tile_size_merge
        int nTilesX = width / tile_size_merge;
        int nTilesY = height / tile_size_merge;
        
        float noise = noiseSd;
        
        // CRITICAL FIX: Use Swift's robustness formula
        // See frequency.swift lines 50-54
        bool isUniformExposure = (uniformExposure == 1);
        double robustness_rev = 0.5 * ((isUniformExposure ? 26.5 : 28.5) - Math.Round(noiseReduction));
        // Swift: robustness_norm = exposure_corr1/exposure_corr2 * pow(2.0, (-robustness_rev + 7.5))
        double robustness_norm = exposureCorrRatio * Math.Pow(2.0, -robustness_rev + 7.5);
        double read_noise = Math.Pow(Math.Pow(2.0, -robustness_rev + 10.0), 1.6);
        double max_motion_norm = Math.Max(1.0, Math.Pow(1.3, 11.0 - robustness_rev));
        
        // Swift: pow(2.0, (Double(exposure_bias[comp]-exposure_bias[ref])/100.0))
        // exposureDiff is in centistops — must divide by 100 to get EV
        float exposureFactor = (float)Math.Pow(2.0, exposureDiff / 100.0);
        
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
            BlackLevel0 = 0, BlackLevel1 = 0, BlackLevel2 = 0, BlackLevel3 = 0
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
            _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if(t0!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t1!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t2!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t3!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.MismatchTexture, t3.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t4!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.HighlightsTexture, t4.View, ImageLayout.General, DescriptorType.SampledImage);
            if(u0!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, u0.View, ImageLayout.General, DescriptorType.StorageImage);

            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

            // FIX: Dispatch expects pixel dimensions, not pre-calculated groups!
            kernel.Dispatch(cmd2, (uint)width, (uint)height, 1);

            _ctx.EndSingleTimeCommands(cmd2);
        }

        // Helper to dispatch FFT kernels (per-tile dispatch)
        void DispatchTile(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage t1 = null, VulkanImage t2 = null, VulkanImage t3 = null, VulkanImage t4 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if(t0!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t1!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t2!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t3!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.MismatchTexture, t3.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t4!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.HighlightsTexture, t4.View, ImageLayout.General, DescriptorType.SampledImage);
            if(u0!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, u0.View, ImageLayout.General, DescriptorType.StorageImage);

            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

            // FIX: Dispatch expects tile counts (width/tileSize, height/tileSize), not pre-calculated groups!
            uint nTilesX_local = (uint)(width / tile_size_merge);
            uint nTilesY_local = (uint)(height / tile_size_merge);
            kernel.Dispatch(cmd2, nTilesX_local, nTilesY_local, 1);

            _ctx.EndSingleTimeCommands(cmd2);
        }

        // Helper to dispatch tile-grid kernels (for RMS, Mismatch, Highlights)
        void DispatchTileGrid(ComputeKernel kernel, VulkanImage u0, VulkanImage t0, VulkanImage t1 = null, VulkanImage t2 = null)
        {
            var cmd2 = _ctx.BeginSingleTimeCommands();
            var set = _descriptors.Allocate(_frequencyLayout);
            _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
            if(t0!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, t0.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t1!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, t1.View, ImageLayout.General, DescriptorType.SampledImage);
            if(t2!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, t2.View, ImageLayout.General, DescriptorType.SampledImage);
            if(u0!=null) _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, u0.View, ImageLayout.General, DescriptorType.StorageImage);
            
            kernel.BindPipeline(cmd2);
            _ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

            // FIX: Dispatch expects tile counts (nTilesX, nTilesY), not pre-calculated groups!
            kernel.Dispatch(cmd2, (uint)nTilesX, (uint)nTilesY, 1);

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

        // 5b. Accumulate normalized mismatch into totalMismatchTexture (Swift: add_texture)
        // Swift: add_texture(mismatch_texture, total_mismatch_texture, textures.count)
        if (totalMismatchTexture != null && totalImageCount > 1)
        {
            // Accumulate: totalMismatch += mismatch / totalImageCount
            // We do this via CPU readback since we don't have an RGBA add kernel in frequency layout
            float[] mismatchData = texMismatch.GetData<float>();
            float[] accumData = totalMismatchTexture.GetData<float>();
            float divisor = (float)totalImageCount;
            for (int i = 0; i < mismatchData.Length; i++)
            {
                accumData[i] += mismatchData[i] / divisor;
            }
            totalMismatchTexture.SetData(accumData);
        }

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

        // DEBUG: Check output from FFT (use TOTAL to avoid zero-padding false alarms)
        {
            float[] outputData = texAlignedFT.GetData<float>();
            double outputTotal = 0;
            for (int i = 0; i < outputData.Length; i++) outputTotal += Math.Abs(outputData[i]);
            Console.WriteLine($"[FFT DEBUG] AFTER FFT: texAlignedFT TOTAL={outputTotal:F2}, mean={outputTotal/outputData.Length:F4}");
            if (outputTotal < 0.01) Console.WriteLine($"[FFT DEBUG] ❌ FFT OUTPUT IS ZERO!");
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

    /// <summary>
    /// Analyzes tile boundary patterns in the final Bayer accumulator.
    /// Checks for systematic differences at tile boundaries vs centers.
    /// </summary>
    private void AnalyzeBayerTileBoundaries(float[] bayerData, int width, int height, int padX, int padY, int bayerTileSize)
    {
        Console.WriteLine($"[BAYER_TILE_DIAG] Analyzing Bayer accumulator for tile boundary artifacts...");
        Console.WriteLine($"[BAYER_TILE_DIAG] Dimensions: {width}x{height}, Padding: ({padX},{padY}), BayerTileSize: {bayerTileSize}");

        // Sample region (skip padding)
        int startX = padX + 100;
        int startY = padY + 100;
        int endX = Math.Min(startX + 400, width - padX - 100);
        int endY = Math.Min(startY + 400, height - padY - 100);

        if (endX <= startX || endY <= startY)
        {
            Console.WriteLine($"[BAYER_TILE_DIAG] Sample region too small, skipping analysis");
            return;
        }

        // Collect statistics for different positions relative to tile boundaries
        // Position 0 = at boundary, Position 1-7 = inside tile
        double[] positionSums = new double[bayerTileSize];
        int[] positionCounts = new int[bayerTileSize];

        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int idx = y * width + x;
                if (idx >= bayerData.Length) continue;

                float val = bayerData[idx];

                // Calculate position within tile (0 = boundary)
                int posInTileX = x % bayerTileSize;
                int posInTileY = y % bayerTileSize;

                // Use minimum distance to any boundary
                int distToXBoundary = Math.Min(posInTileX, bayerTileSize - 1 - posInTileX);
                int distToYBoundary = Math.Min(posInTileY, bayerTileSize - 1 - posInTileY);
                int minDist = Math.Min(distToXBoundary, distToYBoundary);

                positionSums[minDist] += val;
                positionCounts[minDist]++;
            }
        }

        Console.WriteLine($"[BAYER_TILE_DIAG] Value by distance from tile boundary:");
        for (int d = 0; d < bayerTileSize / 2 + 1 && d < positionSums.Length; d++)
        {
            if (positionCounts[d] > 0)
            {
                double mean = positionSums[d] / positionCounts[d];
                Console.WriteLine($"  Distance {d}: mean={mean:F2}, count={positionCounts[d]}");
            }
        }

        // Calculate boundary vs center ratio
        double boundaryMean = positionCounts[0] > 0 ? positionSums[0] / positionCounts[0] : 0;
        double centerMean = 0;
        int centerCount = 0;
        for (int d = 2; d < bayerTileSize / 2 && d < positionSums.Length; d++)
        {
            centerMean += positionSums[d];
            centerCount += positionCounts[d];
        }
        centerMean = centerCount > 0 ? centerMean / centerCount : 0;

        Console.WriteLine($"[BAYER_TILE_DIAG] Summary: boundaryMean={boundaryMean:F2}, centerMean={centerMean:F2}, ratio={boundaryMean/Math.Max(1, centerMean):F4}");

        // Check for a specific horizontal line artifact
        // Sample values along a horizontal line at Y where Y % bayerTileSize == 0 vs Y % bayerTileSize == bayerTileSize/2
        int lineY_boundary = ((startY / bayerTileSize) + 1) * bayerTileSize; // First tile boundary after startY
        int lineY_center = lineY_boundary + bayerTileSize / 2;

        if (lineY_boundary < endY && lineY_center < endY)
        {
            double boundaryLineSum = 0;
            double centerLineSum = 0;
            int lineCount = 0;

            for (int x = startX; x < endX; x++)
            {
                int idxBoundary = lineY_boundary * width + x;
                int idxCenter = lineY_center * width + x;

                if (idxBoundary < bayerData.Length && idxCenter < bayerData.Length)
                {
                    boundaryLineSum += bayerData[idxBoundary];
                    centerLineSum += bayerData[idxCenter];
                    lineCount++;
                }
            }

            if (lineCount > 0)
            {
                Console.WriteLine($"[BAYER_TILE_DIAG] Horizontal line comparison (Y={lineY_boundary} vs Y={lineY_center}):");
                Console.WriteLine($"  Boundary line mean: {boundaryLineSum/lineCount:F2}");
                Console.WriteLine($"  Center line mean: {centerLineSum/lineCount:F2}");
                Console.WriteLine($"  Ratio: {(boundaryLineSum/lineCount) / Math.Max(1, centerLineSum/lineCount):F4}");
            }
        }
    }

    /// <summary>
    /// Analyzes tile border values to diagnose tile boundary artifacts.
    /// Compares values at tile borders vs tile centers.
    /// </summary>
    private void AnalyzeTileBorders(float[] rgbaData, int rgbaWidth, int rgbaHeight, int tileSize, int iteration, string phase)
    {
        // RGBA data has 4 channels per pixel
        const int channels = 4;
        int stride = rgbaWidth * channels;

        // Sample a few tiles to analyze border vs center values
        int numTilesX = rgbaWidth / tileSize;
        int numTilesY = rgbaHeight / tileSize;

        // Collect statistics for borders and centers
        double borderSum = 0, borderAbsSum = 0;
        double centerSum = 0, centerAbsSum = 0;
        int borderCount = 0, centerCount = 0;
        double borderMin = double.MaxValue, borderMax = double.MinValue;
        double centerMin = double.MaxValue, centerMax = double.MinValue;

        // Sample tiles (skip first and last to avoid edge effects)
        int sampleTileX1 = Math.Min(5, numTilesX - 2);
        int sampleTileX2 = Math.Min(10, numTilesX - 2);
        int sampleTileY1 = Math.Min(5, numTilesY - 2);
        int sampleTileY2 = Math.Min(10, numTilesY - 2);

        for (int tileY = sampleTileY1; tileY <= sampleTileY2 && tileY < numTilesY; tileY++)
        {
            for (int tileX = sampleTileX1; tileX <= sampleTileX2 && tileX < numTilesX; tileX++)
            {
                int tileStartX = tileX * tileSize;
                int tileStartY = tileY * tileSize;

                for (int dy = 0; dy < tileSize; dy++)
                {
                    for (int dx = 0; dx < tileSize; dx++)
                    {
                        int px = tileStartX + dx;
                        int py = tileStartY + dy;
                        int idx = (py * rgbaWidth + px) * channels;

                        if (idx + 3 >= rgbaData.Length) continue;

                        // Sample R channel (index 0)
                        float val = rgbaData[idx];

                        bool isBorder = (dx == 0 || dx == tileSize - 1 || dy == 0 || dy == tileSize - 1);

                        if (isBorder)
                        {
                            borderSum += val;
                            borderAbsSum += Math.Abs(val);
                            borderMin = Math.Min(borderMin, val);
                            borderMax = Math.Max(borderMax, val);
                            borderCount++;
                        }
                        else
                        {
                            centerSum += val;
                            centerAbsSum += Math.Abs(val);
                            centerMin = Math.Min(centerMin, val);
                            centerMax = Math.Max(centerMax, val);
                            centerCount++;
                        }
                    }
                }
            }
        }

        double borderMean = borderCount > 0 ? borderSum / borderCount : 0;
        double borderAbsMean = borderCount > 0 ? borderAbsSum / borderCount : 0;
        double centerMean = centerCount > 0 ? centerSum / centerCount : 0;
        double centerAbsMean = centerCount > 0 ? centerAbsSum / centerCount : 0;

        Console.WriteLine($"[TILE_BORDER_DIAG] Iteration {iteration} {phase}:");
        Console.WriteLine($"  Border pixels ({borderCount}): mean={borderMean:F2}, |mean|={borderAbsMean:F2}, min={borderMin:F2}, max={borderMax:F2}");
        Console.WriteLine($"  Center pixels ({centerCount}): mean={centerMean:F2}, |mean|={centerAbsMean:F2}, min={centerMin:F2}, max={centerMax:F2}");
        Console.WriteLine($"  Ratio (border/center): mean={borderMean/Math.Max(1, centerMean):F4}, |mean|={borderAbsMean/Math.Max(1, centerAbsMean):F4}");

        // Also sample specific tile boundaries to see discontinuities
        // Look at the boundary between tile (5,5) and tile (6,5)
        if (sampleTileX1 < numTilesX - 1 && sampleTileY1 < numTilesY)
        {
            int boundaryX = (sampleTileX1 + 1) * tileSize; // First column of tile (6,5)
            int midY = sampleTileY1 * tileSize + tileSize / 2;

            int idxLeft = (midY * rgbaWidth + boundaryX - 1) * channels;  // Last col of tile (5,5)
            int idxRight = (midY * rgbaWidth + boundaryX) * channels;      // First col of tile (6,5)

            if (idxLeft >= 0 && idxRight + 3 < rgbaData.Length)
            {
                float leftVal = rgbaData[idxLeft];
                float rightVal = rgbaData[idxRight];
                Console.WriteLine($"  Boundary sample at ({boundaryX-1},{midY})->({boundaryX},{midY}): left={leftVal:F2}, right={rightVal:F2}, diff={Math.Abs(rightVal-leftVal):F2}");
            }
        }
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
        Console.WriteLine($"[ExecuteBackwardFft] WorkGroupSize: 16x16, Expected workgroups: {Math.Ceiling(nTilesX/16.0)}x{Math.Ceiling(nTilesY/16.0)}");
        
        var freqParams = new FrequencyParams
        {
            TileSize = tileSize,
            NumTextures = numTextures
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), 
             BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });
        
        var cmd = _ctx.BeginSingleTimeCommands();
        
        // CRITICAL: Transition images to correct layout before dispatch
        inputFT.TransitionLayout(ImageLayout.General, cmd);
        outputSpatial.TransitionLayout(ImageLayout.General, cmd);
        
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, inputFT.View, ImageLayout.General, DescriptorType.SampledImage); // InputFT
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, outputSpatial.View, ImageLayout.General, DescriptorType.StorageImage); // Output

        _kernelBackwardFft!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelBackwardFft.PipelineLayout, 0, 1, &set, 0, null);
        
        // FIX: Dispatch expects THREAD COUNTS (nTilesX, nTilesY), not pre-calculated groups!
        // The Dispatch() function internally divides by WorkGroupSize (16×16) to calculate groups.
        // Was passing groupsX=16, groupsY=12 which got divided again → only 1×1 workgroups dispatched!
        Console.WriteLine($"[ExecuteBackwardFft] Dispatching {nTilesX}x{nTilesY} threads");
        _kernelBackwardFft.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }
    
    /// <summary>
    /// Runs FFT round-trip validation: Forward FFT → Backward FFT should return the original data.
    /// This is the most important test - if it fails, one of the FFT shaders is broken.
    /// </summary>
    /// <returns>List of validation results. If round-trip fails, subsequent tests help isolate the bug.</returns>
    private List<ValidationResult> RunFftRoundTripValidation(VulkanImage rgbaInput, int tileSize)
    {
        var results = new List<ValidationResult>();
        
        Console.WriteLine("\n=== FFT Round-Trip Validation ===\n");
        
        // 1. Capture original RGBA data
        float[] originalData = rgbaInput.GetData<float>();
        var originalStats = FftValidator.ComputeRgbaStats(originalData);
        
        Console.WriteLine($"[Validation] Original RGBA texture: {rgbaInput.Width}x{rgbaInput.Height}");
        Console.WriteLine($"[Validation] Original stats: sum={originalStats.TotalSum:G6}, energy={originalStats.TotalEnergy:G6}");
        
        // 2. Forward FFT
        int spatialWidth = (int)rgbaInput.Width;
        int spatialHeight = (int)rgbaInput.Height;
        int ftWidth = spatialWidth * 2; // Complex storage
        
        using var tempFT = new VulkanImage(_ctx, (uint)ftWidth, (uint)spatialHeight, Format.R32G32B32A32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
        
        Console.WriteLine($"[Validation] Running Forward FFT...");
        ExecuteForwardFft(rgbaInput, tempFT, tileSize, spatialWidth, spatialHeight);
        
        // 2b. Validate Forward FFT - check Parseval's theorem
        float[] ftData = tempFT.GetData<float>();
        var ftStats = FftValidator.ComputeFrequencyStats(ftData, spatialWidth, spatialHeight);
        
        Console.WriteLine($"[Validation] FFT output: width={ftWidth}, FT energy={ftStats.TotalEnergy:G6}");

        // Old validation (will fail due to window)
        var parsevalResultOld = FftValidator.ValidateParseval(
            originalStats.TotalEnergy,
            ftStats.TotalEnergy,
            tileSize,
            "Forward FFT");
        results.Add(parsevalResultOld);
        Console.WriteLine(parsevalResultOld);

        // New window-aware validation (CORRECT test)
        var parsevalResultWindowed = FftValidator.ValidateParsevalWithWindow(
            originalStats.TotalEnergy,
            ftStats.TotalEnergy,
            tileSize,
            "Forward FFT");
        results.Add(parsevalResultWindowed);
        Console.WriteLine(parsevalResultWindowed);

        // CRITICAL DIAGNOSTIC: Measure actual window factor
        double actualWindowFactor = WindowDiagnostics.MeasureActualWindowFactor(
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
        ExecuteBackwardFft(tempFT, roundTripOutput, 1, tileSize); // numTextures=1 for simple round-trip
        
        // 4. Compare original vs round-trip output
        float[] afterRoundTrip = roundTripOutput.GetData<float>();
        var afterStats = FftValidator.ComputeRgbaStats(afterRoundTrip);
        
        Console.WriteLine($"[Validation] After round-trip: sum={afterStats.TotalSum:G6}, energy={afterStats.TotalEnergy:G6}");
        
        // Calculate range for tolerance
        double range = 0;
        for (int i = 0; i < originalData.Length; i++)
        {
            if (Math.Abs(originalData[i]) > range) range = Math.Abs(originalData[i]);
        }
        
        var roundTripResult = FftValidator.ValidateRoundTrip(originalData, afterRoundTrip, range, "FFT Round-Trip");
        results.Add(roundTripResult);
        Console.WriteLine(roundTripResult);
        
        // 5. DC component check on backward FFT
        // The mean of the output should match the DC bin / normalization factor
        double dcBinEstimate = originalStats.TotalSum; // DC = sum of all input values for FFT
        int normFactor = tileSize * tileSize * 1; // 1 texture for round-trip
        
        var dcResult = FftValidator.ValidateDcComponent(
            dcBinEstimate, 
            afterStats.MeanPerChannel * 4 * afterStats.PixelCount, // Convert mean back to sum for comparison
            normFactor,
            "Backward FFT DC");
        results.Add(dcResult);
        Console.WriteLine(dcResult);
        
        // 6. Summary diagnosis
        Console.WriteLine("\n--- Validation Summary ---");
        if (!roundTripResult.Passed)
        {
            // Provide specific diagnosis based on metrics
            double outputRatio = afterStats.TotalSum / originalStats.TotalSum;
            Console.WriteLine($"[DIAGNOSIS] Output/Input ratio: {outputRatio:F4}");
            
            if (outputRatio < 0.2)
            {
                Console.WriteLine("[DIAGNOSIS] Output is <20% of input → Backward FFT is severely broken");
                Console.WriteLine("[DIAGNOSIS] This matches the 'dot matrix' pattern bug (only ~16% of expected values)");
            }
            else if (outputRatio > 5)
            {
                Console.WriteLine("[DIAGNOSIS] Output is >5x input → Normalization issue or Forward FFT bug");
            }
            
            if (parsevalResultWindowed.Passed)
            {
                Console.WriteLine("[DIAGNOSIS] Forward FFT passed Parseval's theorem → Bug is in backward_fft.hlsl");
            }
            else
            {
                Console.WriteLine("[DIAGNOSIS] Forward FFT failed Parseval's theorem → Bug may be in forward FFT too");
            }
        }
        else
        {
            Console.WriteLine("[DIAGNOSIS] Round-trip PASSED → FFT shaders are working correctly");
            Console.WriteLine("[DIAGNOSIS] If output is still wrong, the bug is in the merge/pipeline, not FFT");
        }
        Console.WriteLine();
        
        return results;
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
           _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, pb.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
           _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, input.View, ImageLayout.General, DescriptorType.SampledImage); // input RGBA
           _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.AlignedTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // unused
           _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RmsTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // unused
           _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.MismatchTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // unused
           _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.HighlightsTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // unused
           _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, output.View, ImageLayout.General, DescriptorType.StorageImage); // output FT
           Console.WriteLine($"║       ✓ Descriptors bound:");
           Console.WriteLine($"║         - Binding {ShaderBindings.FrequencyDomain.Params}: UniformBuffer (FrequencyParams)");
           Console.WriteLine($"║         - Binding {ShaderBindings.FrequencyDomain.RefTexture}: SampledImage (input RGBA)");
           Console.WriteLine($"║         - Bindings {ShaderBindings.FrequencyDomain.AlignedTexture}-{ShaderBindings.FrequencyDomain.HighlightsTexture}: SampledImage (dummy, unused)");
           Console.WriteLine($"║         - Binding {ShaderBindings.FrequencyDomain.OutputTexture}: StorageImage (output FT, double width)");

           Console.WriteLine($"║ [7/9] Binding pipeline...");
           _kernelForwardFft!.BindPipeline(cmd);
           _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelForwardFft.PipelineLayout, 0, 1, &set, 0, null);
           Console.WriteLine($"║       ✓ Pipeline and descriptor sets bound");

           // FIX: Dispatch expects THREAD COUNTS (nTilesX, nTilesY), not pre-calculated groups!
           // The Dispatch() function internally divides by WorkGroupSize (16×16) to calculate groups.
           uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
           uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);

           Console.WriteLine($"║ [8/9] Dispatching compute shader:");
           Console.WriteLine($"║       Thread count: {nTilesX}x{nTilesY} (one thread per tile)");
           Console.WriteLine($"║       Expected workgroups (after Dispatch divides): {groupsX}x{groupsY}");
           Console.WriteLine($"║       Workgroup size: 16x16x1 = 256 threads/group");

           Console.WriteLine($"║       >>> DISPATCHING NOW <<<");
           _kernelForwardFft.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
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
        _descriptors.UpdateBuffer(set, ShaderBindings.Conversion.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.BayerInput, bayerInput.View, ImageLayout.General, DescriptorType.SampledImage);   // Bayer input
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.UnusedSampled, dummyRgba.View, ImageLayout.General, DescriptorType.SampledImage);    // unused
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.UnusedStorage, dummyFloat.View, ImageLayout.General, DescriptorType.StorageImage);  // unused
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.RgbaOutput, rgbaOutput.View, ImageLayout.General, DescriptorType.StorageImage);  // RGBA output

        _kernelConvertToRgba!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelConvertToRgba.PipelineLayout, 0, 1, &set, 0, null);

        // FIX: Dispatch expects OUTPUT DIMENSIONS (pixels), not pre-calculated groups!
        // The Dispatch() function internally divides by WorkGroupSize to calculate groups.
        // Passing pre-calculated groups caused only 128/16 x 96/16 = 8x6 groups = 128x96 pixels
        Console.WriteLine($"[DEBUG] ExecuteConvertToRgba: Input={bayerInput.Width}x{bayerInput.Height}, Output={rgbaOutput.Width}x{rgbaOutput.Height}, CropX={cropX}, CropY={cropY}");
        _kernelConvertToRgba.Dispatch(cmd, rgbaOutput.Width, rgbaOutput.Height, 1);

        _ctx.EndSingleTimeCommands(cmd);

        // POST-SHADER VALIDATION
        {
            float[] outData = rgbaOutput.GetData<float>();
            double sumFirst = 0, sumMid = 0;
            int samples = Math.Min(1000, outData.Length);
            int midStart = outData.Length / 2;
            for (int i = 0; i < samples; i++) sumFirst += Math.Abs(outData[i]);
            for (int i = 0; i < samples && midStart + i < outData.Length; i++) sumMid += Math.Abs(outData[midStart + i]);
            Console.WriteLine($"[CONVERT_RGBA] POST-SHADER: first1000={sumFirst:F2}, mid1000={sumMid:F2}, total={outData.Length}");
            
            // Sample specific rows to find where data stops
            int texWidth = (int)rgbaOutput.Width;
            int texHeight = (int)rgbaOutput.Height;
            for (int row = 0; row < texHeight; row += texHeight / 8)  // Sample every 1/8th of the image
            {
                int rowStartFloat = row * texWidth * 4;  // 4 floats per RGBA pixel
                double rowSum = 0;
                int rowSamples = Math.Min(texWidth * 4, outData.Length - rowStartFloat);
                if (rowStartFloat >= 0 && rowStartFloat < outData.Length && rowSamples > 0)
                {
                    for (int i = 0; i < Math.Min(400, rowSamples); i++) 
                        rowSum += Math.Abs(outData[rowStartFloat + i]);
                    Console.WriteLine($"[CONVERT_RGBA] Row {row}: sum={rowSum:F2} (first 100 pixels)");
                }
            }
            
            if (sumFirst < 0.01 && sumMid < 0.01)
            {
                Console.WriteLine($"[CONVERT_RGBA] ❌ SHADER PRODUCED ALL ZEROS!");
            }
        }
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
        _descriptors.UpdateBuffer(set, ShaderBindings.Conversion.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.BayerInput, dummyFloat.View, ImageLayout.General, DescriptorType.SampledImage);   // unused
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.RgbaInput, rgbaInput.View, ImageLayout.General, DescriptorType.SampledImage);    // RGBA input
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.BayerOutput, bayerOutput.View, ImageLayout.General, DescriptorType.StorageImage); // Bayer output
        _descriptors.UpdateImage(set, ShaderBindings.Conversion.UnusedStorage2, dummyRgba.View, ImageLayout.General, DescriptorType.StorageImage);   // unused
        
        _kernelConvertToBayer!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelConvertToBayer.PipelineLayout, 0, 1, &set, 0, null);
        
        
        // FIX: Dispatch expects OUTPUT DIMENSIONS (pixels), not pre-calculated groups!
        // The Dispatch() function internally divides by WorkGroupSize to calculate groups.
        _kernelConvertToBayer.Dispatch(cmd, bayerOutput.Width, bayerOutput.Height, 1);
        
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
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        // calculate_rms_rgba shader bindings:
        // RefTexture = RGBA reference texture (reads pixel values)
        // OutputTexture = RMS output texture (writes squared values per tile)
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, rgbaInput.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, rmsOutput.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelRms!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelRms.PipelineLayout, 0, 1, &set, 0, null);

        // FIX: Dispatch one thread per tile - pass tile counts, not pre-calculated groups!
        Console.WriteLine($"[ExecuteCalculateRms] Input: {rgbaInput.Width}x{rgbaInput.Height}, Output: {rmsOutput.Width}x{rmsOutput.Height}, Dispatch: {nTilesX}x{nTilesY}");
        _kernelRms.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);

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
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        // deconvolute_frequency_domain shader bindings:
        // RefTexture = mismatch texture (reads per-tile mismatch values)
        // OutputTexture = final_texture_ft (read-write for in-place deconvolution)
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, mismatchTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, finalTextureFT.View, ImageLayout.General, DescriptorType.StorageImage);
        
        _kernelDeconvoluteFrequency!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelDeconvoluteFrequency.PipelineLayout, 0, 1, &set, 0, null);
        
        // FIX: Dispatch expects THREAD COUNTS (nTilesX, nTilesY for tile-based shader)
        // Deconvolution operates per-tile, so pass tile counts
        _kernelDeconvoluteFrequency.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }

    private void ExecuteReduceArtifacts(VulkanImage outputTexture, VulkanImage refTexture, int nTilesX, int nTilesY, int tileSize, int[] blackLevel)
    {
        EnsureMergeFrequencyPipeline();

        // NOTE: Border blending with RefTexture is currently DISABLED in the shader.
        // The RefTexture binding is kept for potential future re-enablement.
        // When border blending was enabled, it caused 8x8 grid artifacts because the formula
        // 0.5*(norm_cosine*refP + pixel_value) halves border values when norm_cosine ≈ 0.01
        // The current implementation only performs the clamp operation.

        // Create FrequencyParams with per-channel black levels
        // Swift passes individual black levels for proper per-channel clamping
        var freqParams = new FrequencyParams
        {
            TileSize = tileSize,
            BlackLevelMean = (blackLevel[0] + blackLevel[1] + blackLevel[2] + blackLevel[3]) / 4.0f,
            // Per-channel black levels for reduce_artifacts_tile_border
            BlackLevel0 = blackLevel[0],
            BlackLevel1 = blackLevel[1],
            BlackLevel2 = blackLevel[2],
            BlackLevel3 = blackLevel[3]
        };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(),
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });

        var cmd = _ctx.BeginSingleTimeCommands();
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, ShaderBindings.FrequencyDomain.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        // reduce_artifacts_tile_border uses:
        // RefTexture = ref_texture (read)
        // OutputTexture = out_texture (read_write)
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.RefTexture, refTexture.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.FrequencyDomain.OutputTexture, outputTexture.View, ImageLayout.General, DescriptorType.StorageImage);

        _kernelArtifactsTileBorder!.BindPipeline(cmd);
        _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelArtifactsTileBorder.PipelineLayout, 0, 1, &set, 0, null);

        // FIX: Dispatch by TILE COUNT to match Swift's tile-based model
        // Swift: threads_per_grid = MTLSize(width: tile_info.n_tiles_x, height: tile_info.n_tiles_y, depth: 1)
        // Each thread processes an entire tile via nested loops
        _kernelArtifactsTileBorder.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);
        _ctx.EndSingleTimeCommands(cmd);
    }
    private void ExecuteAvgPool(VulkanImage input, VulkanImage output, int scale, RawImage rawInfo, bool normalize = false)
    {
        EnsureAlignPipeline();

        // Compute color factors and black level for normalization (Swift: build_pyramid level 0)
        float factorRed = 1.0f, factorGreen = 1.0f, factorBlue = 1.0f;
        float blackLevelMean = 0.0f;
        if (normalize && rawInfo.ColorFactors != null && rawInfo.ColorFactors.Length >= 3)
        {
            // ColorFactors from camera WB: [R, G1, G2, B] or [R, G, B]
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
            // Black level: keep at 0 since prepare_texture_bayer already subtracts it
            // Applying it here would double-subtract and shift values negative
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

        _descriptors.UpdateBuffer(set, ShaderBindings.Alignment.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.InTexture, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.CompTexture, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // unused
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.AlignmentVectors, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage); // unused
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

    private void ExecuteAlignmentSearch(List<VulkanImage> refPyramid, List<VulkanImage> compPyramid, VulkanImage alignmentOut, TileInfo baseTileInfo, int scale, bool uniformExposure = true)
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

                // FIX: Dispatch expects tile counts, not pre-calculated groups!
                _kernelUpsampleAlignment.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);
                
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
                UniformExposure = uniformExposure ? 1 : 0,
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

            // FIX: Dispatch expects tile counts, not pre-calculated groups!
            _kernelCorrectUpsamplingError.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);
            
            // Barrier
            var barrier2 = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.ShaderReadBit };
            _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier2, 0, null, 0, null);
            
            // 3. Compute Tile Differences
            // Reads: Ref, Comp, Corrected(as Prev). Writes: TileDiff.
            // SearchDist = 2 -> nPos2D = 25.
            int nPos2D = 25;
            
            // 3D texture dimensions must match shader access pattern:
            // Shader writes: TileDiff[uint3(i, gid.x, gid.y)] where i=position index (0-24), gid.x=tile X, gid.y=tile Y
            // Shader reads:  InTileDiff.Load(int4(i, gid.x, gid.y, 0)) - same pattern
            // So: Width=nPos2D (25), Height=nTilesX, Depth=nTilesY
            var tileDiff = new VulkanImage(_ctx, (uint)nPos2D, (uint)nTilesX, (uint)nTilesY, Format.R32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit, ImageViewType.Type3D);
            levelDisposables.Add(tileDiff);
            tileDiff.TransitionLayout(ImageLayout.General, cmdBuffer);
            
            // Update params if needed (SearchDist)
            alignParams.SearchDist = 2;
            alignParams.WeightSSD = (level == 0) ? 0 : 1; // L1 norm at finest level, L2 at coarser (Swift: use_ssd = (i != 0))
            paramBuffer.SetData(new[] { alignParams }); // Update buffer content
            
            var setDiff = _descriptors.Allocate(_alignLayout);
            _descriptors.UpdateBuffer(setDiff, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
            _descriptors.UpdateImage(setDiff, 1, refLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setDiff, 2, compLayer.View, ImageLayout.General, DescriptorType.SampledImage);
            _descriptors.UpdateImage(setDiff, 3, corrected.View, ImageLayout.General, DescriptorType.SampledImage); // Use CORRECTED as prev
            _descriptors.UpdateImage(setDiff, 10, tileDiff.View, ImageLayout.General, DescriptorType.StorageImage);
            
            // Optimized kernel (25)
            // Use _kernelTileDiff25 or _kernelTileDiffExposure25
            var kernelDiff = uniformExposure ? _kernelTileDiff25! : _kernelTileDiffExposure25!;
            kernelDiff.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernelDiff.PipelineLayout, 0, 1, &setDiff, 0, null);

            // FIX: Dispatch expects tile counts, not pre-calculated groups!
            kernelDiff.Dispatch(cmdBuffer, (uint)nTilesX, (uint)nTilesY, 1);
            
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
    
    private void ExecuteWarp(VulkanImage altImage, VulkanImage output, VulkanImage alignment, TileInfo tileInfo, int padLeft = 0, int padTop = 0)
    {
        Console.WriteLine($"╔════════════════════════════════════════════════════════════════");
        Console.WriteLine($"║ [WARP DEBUG] ExecuteWarp CALLED");
        Console.WriteLine($"╠════════════════════════════════════════════════════════════════");
        Console.WriteLine($"║ [1/8] Input Configuration:");
        Console.WriteLine($"║       altImage (input):  {altImage.Width}x{altImage.Height} (format: {altImage.Format})");
        Console.WriteLine($"║       output:            {output.Width}x{output.Height} (format: {output.Format})");
        Console.WriteLine($"║       alignment:         {alignment.Width}x{alignment.Height} (format: {alignment.Format})");
        Console.WriteLine($"║       TileInfo: TileSize={tileInfo.TileSize}, NTilesX={tileInfo.NTilesX}, NTilesY={tileInfo.NTilesY}");
        Console.WriteLine($"║       Padding: padLeft={padLeft}, padTop={padTop}");

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
            int nTilesX = tileInfo.NTilesX;
            Console.WriteLine($"║       alignment data length: {alignData.Length} shorts ({alignData.Length/4} int4 values)");
            if (alignData.Length >= 8)
            {
                // Each int4 is 4 shorts: x, y, z, w
                Console.WriteLine($"║       First alignment vector: ({alignData[0]}, {alignData[1]}, {alignData[2]}, {alignData[3]})");
                int midIdx = (alignData.Length / 2) / 4 * 4; // Align to int4 boundary
                Console.WriteLine($"║       Mid alignment vector:   ({alignData[midIdx]}, {alignData[midIdx+1]}, {alignData[midIdx+2]}, {alignData[midIdx+3]})");

                // Check alignment at tiles covering the data region (padLeft=124, padTop=132 with HalfTileSize=16)
                // x_grid for pixel 124 = (124+0.5)/16 - 1 = 6.78 → tiles 6 and 7
                // y_grid for pixel 132 = (132+0.5)/16 - 1 = 7.28 → tiles 7 and 8
                int[] checkTiles = new[] { 6, 7, 8 };
                foreach (int tx in checkTiles)
                {
                    foreach (int ty in checkTiles)
                    {
                        int tileIdx = ty * nTilesX + tx;
                        int shortIdx = tileIdx * 4;
                        if (shortIdx + 3 < alignData.Length)
                        {
                            Console.WriteLine($"║       Alignment at tile ({tx},{ty}): ({alignData[shortIdx]}, {alignData[shortIdx+1]}, {alignData[shortIdx+2]}, {alignData[shortIdx+3]})");
                        }
                    }
                }
            }
            // Check if alignment has any non-zero values
            bool hasNonZero = false;
            for (int i = 0; i < Math.Min(alignData.Length, 1000) && !hasNonZero; i++)
                if (alignData[i] != 0) hasNonZero = true;
            Console.WriteLine($"║       Alignment has non-zero values: {hasNonZero}");
        }
        
        EnsureAlignPipeline();
         
        // For Bayer images (mosaic_pattern_width=2), DownscaleFactor = 2
        // Swift warp_texture passes: (downscale_factor==2 ? 1 : downscale_factor) * tile_size
        // So for Bayer: half_tile_size = 1 * tile_size = tile_size (NOT tile_size/2!)
        int downscaleFactor = 2; // Bayer
        int halfTileSizeForWarp = (downscaleFactor == 2 ? 1 : downscaleFactor) * tileInfo.TileSize;

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
            // Warp clamping params - clamp read coordinates to valid data region
            PadLeft = padLeft,
            PadTop = padTop,
            ImageWidth = (int)altImage.Width,
            ImageHeight = (int)altImage.Height
        };

        Console.WriteLine($"║ [4/8] AlignParams:");
        Console.WriteLine($"║       Scale={alignParams.Scale}, BlackLevel={alignParams.BlackLevel}");
        Console.WriteLine($"║       DownscaleFactor={alignParams.DownscaleFactor}");
        Console.WriteLine($"║       TileSize={alignParams.TileSize}, HalfTileSize={alignParams.HalfTileSize}");
        Console.WriteLine($"║       NumTilesX={alignParams.NumTilesX}, NumTilesY={alignParams.NumTilesY}");
        Console.WriteLine($"║       SearchDist={alignParams.SearchDist}, WeightSSD={alignParams.WeightSSD}");
        Console.WriteLine($"║       PadLeft={alignParams.PadLeft}, PadTop={alignParams.PadTop}");
        Console.WriteLine($"║       ImageWidth={alignParams.ImageWidth}, ImageHeight={alignParams.ImageHeight}");
        
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
        _descriptors.UpdateBuffer(set, ShaderBindings.Alignment.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.InTexture, altImage.View, ImageLayout.General, DescriptorType.SampledImage); // InTexture

        using var dummyComp = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyComp.TransitionLayout(ImageLayout.General, cmdBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.CompTexture, dummyComp.View, ImageLayout.General, DescriptorType.SampledImage); // unused

        _descriptors.UpdateImage(set, ShaderBindings.Alignment.AlignmentVectors, alignment.View, ImageLayout.General, DescriptorType.SampledImage); // alignment vectors
        _descriptors.UpdateImage(set, ShaderBindings.Alignment.Output, output.View, ImageLayout.General, DescriptorType.StorageImage); // output
        Console.WriteLine($"║       ✓ Descriptors bound:");
        Console.WriteLine($"║         - Binding {ShaderBindings.Alignment.Params}: UniformBuffer (AlignParams)");
        Console.WriteLine($"║         - Binding {ShaderBindings.Alignment.InTexture}: SampledImage (altImage/InTexture)");
        Console.WriteLine($"║         - Binding {ShaderBindings.Alignment.CompTexture}: SampledImage (dummy/Comp)");
        Console.WriteLine($"║         - Binding {ShaderBindings.Alignment.AlignmentVectors}: SampledImage (alignment/PrevAlignment)");
        Console.WriteLine($"║         - Binding {ShaderBindings.Alignment.Output}: StorageImage (output/OutTexture)");
        
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
    
    private void ExecutePrepare(VulkanImage input, VulkanImage output, RawImage rawInfo, int padLeft, int padTop, int exposureDiff = 0)
    {
        // CRITICAL: Fill output with zeros FIRST (Swift does this at texture.swift:638)
        // This ensures the padding region is zeros, which is important for alignment
        FillWithZeros(output);

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
             ExposureDiff = exposureDiff, // Exposure difference in centistops (ref - comp)
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
        // prepare_texture_bayer uses: InTextureUint (Binding 2), AuxTextureFloat (Binding 4), BlackLevels (Binding 6)
        _descriptors.UpdateBuffer(set, ShaderBindings.Prepare.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedFloat, dummyWeight.View, ImageLayout.General, DescriptorType.SampledImage); // unused
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.InputUint, input.View, ImageLayout.General, DescriptorType.SampledImage);        // InTextureUint
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedRgba, dummyRGBA.View, ImageLayout.General, DescriptorType.SampledImage);    // unused
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.HotPixelWeight, dummyWeight.View, ImageLayout.General, DescriptorType.SampledImage);  // AuxTextureFloat (hotpixel weight)
        _descriptors.UpdateBuffer(set, ShaderBindings.Prepare.MeanBuffer, meanBuffer.Handle, (ulong)sizeof(float), DescriptorType.StorageBuffer);       // MeanTextureBuffer (unused)
        _descriptors.UpdateBuffer(set, ShaderBindings.Prepare.BlackLevelsBuffer, blParams.Handle, (ulong)(4*sizeof(float)), DescriptorType.StorageBuffer);      // BlackLevels
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.OutputFloat, output.View, ImageLayout.General, DescriptorType.StorageImage);       // OutTextureFloat
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedOutputUint, dummyUint.View, ImageLayout.General, DescriptorType.StorageImage);   // unused
        _descriptors.UpdateImage(set, ShaderBindings.Prepare.UnusedOutputRgba, dummyRGBA.View, ImageLayout.General, DescriptorType.StorageImage);   // unused
        
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
        _descriptors.UpdateBuffer(set, ShaderBindings.Exposure.Params, paramBuffer.Handle, (ulong)Marshal.SizeOf<ExposureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, ShaderBindings.Exposure.InputTexture, image.View, ImageLayout.General, DescriptorType.SampledImage); // InTexture
        _descriptors.UpdateImage(set, ShaderBindings.Exposure.BlurredTexture, blurredTex != null ? blurredTex.View : image.View, ImageLayout.General, DescriptorType.SampledImage); // InBlurred
        _descriptors.UpdateBuffer(set, ShaderBindings.Exposure.BlackLevelsBuffer, blParams.Handle, (ulong)(4*sizeof(float)), DescriptorType.StorageBuffer); // BlackLevelsMean
        _descriptors.UpdateBuffer(set, ShaderBindings.Exposure.MaxBuffer, maxBuffer.Handle, 4, DescriptorType.StorageBuffer); // MaxTextureBuffer
        _descriptors.UpdateImage(set, ShaderBindings.Exposure.OutputTexture, image.View, ImageLayout.General, DescriptorType.StorageImage); // OutTexture (In/Out)
        _descriptors.UpdateBuffer(set, ShaderBindings.Exposure.OutputBuffer, dummyBuff.Handle, 4, DescriptorType.StorageBuffer); // unused
        
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
        _kernelAvgPoolNormalization?.Dispose();
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

