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
        _descriptors = new VulkanDescriptorManager(_ctx);
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
        pyramid.Add(preparedTexture); // Level 0
        
        int currentW = (int)preparedTexture.Width;
        int currentH = (int)preparedTexture.Height;
        
        // Create 3 more levels (Scale 2 each time) -> 0, 1, 2, 3
        for (int i = 1; i < 4; i++)
        {
            int nextW = currentW / 2;
            int nextH = currentH / 2;
            
            // Ensure even?
            if (nextW % 2 != 0) nextW++;
            if (nextH % 2 != 0) nextH++;
            
            var levelImg = new VulkanImage(_ctx, (uint)nextW, (uint)nextH, Format.R32Sfloat, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                
            ExecuteAvgPool(pyramid[i-1], levelImg, 2, refImage);
            
            pyramid.Add(levelImg);
            currentW = nextW;
            currentH = nextH;
        }

        var disposables = new List<IDisposable>();
        
        Console.WriteLine("[VulkanComputePipeline] Starting Alignment Search...");
        var tileInfo = TileInfo.Calculate(width, height, ProcessingOptions.GetTileSizePixels(options.TileSize), ProcessingOptions.GetSearchDistancePixels(options.SearchDistance));
        
        // Accumulators
        VulkanImage? pixelAccum = null;
        VulkanImage? weightAccum = null;
        VulkanImage? pixelAccumFT = null;
        VulkanImage? refFT = null;
        
        float estimatedNoiseSd = 0;
        
        if (isFrequency)
        {
            EnsureMergeFrequencyPipeline();
            EnsureConversionPipeline();
            
            // CRITICAL: Swift hardcodes tile_size_merge = 8 for FFT merging
            const int tile_size_merge = 8;
            
            // RGBA dimensions are half of Bayer (2x2 superpixels -> 1 RGBA pixel)
            int rgbaWidth = outWidth / 2;
            int rgbaHeight = outHeight / 2;
            
            // FFT stores Real and Imaginary at adjacent X coordinates.
            // Real at x*2+0, Imaginary at x*2+1.
            // So FFT texture width must be 2x RGBA width.
            int ftWidth = rgbaWidth * 2;
            int ftHeight = rgbaHeight;
            
            Console.WriteLine($"[VulkanComputePipeline] FFT Dimensions: Bayer={outWidth}x{outHeight}, RGBA={rgbaWidth}x{rgbaHeight}, FT={ftWidth}x{ftHeight}");
            
            // Allocate RGBA texture for FFT processing
            var rgbaRefTexture = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit);
            disposables.Add(rgbaRefTexture);
            
            // Convert Reference Bayer -> RGBA
            Console.WriteLine("[VulkanComputePipeline] Converting Reference to RGBA...");
            ExecuteConvertToRgba(preparedTexture, rgbaRefTexture, refImage.CfaPattern);
            
            // DEBUG: Dump after RGBA conversion
            if (EnableDebugDump)
            {
                // Readback RGBA data to verify conversion worked
                float[] rgbaData = rgbaRefTexture.GetData<float>();
                float rgbaSum = 0;
                for (int i = 0; i < Math.Min(1000, rgbaData.Length); i++) rgbaSum += Math.Abs(rgbaData[i]);
                Console.WriteLine($"[DebugDump] step_1b_rgba - RGBA sum (first 1000): {rgbaSum:F2}, Total elements: {rgbaData.Length}");
                if (rgbaSum < 1.0f) Console.WriteLine("[DebugDump] WARNING: RGBA data appears to be zeros!");
            }
            
            // Allocate Complex Accumulator 
            pixelAccumFT = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit);
            disposables.Add(pixelAccumFT);
            
            // Clear Accumulator to 0
            pixelAccumFT.SetData(new float[ftWidth * ftHeight * 4]);
            
            // Create RefFT
            refFT = new VulkanImage(_ctx, (uint)ftWidth, (uint)ftHeight, Format.R32G32B32A32Sfloat, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit);
            disposables.Add(refFT);

            // Execute Forward FFT on Reference RGBA (rgbaRefTexture -> refFT)
            ExecuteForwardFft(rgbaRefTexture, refFT, tile_size_merge, rgbaWidth, rgbaHeight);
            
            // DEBUG: Dump after Forward FFT
            DebugDump(refFT, "step_2_fft_ref", refImage, rgbaWidth, rgbaHeight, 0);
            
            // Also accumulate Ref into PixelAccumFT
            // Copy RefFT -> PixelAccumFT
            ExecuteCopyImage(refFT, pixelAccumFT, ftWidth, ftHeight);
             
             // Estimation needs spatial Bayer texture.
             estimatedNoiseSd = ExecuteNoiseEstimationGPU(preparedTexture, refImage.MosaicPatternWidth);
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
        }
        
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
             
             // 3. Pyramid Alternate
             var altPyramid = new List<VulkanImage>();
             altPyramid.Add(preparedAlt);
             
             int currW = (int)preparedAlt.Width;
             int currH = (int)preparedAlt.Height;
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
             
             // 6. Merge
             Console.WriteLine($"[VulkanComputePipeline] Merging Image {i}...");
             float expDiff = (float)(refImage.ExposureBias - altImage.ExposureBias);
             
             if (isFrequency)
             {
                 // Note: we use 'preparedTexture' (Ref Spat) as refPyramid0 in params
                 ExecuteMergeFrequency(refFT!, preparedTexture, warpedAlt, null!, pixelAccumFT!, 
                    refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd, expDiff, tileSize, refImage.MosaicPatternWidth, 0);
             }
             else
             {
                 ExecuteMerge(preparedTexture, warpedAlt, weightAccum!, pixelAccum!, refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd, expDiff);
             }
             
             // Cleanup Alt Pyramid
             foreach(var p in altPyramid) if(p!=preparedAlt) p.Dispose();
             warpedAlt.Dispose(); // We can dispose warped after merge
        }

        // Cleanup pyramid levels
        for(int i=1; i<pyramid.Count; i++) disposables.Add(pyramid[i]);
        
        // DEBUG: Dump after all merges complete
        if (isFrequency)
        {
            DebugDump(pixelAccumFT!, "step_3_merge_accum_ft", refImage, outWidth, outHeight, pad);
        }
        else
        {
            DebugDump(pixelAccum!, "step_3_merge_accum_spatial", refImage, outWidth, outHeight, pad);
        }
        
        // 7. Convert back / Post Process
        Console.WriteLine($"[VulkanComputePipeline] Downloading Merged Result...");
        
        float[] floatData;
        
        if (isFrequency)
        {
            // CRITICAL: Must use tile_size_merge=8, same as forward FFT
            const int tile_size_merge = 8;
            
            // RGBA dimensions (half of Bayer)
            int rgbaWidth = outWidth / 2;
            int rgbaHeight = outHeight / 2;
            int nTilesX = rgbaWidth / tile_size_merge;
            int nTilesY = rgbaHeight / tile_size_merge;
            
            Console.WriteLine($"[VulkanComputePipeline] Post-FFT: RGBA={rgbaWidth}x{rgbaHeight}, Tiles={nTilesX}x{nTilesY}");
            
            // Step 1: Deconvolution (before backward FFT)
            // Apply simple deconvolution to slightly correct potential blurring from misalignment
            Console.WriteLine("[VulkanComputePipeline] Executing Deconvolution...");
            ExecuteDeconvoluteFrequency(pixelAccumFT!, nTilesX, nTilesY, tile_size_merge);
            
            // DEBUG: Dump after deconvolution
            DebugDump(pixelAccumFT!, "step_4_deconv_ft", refImage, rgbaWidth, rgbaHeight, 0);
            
            // Allocate RGBA output texture for backward FFT
            using var rgbaOutput = new VulkanImage(_ctx, (uint)rgbaWidth, (uint)rgbaHeight, Format.R32G32B32A32Sfloat, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
            
            // Step 2: Backward FFT: AccumFT -> RGBA output
            // Normalized by Input.Count (Ref + Alts)
            Console.WriteLine("[VulkanComputePipeline] Executing Backward FFT to RGBA...");
            ExecuteBackwardFft(pixelAccumFT!, rgbaOutput, input.Images.Count, tile_size_merge);
            
            // DEBUG: Dump after backward FFT (RGBA)
            if (EnableDebugDump)
            {
                // Readback RGBA output data to verify backward FFT worked
                float[] rgbaOutData = rgbaOutput.GetData<float>();
                float rgbaOutSum = 0;
                for (int i = 0; i < Math.Min(1000, rgbaOutData.Length); i++) rgbaOutSum += Math.Abs(rgbaOutData[i]);
                Console.WriteLine($"[DebugDump] step_5_back_fft_rgba - RGBA output sum (first 1000): {rgbaOutSum:F2}, Total elements: {rgbaOutData.Length}");
                if (rgbaOutSum < 1.0f) Console.WriteLine("[DebugDump] WARNING: Backward FFT RGBA output appears to be zeros!");
            }
            
            // Step 3: Convert RGBA back to Bayer
            Console.WriteLine("[VulkanComputePipeline] Converting RGBA to Bayer...");
            ExecuteConvertToBayer(rgbaOutput, preparedTexture, refImage.CfaPattern);
            
            // DEBUG: Dump after Bayer conversion
            DebugDump(preparedTexture, "step_5_back_fft", refImage, outWidth, outHeight, pad);
            
            // Step 4: Reduce artifacts at tile borders (in Bayer domain)
            Console.WriteLine("[VulkanComputePipeline] Reducing Tile Border Artifacts...");
            // Note: Swift uses ref_texture_rgba here, we use preparedTexture (reference spatial)
            // We need a copy of the reference for blending
            using var refCopy = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
            refCopy.SetData(pyramid[0].GetData<float>()); // Get reference from pyramid level 0
            // Note: tile grid for Bayer is 2x the RGBA tile grid
            int bayerTilesX = outWidth / (2 * tile_size_merge);
            int bayerTilesY = outHeight / (2 * tile_size_merge);
            ExecuteReduceArtifacts(preparedTexture, refCopy, bayerTilesX, bayerTilesY, tile_size_merge * 2, refImage.BlackLevel);
            
            // Download
            floatData = preparedTexture.GetData<float>();
        }
        else
        {
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
             preparedTexture.SetData(floatData);
             ExecuteExposureCorrection(preparedTexture, options.ExposureControl, refImage);
             floatData = preparedTexture.GetData<float>();
             
             // DEBUG: Dump after exposure correction
             DebugDump(preparedTexture, "step_6_exposure", refImage, outWidth, outHeight, pad);
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
        _prepareLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 5, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 11, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 12, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }
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
        
        _frequencyLayout = _descriptors.CreateLayout(new[] 
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, 
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t0
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t1
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t2 (Aux0)
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t3 (Aux1)
            new DescriptorSetLayoutBinding { Binding = 5, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }, // t4 (Aux2)
            new DescriptorSetLayoutBinding { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit } // u0
        });
        
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string shaderPath = Path.Combine(baseDir, "Shaders", "MergeFrequency.hlsl");
        string constantsPath = Path.Combine(baseDir, "Shaders", "Constants.hlsli");
        
        string source = File.ReadAllText(shaderPath);
        if (File.Exists(constantsPath))
        {
            string constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }
        
        // calculate_abs_diff_rgba
        string srcAbs = source.Replace("void calculate_abs_diff_rgba(", "void CSMain(");
        _kernelAbsDiff = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcAbs, "CSMain"), "CSMain", 16, 16, 1);
        
        // calculate_rms_rgba
        string srcRms = source.Replace("void calculate_rms_rgba(", "void CSMain(");
        _kernelRms = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcRms, "CSMain"), "CSMain", 16, 16, 1);
        
        // calculate_mismatch_rgba
        string srcMis = source.Replace("void calculate_mismatch_rgba(", "void CSMain(");
        _kernelMismatch = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcMis, "CSMain"), "CSMain", 16, 16, 1);
        
        // calculate_highlights_norm_rgba
        string srcHigh = source.Replace("void calculate_highlights_norm_rgba(", "void CSMain(");
        _kernelHighlightsNorm = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcHigh, "CSMain"), "CSMain", 16, 16, 1);
        
        // normalize_mismatch
        string srcNorm = source.Replace("void normalize_mismatch(", "void CSMain(");
        _kernelNormalizeMismatch = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcNorm, "CSMain"), "CSMain", 16, 16, 1);
        
        // reduce_artifacts_tile_border
        string srcArt = source.Replace("void reduce_artifacts_tile_border(", "void CSMain(");
        _kernelArtifactsTileBorder = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcArt, "CSMain"), "CSMain", 16, 16, 1);
        
        // forward_fft
        string srcFwd = source.Replace("void forward_fft(", "void CSMain(");
        _kernelForwardFft = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcFwd, "CSMain"), "CSMain", 16, 16, 1);
        
        // backward_fft
        string srcBwd = source.Replace("void backward_fft(", "void CSMain(");
        _kernelBackwardFft = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcBwd, "CSMain"), "CSMain", 16, 16, 1);
        
        // merge_frequency_domain
        string srcMerge = source.Replace("void merge_frequency_domain(", "void CSMain(");
        _kernelMergeFrequency = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcMerge, "CSMain"), "CSMain", 16, 16, 1);
        
        // deconvolute_frequency_domain
        string srcDeconv = source.Replace("void deconvolute_frequency_domain(", "void CSMain(");
        _kernelDeconvoluteFrequency = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcDeconv, "CSMain"), "CSMain", 16, 16, 1);
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
        DispatchTile(_kernelForwardFft!, texAlignedFT, aligned);
        
        // 8. Merge Frequency (per-tile dispatch)
        // u0=AccumFT, t0=RefFT, t1=AlignedFT, t2=RMS, t3=Mismatch, t4=Highlights
        DispatchTile(_kernelMergeFrequency!, pixelAccumFT, refFT, texAlignedFT, texRms, texMismatch, texHighlights);
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
       EnsureMergeFrequencyPipeline();
       
       // Calculate dispatch dimensions
       int nTilesX = width / tileSize;
       int nTilesY = height / tileSize;
       
       Console.WriteLine($"[ExecuteForwardFft] Input: {input.Width}x{input.Height}, Output: {output.Width}x{output.Height}");
       Console.WriteLine($"[ExecuteForwardFft] TileSize={tileSize}, Spatial={width}x{height}, Tiles={nTilesX}x{nTilesY}");
       
       var freqParams = new FrequencyParams { TileSize = tileSize };
       using var pb = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
       pb.SetData(new[] { freqParams });
       
       var cmd = _ctx.BeginSingleTimeCommands();
       
       // CRITICAL: Transition images to correct layout before dispatch
       input.TransitionLayout(ImageLayout.General, cmd);
       output.TransitionLayout(ImageLayout.General, cmd);
       
       var set = _descriptors.Allocate(_frequencyLayout);
       _descriptors.UpdateBuffer(set, 0, pb.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
       _descriptors.UpdateImage(set, 1, input.View, ImageLayout.General, DescriptorType.SampledImage); // t1 = RefTexture (input)
       _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage); // u10 = OutputTexture
       
       _kernelForwardFft!.BindPipeline(cmd);
       _ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelForwardFft.PipelineLayout, 0, 1, &set, 0, null);
       
       // Dispatch 1 thread per tile. Group size (16,16).
       // Threads needed: nTilesX, nTilesY.
       uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
       uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
       
       Console.WriteLine($"[ExecuteForwardFft] Dispatching {groupsX}x{groupsY} groups ({nTilesX}x{nTilesY} threads)");
       _kernelForwardFft.Dispatch(cmd, groupsX, groupsY, 1);
       _ctx.EndSingleTimeCommands(cmd);
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
    private void ExecuteConvertToRgba(VulkanImage bayerInput, VulkanImage rgbaOutput, int[] cfaPattern)
    {
        EnsureConversionPipeline();
        
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
        
        var texParams = new TextureParams { CfaPattern = cfaIndex };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<TextureParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit);
        paramBuffer.SetData(new[] { texParams });
        
        // Dummy textures for unused bindings
        using var dummyRgba = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        using var dummyFloat = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.StorageBit);
        
        var cmd = _ctx.BeginSingleTimeCommands();
        bayerInput.TransitionLayout(ImageLayout.General, cmd);
        rgbaOutput.TransitionLayout(ImageLayout.General, cmd);
        dummyRgba.TransitionLayout(ImageLayout.General, cmd);
        dummyFloat.TransitionLayout(ImageLayout.General, cmd);
        
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
    
    private void ExecuteDeconvoluteFrequency(VulkanImage finalTextureFT, int nTilesX, int nTilesY, int tileSize)
    {
        EnsureMergeFrequencyPipeline();
        
        // For deconvolution, we need the total_mismatch_texture which we don't have here
        // Swift creates this from accumulated mismatch textures. For now, use a placeholder.
        // TODO: Properly accumulate mismatch texture across all comparison frames
        using var mismatchPlaceholder = new VulkanImage(_ctx, (uint)nTilesX, (uint)nTilesY, Format.R32G32B32A32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
        // Initialize with low mismatch values (enables full deconvolution)
        float[] lowMismatch = new float[nTilesX * nTilesY * 4];
        Array.Fill(lowMismatch, 0.1f); // Low mismatch = enable deconvolution
        mismatchPlaceholder.SetData(lowMismatch);
        
        var freqParams = new FrequencyParams { TileSize = tileSize };
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { freqParams });
        
        var cmd = _ctx.BeginSingleTimeCommands();
        var set = _descriptors.Allocate(_frequencyLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
        // deconvolute_frequency_domain uses:
        // t0/t1 = final_texture_ft (read_write via u10)
        // t1 = total_mismatch_texture (read)
        _descriptors.UpdateImage(set, 1, finalTextureFT.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 2, mismatchPlaceholder.View, ImageLayout.General, DescriptorType.SampledImage);
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

    private void ExecuteAlignmentSearch(List<VulkanImage> refPyramid, List<VulkanImage> compPyramid, VulkanImage alignmentOut, TileInfo tileInfo, int scale)
    {
        EnsureAlignPipeline();
        
         var alignParams = new AlignParams
        {
            Scale = 1, // Not used for these kernels
            BlackLevel = 0.0f,
            FactorRed = 1.0f, FactorGreen = 1.0f, FactorBlue = 1.0f,
            
            DownscaleFactor = 1, // Assuming usually 1 or 2 depending on pass
            TileSize = tileInfo.TileSize, 
            SearchDist = tileInfo.SearchDist, 
            WeightSSD = 1, // WeightSSD usually high?
            
            HalfTileSize = tileInfo.TileSize / 2,
            NumTilesX = tileInfo.NTilesX,
            NumTilesY = tileInfo.NTilesY,
            UniformExposure = 0
        };
        
        int nPos2D = tileInfo.NPos2D;
        
        using var tileDiff = new VulkanImage(_ctx, (uint)tileInfo.NTilesX, (uint)tileInfo.NTilesY, (uint)nPos2D, Format.R32Sfloat, 
             ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit, ImageViewType.Type3D);
             
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { alignParams });     
        
        // prevAlignment usually from coarser level. For coarsest level (or test), use 0.
        using var dummyPrev = new VulkanImage(_ctx, (uint)tileInfo.NTilesX, (uint)tileInfo.NTilesY, Format.R16G16B16A16Sint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
        int totalTiles = tileInfo.NTilesX * tileInfo.NTilesY;
        short[] zeroAlign = new short[totalTiles * 4]; 
        dummyPrev.SetData(zeroAlign);

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
        refPyramid[0].TransitionLayout(ImageLayout.General, cmdBuffer); 
        compPyramid[0].TransitionLayout(ImageLayout.General, cmdBuffer);
        tileDiff.TransitionLayout(ImageLayout.General, cmdBuffer); 
        alignmentOut.TransitionLayout(ImageLayout.General, cmdBuffer);
        dummyPrev.TransitionLayout(ImageLayout.General, cmdBuffer);

        // --- Pass 1: compute_tile_differences ---
        var setDiff = _descriptors.Allocate(_alignLayout);
        _descriptors.UpdateBuffer(setDiff, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setDiff, 1, refPyramid[0].View, ImageLayout.General, DescriptorType.SampledImage); // Ref
        _descriptors.UpdateImage(setDiff, 2, compPyramid[0].View, ImageLayout.General, DescriptorType.SampledImage); // Comp
        _descriptors.UpdateImage(setDiff, 3, dummyPrev.View, ImageLayout.General, DescriptorType.SampledImage); // Prev
        _descriptors.UpdateImage(setDiff, 10, tileDiff.View, ImageLayout.General, DescriptorType.StorageImage); // TileDiff Out
        
        // Select kernel: use optimized 2D dispatch when n_pos_2d == 25 (search_dist=2)
        bool useOptimized = (nPos2D == 25);
        bool uniformExposure = (alignParams.UniformExposure != 0);
        
        if (useOptimized)
        {
            // Use optimized kernel: 2D dispatch over (n_tiles_x, n_tiles_y), each thread computes all 25 differences
            var kernel = uniformExposure ? _kernelTileDiff25! : _kernelTileDiffExposure25!;
            kernel.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &setDiff, 0, null);
            
            uint gX = (uint)Math.Ceiling(tileInfo.NTilesX / 16.0);
            uint gY = (uint)Math.Ceiling(tileInfo.NTilesY / 16.0);
            _ctx.Vk.CmdDispatch(cmdBuffer, gX, gY, 1);
            
            Console.WriteLine($"[VulkanComputePipeline] Using optimized kernel (n_pos_2d={nPos2D}, uniformExposure={uniformExposure})");
        }
        else
        {
            // Use generic kernel: 3D dispatch over (n_tiles_x, n_tiles_y, n_pos_2d)
            _kernelTileDiff!.BindPipeline(cmdBuffer);
            _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelTileDiff.PipelineLayout, 0, 1, &setDiff, 0, null);
            
            uint gX = (uint)Math.Ceiling(tileInfo.NTilesX / 8.0);
            uint gY = (uint)Math.Ceiling(tileInfo.NTilesY / 8.0);
            uint gZ = (uint)Math.Ceiling(nPos2D / 4.0);
            _ctx.Vk.CmdDispatch(cmdBuffer, gX, gY, gZ);
            
            Console.WriteLine($"[VulkanComputePipeline] Using generic kernel (n_pos_2d={nPos2D})");
        }
        
        // Barrier: TileDiff write -> read
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // --- Pass 2: find_best_tile_alignment ---
        var setFind = _descriptors.Allocate(_alignLayout);
        _descriptors.UpdateBuffer(setFind, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(setFind, 1, tileDiff.View, ImageLayout.General, DescriptorType.SampledImage); // InTileDiff
        
        using var dummyComp = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit); // Dummy
        dummyComp.TransitionLayout(ImageLayout.General, cmdBuffer);
        _descriptors.UpdateImage(setFind, 2, dummyComp.View, ImageLayout.General, DescriptorType.SampledImage);
        
        _descriptors.UpdateImage(setFind, 3, dummyPrev.View, ImageLayout.General, DescriptorType.SampledImage); // PrevAlignment
        _descriptors.UpdateImage(setFind, 10, alignmentOut.View, ImageLayout.General, DescriptorType.StorageImage); // OutAlignment
        
        _kernelFindBest!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelFindBest.PipelineLayout, 0, 1, &setFind, 0, null);
        
        _kernelFindBest.Dispatch(cmdBuffer, (uint)tileInfo.NTilesX, (uint)tileInfo.NTilesY, 1);
        
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
    
    private void ExecuteWarp(VulkanImage altImage, VulkanImage output, VulkanImage alignment, TileInfo tileInfo)
    {
         EnsureAlignPipeline();
         
         var alignParams = new AlignParams
        {
            Scale = 1,
            BlackLevel = 0.0f,
            FactorRed = 1.0f, FactorGreen = 1.0f, FactorBlue = 1.0f,
            DownscaleFactor = 1, // Correct? warp shader scales loaded alignment by DownscaleFactor.
            // Alignment computed on scale 2? 
            // If align was computed on Level 1 (scale 2), the vectors are for Level 1 pixels?
            // "DownscaleFactor * PrevAlignment.Load(...)"
            // If we computed alignment on Level 1, we set DownscaleFactor=2 when computing differences?
            // Let's assume DownscaleFactor=2 matches the level we used.
            // But wait, warp applies to FULL RES image logic?
            // warp input: Full resolution bayer.
            // Alignment: From level 1 (downscaled by 2).
            // So DownscaleFactor should be 2 to scale vectors up?
            
            // Re-check Align.hlsl:
            // warp_texture_bayer:
            // x_grid = ...
            // int4 prev_align0 = DownscaleFactor * PrevAlignment.Load(...)
            
            // Yes, DownscaleFactor scales the stored vector to current resolution.
            
            TileSize = tileInfo.TileSize, 
            SearchDist = 0, WeightSSD = 0,
            
            HalfTileSize = tileInfo.TileSize / 2,
            NumTilesX = tileInfo.NTilesX,
            NumTilesY = tileInfo.NTilesY,
            UniformExposure = 0
        };
        
        using var paramBuffer = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<AlignParams>(), 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        paramBuffer.SetData(new[] { alignParams });
        
        // Resources:
        // t0: InTexture (altImage)
        // t1: Comp (unused)
        // t2: PrevAlignment (alignment)
        // u10: OutTexture (output)

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

        var set = _descriptors.Allocate(_alignLayout);
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<AlignParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, altImage.View, ImageLayout.General, DescriptorType.SampledImage); // t0 In
        
        using var dummyComp = new VulkanImage(_ctx, 1, 1, Format.R32Sfloat, ImageUsageFlags.SampledBit);
        dummyComp.TransitionLayout(ImageLayout.General, cmdBuffer);
        _descriptors.UpdateImage(set, 2, dummyComp.View, ImageLayout.General, DescriptorType.SampledImage); // t1 (unused)
        
        _descriptors.UpdateImage(set, 3, alignment.View, ImageLayout.General, DescriptorType.SampledImage); // t2 PrevAlignment
        _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage); // u10 Out
        
        _kernelWarp!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelWarp.PipelineLayout, 0, 1, &set, 0, null);
        
        _kernelWarp.Dispatch(cmdBuffer, output.Width, output.Height, 1);
        
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
        _descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, (ulong)Marshal.SizeOf<TextureParams>(), DescriptorType.UniformBuffer);
        _descriptors.UpdateImage(set, 1, input.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 2, dummyRGBA.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateImage(set, 3, dummyWeight.View, ImageLayout.General, DescriptorType.SampledImage);
        _descriptors.UpdateBuffer(set, 4, meanBuffer.Handle, (ulong)sizeof(float), DescriptorType.StorageBuffer);
        _descriptors.UpdateBuffer(set, 5, blParams.Handle, (ulong)(4*sizeof(float)), DescriptorType.StorageBuffer);
        _descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage);
        _descriptors.UpdateImage(set, 11, dummyUint.View, ImageLayout.General, DescriptorType.StorageImage);
        _descriptors.UpdateImage(set, 12, dummyRGBA.View, ImageLayout.General, DescriptorType.StorageImage);
        
        // Bind Pipeline & Sets
        _kernelPrepareBayer!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelPrepareBayer.PipelineLayout, 0, 1, in set, 0, null);
        
        // Dispatch
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

