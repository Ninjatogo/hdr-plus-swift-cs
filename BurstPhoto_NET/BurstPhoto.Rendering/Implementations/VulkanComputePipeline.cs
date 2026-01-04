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
    private ComputeKernel? _kernelBlurMosaic;
    private ComputeKernel? _kernelColorDiffSuperpixel;
    
    private DescriptorSetLayout _noiseEstLayout;
    
    private DescriptorSetLayout _alignLayout;
    private DescriptorSetLayout _mergeLayout;
    private DescriptorSetLayout _accumLayout;

    
    // Constants
    private const int TILE_SIZE_DEFAULT = 32; 

    public VulkanComputePipeline(VulkanContext ctx)
    {
        _ctx = ctx;
        _compiler = new VulkanShaderCompiler();
        _descriptors = new VulkanDescriptorManager(_ctx);
    }

    public async Task<RawImage> ProcessAsync(RenderingInput input, ProcessingOptions options, ProcessingProgress progress)
    {
        Console.WriteLine("[VulkanComputePipeline] Starting processing...");
        
        // 1. Compile Shaders
        // 1. Shaders compiled on demand
        // CompileShaders(); removed
        
        // 2. Setup Reference Frame
        var refImage = input.Images[input.ReferenceFrameIndex];
        int width = refImage.Width;
        int height = refImage.Height;
        
        // Calculate Padded Dimensions for Alignment
        // Padding usually TileSize/2 on all sides.
        int tileSize = ProcessingOptions.GetTileSizePixels(options.TileSize);
        int pad = tileSize / 2;
        int outWidth = width + tileSize; // roughly
        int outHeight = height + tileSize;
        
        // Ensure even dimensions
        if (outWidth % 2 != 0) outWidth++;
        if (outHeight % 2 != 0) outHeight++;
        
        Console.WriteLine($"[VulkanComputePipeline] Input: {width}x{height}, Padded: {outWidth}x{outHeight}");
        
        // 3. Allocate Resources
        using var rawTexture = new VulkanImage(_ctx, (uint)width, (uint)height, Format.R16Uint, 
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
            
        using var preparedTexture = new VulkanImage(_ctx, (uint)outWidth, (uint)outHeight, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
            
        // 4. Upload Reference Frame
        Console.WriteLine("[VulkanComputePipeline] Uploading Reference Frame...");
        // Marshal.Copy logic or unsafe copy in SetData?
        // SetData takes T[]. Data is ushort[].
        rawTexture.SetData(refImage.Data);
        
        // 5. Execute Prepare Pass
        Console.WriteLine("[VulkanComputePipeline] Executing Prepare Pass...");
        ExecutePrepare(rawTexture, preparedTexture, refImage, pad, pad);
        
        progress.ProgressInt += 50_000_000;
        
        // 6. Download Result
        // We download the float texture for verification.
        // In real pipeline, this stays on GPU for Alignment/Merge.
        Console.WriteLine("[VulkanComputePipeline] Downloading Result...");
        // float[] floatData = preparedTexture.GetData<float>(); // Old debug download
        
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
            
            Console.WriteLine($"[VulkanComputePipeline] Level {i}: {nextW}x{nextH}");
        }
        
        // Download Level 1 verified earlier. Now proceeding to Alignment Search loop.
        // float[] floatData = pyramid[1].GetData<float>();
        // outWidth = (int)pyramid[1].Width;
        // outHeight = (int)pyramid[1].Height;

        var disposables = new List<IDisposable>();
        
        // --- Single Alternate Image Alignment Test ---
        // For Step 2 verification, we will pretend the REFERENCE image is also an alternate,
        // or just load the 2nd image if available. 
        // Input has multiple images.
        // Let's loop through non-reference images.
        
        Console.WriteLine("[VulkanComputePipeline] Starting Alignment Search...");
        var tileInfo = TileInfo.Calculate(width, height, ProcessingOptions.GetTileSizePixels(options.TileSize), ProcessingOptions.GetSearchDistancePixels(options.SearchDistance));
        
        var alignments = new List<VulkanImage>();
        VulkanImage? finalResult = null; // To capture warped image
        
        // Initialize accumulators for merge
        var pixelAccum = new VulkanImage(_ctx, preparedTexture.Width, preparedTexture.Height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
        var weightAccum = new VulkanImage(_ctx, preparedTexture.Width, preparedTexture.Height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
        // Zero them out
        pixelAccum.SetData(new float[preparedTexture.Width * preparedTexture.Height]);
        weightAccum.SetData(new float[preparedTexture.Width * preparedTexture.Height]);
        disposables.Add(pixelAccum);
        disposables.Add(weightAccum);
        
        // Add reference frame to accumulator (with weight 1.0)
        // Also estimate noise from reference texture
        float estimatedNoiseSd;
        {
             float[] refData = preparedTexture.GetData<float>();
             float[] refPixAcc = new float[refData.Length];
             float[] refWAcc = new float[refData.Length];
             for (int k = 0; k < refData.Length; k++)
             {
                 refPixAcc[k] = refData[k];
                 refWAcc[k] = 1.0f;
             }
             pixelAccum.SetData(refPixAcc);
             weightAccum.SetData(refWAcc);
             
             // Estimate noise from reference texture (Swift: estimate_color_noise)
             estimatedNoiseSd = EstimateColorNoise(refData, outWidth, outHeight, refImage.MosaicPatternWidth);
        }

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
             using var preparedAlt = new VulkanImage(_ctx, (uint)width + (uint)tileSize, (uint)height + (uint)tileSize, Format.R32Sfloat,
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
             // Output Alignment Texture
             var alignment = new VulkanImage(_ctx, (uint)tileInfo.NTilesX, (uint)tileInfo.NTilesY, Format.R16G16B16A16Sint, 
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
                
             // Align Level 1 vs Level 1 (Single Pass Demo)
             ExecuteAlignmentSearch(pyramid, altPyramid, alignment, tileInfo, 2);
             
             alignments.Add(alignment);
             disposables.Add(alignment);
             
             // 5. Warp
             Console.WriteLine($"[VulkanComputePipeline] Warping Image {i}...");
             var warpedAlt = new VulkanImage(_ctx, preparedAlt.Width, preparedAlt.Height, Format.R32Sfloat,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
                
             ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo);
             
             disposables.Add(warpedAlt);
             finalResult = warpedAlt;
             
             // 6. Merge (Accumulate)
             Console.WriteLine($"[VulkanComputePipeline] Merging Image {i}...");
             ExecuteMerge(preparedTexture, warpedAlt, weightAccum, pixelAccum, refImage.WhiteLevel, 0.0f, options.NoiseReduction, estimatedNoiseSd);
             
             // Cleanup Alt Pyramid
             // preparedAlt is in 0. We dispose manually loop below.
             // We need to dispose logic.
             // preparedAlt was 'using' var. So it auto disposes at end of loop.
             // But we put it in altPyramid list.
             // If we double dispose?
             // VulkanImage check Handle!=0. Safe.
             foreach(var p in altPyramid) if(p!=preparedAlt) p.Dispose();
             // preparedAlt disposed by using.
        }

        // Cleanup pyramid levels later (dispose logic)
        for(int i=1; i<pyramid.Count; i++) disposables.Add(pyramid[i]);
        
        // 7. Convert back (Use merged accumulator)
        Console.WriteLine($"[VulkanComputePipeline] Downloading Merged Result...");
        
        // Normalize: result = pixelAccum / weightAccum
        float[] pixAcc = pixelAccum.GetData<float>();
        float[] wAcc = weightAccum.GetData<float>();
        float[] floatData = new float[pixAcc.Length];
        for (int i = 0; i < pixAcc.Length; i++)
        {
            floatData[i] = wAcc[i] > 0.0001f ? pixAcc[i] / wAcc[i] : pixAcc[i];
        }
        
        outWidth = (int)pixelAccum.Width;
        outHeight = (int)pixelAccum.Height;

        // Cleanup pyramid levels later (dispose logic)
        // For now, let's keep them scoped to method or add to a disposal list. 
        // We added 'using' for preparedTexture (Level 0).
        // The others need disposal.
        // Quick fix: Add to a disposable list.
        for(int i=1; i<pyramid.Count; i++) disposables.Add(pyramid[i]);
        
        // 7. Convert back to RawImage (Simple Quantization for verification output)
        // 7. Convert back to RawImage (Crop back to original size)
        Console.WriteLine("[VulkanComputePipeline] Converting to Output...");
        var outputImage = new RawImage
        {
            Width = width,
            Height = height,
            Data = new ushort[width * height],
            
            // Core metadata copy
            MosaicPatternWidth = refImage.MosaicPatternWidth,
            WhiteLevel = refImage.WhiteLevel,
            BlackLevel = refImage.BlackLevel,
            ExposureBias = refImage.ExposureBias,
            IsoExposureTime = refImage.IsoExposureTime,
            ColorFactors = refImage.ColorFactors,
            SourcePath = refImage.SourcePath,
            
            // DNG metadata copy
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
        
        // Crop back to original size (remove padding)
        // Original data starts at (pad, pad) in the padded floatData
        
        // Calculate factor for 16-bit scaling if enabled
        float factor16Bit = 1.0f;
        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            float maxVal = refImage.WhiteLevel;
            factor16Bit = (float)Math.Pow(2.0, 16.0 - Math.Ceiling(Math.Log2(maxVal)));
            Console.WriteLine($"[VulkanComputePipeline] Scaling to 16-bit (Factor: {factor16Bit:F2})");
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
        
        // Update WhiteLevel in output if scaled
        if (options.OutputBitDepth == OutputBitDepthOption.Bit16)
        {
            // Scale white level to reflect new range
            // e.g. 4095 * 16 = 65520
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
    private void ExecuteMerge(VulkanImage referenceFrame, VulkanImage warpedFrame, VulkanImage weightAccum, VulkanImage pixelAccum, float whiteLevel, float blackLevel, double noiseReduction, float noiseSd)
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

        // --- Pass 3a: GPU Weighted Pixel Accumulation (pixelAccum += warped * weight) ---
        // Create dummy param buffer for accumulation layout (b0 is required but not used)
        using var dummyParams = new VulkanBuffer(_ctx, 64, 
            BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        
        var setPixelAccum = _descriptors.Allocate(_accumLayout);
        _descriptors.UpdateBuffer(setPixelAccum, 0, dummyParams.Handle, 64, DescriptorType.UniformBuffer); // b0 dummy
        _descriptors.UpdateImage(setPixelAccum, 1, warpedFrame.View, ImageLayout.General, DescriptorType.SampledImage); // t0 InTextureFloat
        _descriptors.UpdateImage(setPixelAccum, 4, weightTex.View, ImageLayout.General, DescriptorType.SampledImage); // t3 AuxTextureFloat
        _descriptors.UpdateImage(setPixelAccum, 10, pixelAccum.View, ImageLayout.General, DescriptorType.StorageImage); // u10 OutTextureFloat
        
        _kernelAddWeighted!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddWeighted.PipelineLayout, 0, 1, &setPixelAccum, 0, null);
        _kernelAddWeighted.Dispatch(cmdBuffer, pixelAccum.Width, pixelAccum.Height, 1);
        
        // Barrier between pixel and weight accumulation
        _ctx.Vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);

        // --- Pass 3b: GPU Weight Accumulation (weightAccum += weight) ---
        var setWeightAccum = _descriptors.Allocate(_accumLayout);
        _descriptors.UpdateBuffer(setWeightAccum, 0, dummyParams.Handle, 64, DescriptorType.UniformBuffer); // b0 dummy
        _descriptors.UpdateImage(setWeightAccum, 1, dummyDiff.View, ImageLayout.General, DescriptorType.SampledImage); // t0 (unused)
        _descriptors.UpdateImage(setWeightAccum, 4, weightTex.View, ImageLayout.General, DescriptorType.SampledImage); // t3 AuxTextureFloat (weight)
        _descriptors.UpdateImage(setWeightAccum, 10, weightAccum.View, ImageLayout.General, DescriptorType.StorageImage); // u10 OutTextureFloat
        
        _kernelAddWeightOnly!.BindPipeline(cmdBuffer);
        _ctx.Vk.CmdBindDescriptorSets(cmdBuffer, PipelineBindPoint.Compute, _kernelAddWeightOnly.PipelineLayout, 0, 1, &setWeightAccum, 0, null);
        _kernelAddWeightOnly.Dispatch(cmdBuffer, weightAccum.Width, weightAccum.Height, 1);
        
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


    /// <summary>
    /// Calculates the robustness parameter using the Swift formula.
    /// Swift: let robustness = 0.12*pow(1.3, robustness_rev) - 0.4529822
    /// </summary>
    private float CalculateRobustness(double noiseReduction)
    {
        // Swift Logic from spatial.swift:
        // let robustness_rev = 0.5*(36.0-Double(Int(noise_reduction+0.5)))
        // let robustness = 0.12*pow(1.3, robustness_rev) - 0.4529822
        
        double robustnessRev = 0.5 * (36.0 - (int)(noiseReduction + 0.5));
        double robustnessVal = 0.12 * Math.Pow(1.3, robustnessRev) - 0.4529822;
        
        // The shader uses: max_diff = NoiseSd / Robustness
        // Higher robustness = smaller max_diff = stricter matching (less tolerance for differences)
        // Lower robustness = larger max_diff = more tolerance for differences
        
        return (float)robustnessVal;
    }

    /// <summary>
    /// Estimates color noise from the reference texture.
    /// This approximates Swift's estimate_color_noise which computes:
    /// 1. Blur the texture
    /// 2. Compute color difference between original and blurred
    /// 3. Calculate mean of the difference
    /// 
    /// Our simplified approach: compute average absolute difference between
    /// adjacent same-color pixels (Bayer neighbors at distance 2).
    /// </summary>
    private float EstimateColorNoise(float[] textureData, int width, int height, int mosaicPatternWidth)
    {
        if (textureData == null || textureData.Length == 0)
            return 100.0f; // Fallback value
        
        double sumDiff = 0;
        int count = 0;
        int step = mosaicPatternWidth; // Distance to same-color neighbor (2 for Bayer, 6 for X-Trans)
        
        // Sample a grid of pixels and compare with same-color neighbors
        // Use stride of step*2 for efficiency (don't need to check every pixel)
        for (int y = step; y < height - step; y += step * 2)
        {
            for (int x = step; x < width - step; x += step * 2)
            {
                int idx = y * width + x;
                float centerVal = textureData[idx];
                
                // Compare with 4 same-color neighbors at distance 'step'
                // Left neighbor
                if (x >= step)
                {
                    int leftIdx = idx - step;
                    sumDiff += Math.Abs(centerVal - textureData[leftIdx]);
                    count++;
                }
                
                // Right neighbor
                if (x + step < width)
                {
                    int rightIdx = idx + step;
                    sumDiff += Math.Abs(centerVal - textureData[rightIdx]);
                    count++;
                }
                
                // Top neighbor
                if (y >= step)
                {
                    int topIdx = idx - step * width;
                    sumDiff += Math.Abs(centerVal - textureData[topIdx]);
                    count++;
                }
                
                // Bottom neighbor
                if (y + step < height)
                {
                    int botIdx = idx + step * width;
                    sumDiff += Math.Abs(centerVal - textureData[botIdx]);
                    count++;
                }
            }
        }
        
        if (count == 0)
            return 100.0f;
        
        // The mean absolute difference is proportional to noise
        // Multiply by 4 to account for sum of 4 channels in superpixel (Bayer: RGGB)
        float meanDiff = (float)(sumDiff / count);
        float noiseSd = meanDiff * mosaicPatternWidth * mosaicPatternWidth;
        
        Console.WriteLine($"[VulkanComputePipeline] Noise Estimation: meanDiff={meanDiff:F2}, noiseSd={noiseSd:F2} (samples={count})");
        
        return Math.Max(noiseSd, 1.0f); // Ensure minimum noise level
    }

    public void Dispose()
    {
        _descriptors.Dispose();
        _ctx.Dispose();
    }
}
