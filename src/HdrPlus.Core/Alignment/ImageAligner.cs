using HdrPlus.Compute;

namespace HdrPlus.Core.Alignment;

/// <summary>
/// Performs tile-based image alignment using GPU compute.
/// Ported from align.swift
/// </summary>
public class ImageAligner
{
    private readonly IComputeDevice _device;

    public ImageAligner(IComputeDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>
    /// Aligns a comparison texture to a reference pyramid using coarse-to-fine alignment.
    /// Corresponds to align_texture() in align.swift:14
    /// </summary>
    public IComputeTexture AlignTexture(
        IComputeTexture[] refPyramid,
        IComputeTexture compTexture,
        int[] downscaleFactorArray,
        int[] tileSizeArray,
        int[] searchDistArray,
        bool uniformExposure,
        double blackLevelMean,
        double[] colorFactors)
    {
        // Initialize tile alignments (align.swift:16-24)
        var alignmentDesc = new
        {
            Width = 1,
            Height = 1,
            Format = TextureFormat.RG16_SInt
        };

        var prevAlignment = _device.CreateTexture2D(1, 1, TextureFormat.RG16_SInt, TextureUsage.ShaderReadWrite);
        prevAlignment.Label = $"{GetTextureName(compTexture)}: Current alignment Start";

        var currentAlignment = _device.CreateTexture2D(1, 1, TextureFormat.RG16_SInt, TextureUsage.ShaderReadWrite);
        var tileInfo = new TileInfo(0, 0, 0, 0, 0, 0, 0);

        // Build comparison pyramid (align.swift:27)
        var compPyramid = BuildPyramid(compTexture, downscaleFactorArray, blackLevelMean, colorFactors);

        // Align tiles at each pyramid level (align.swift:30-75)
        for (int i = downscaleFactorArray.Length - 1; i >= 0; i--)
        {
            // Load layer params (align.swift:32-36)
            int tileSize = tileSizeArray[i];
            int searchDist = searchDistArray[i];
            var refLayer = refPyramid[i];
            var compLayer = compPyramid[i];

            // Calculate the number of tiles (align.swift:38-42)
            int nTilesX = refLayer.Width / (tileSize / 2) - 1;
            int nTilesY = refLayer.Height / (tileSize / 2) - 1;
            int nPos1D = 2 * searchDist + 1;
            int nPos2D = nPos1D * nPos1D;

            tileInfo = new TileInfo(tileSize, 0, searchDist, nTilesX, nTilesY, nPos1D, nPos2D);

            // Get downscale factor from previous layer (align.swift:47-54)
            int downscaleFactor = (i < downscaleFactorArray.Length - 1)
                ? downscaleFactorArray[i + 1]
                : 0;

            // Upsample alignment vectors by a factor of 2 (align.swift:57-58)
            prevAlignment = Upsample(prevAlignment, nTilesX, nTilesY, UpsampleMode.NearestNeighbor);
            prevAlignment.Label = $"{GetTextureName(compTexture)}: Prev alignment {i}";

            // Correct upsampling error (align.swift:62)
            bool useSsd = (i != 0); // L2 norm for low-res, L1 for high-res
            prevAlignment = CorrectUpsamplingError(
                refLayer, compLayer, prevAlignment,
                downscaleFactor, uniformExposure, useSsd, tileInfo
            );

            // Compute tile differences (align.swift:68)
            var tileDiff = ComputeTileDiff(
                refLayer, compLayer, prevAlignment,
                downscaleFactor, uniformExposure, useSsd, tileInfo
            );

            // Create new current alignment texture (align.swift:70-71)
            currentAlignment = _device.CreateTexture2D(nTilesX, nTilesY, TextureFormat.RG16_SInt, TextureUsage.ShaderReadWrite);
            currentAlignment.Label = $"{GetTextureName(compTexture)}: Current alignment {i}";

            // Find best tile alignment (align.swift:74)
            FindBestTileAlignment(tileDiff, prevAlignment, currentAlignment, downscaleFactor, tileInfo);
        }

        // Warp the aligned layer (align.swift:78)
        var alignedTexture = WarpTexture(compTexture, currentAlignment, tileInfo, downscaleFactorArray[0]);

        return alignedTexture;
    }

    /// <summary>
    /// Builds a multi-scale pyramid by iteratively downsampling.
    /// Corresponds to build_pyramid() in align.swift:120
    /// </summary>
    private IComputeTexture[] BuildPyramid(
        IComputeTexture inputTexture,
        int[] downscaleFactorList,
        double blackLevelMean,
        double[] colorFactors)
    {
        var pyramid = new List<IComputeTexture>();

        for (int i = 0; i < downscaleFactorList.Length; i++)
        {
            int downscaleFactor = downscaleFactorList[i];

            if (i == 0)
            {
                // First level with optional normalization (align.swift:127)
                bool normalize = colorFactors[0] > 0;
                pyramid.Add(AvgPool(inputTexture, downscaleFactor, Math.Max(0.0, blackLevelMean), normalize, colorFactors));
            }
            else
            {
                // Subsequent levels: blur then downsample (align.swift:129)
                var blurred = Blur(pyramid[^1], patternWidth: 1, kernelSize: 2);
                pyramid.Add(AvgPool(blurred, downscaleFactor, 0.0, false, colorFactors));
            }
        }

        return pyramid.ToArray();
    }

    /// <summary>
    /// Average pooling with downscaling.
    /// Corresponds to avg_pool() in align.swift:84
    /// </summary>
    private IComputeTexture AvgPool(
        IComputeTexture inputTexture,
        int scale,
        double blackLevelMean,
        bool normalization,
        double[] colorFactors)
    {
        int outputWidth = inputTexture.Width / scale;
        int outputHeight = inputTexture.Height / scale;

        var outputTexture = _device.CreateTexture2D(outputWidth, outputHeight, TextureFormat.R16_Float, TextureUsage.ShaderReadWrite);
        outputTexture.Label = $"{GetTextureName(inputTexture)}: pool w/ scale {scale}";

        // Create and execute compute command
        var cmdBuffer = _device.CreateCommandBuffer();
        cmdBuffer.Label = "Avg Pool";
        cmdBuffer.BeginCompute(normalization ? "Average Pool Normalized" : "Average Pool");

        var pipeline = _device.CreatePipeline(
            normalization ? "avg_pool_normalization" : "avg_pool",
            normalization ? "avg_pool_normalization" : "avg_pool"
        );

        cmdBuffer.SetPipeline(pipeline);
        cmdBuffer.SetTexture(inputTexture, 0);
        cmdBuffer.SetTexture(outputTexture, 1);

        // TODO: Set buffer parameters (scale, black_level, color factors)

        cmdBuffer.DispatchThreads(outputWidth, outputHeight, 1);
        cmdBuffer.EndCompute();

        _device.Submit(cmdBuffer);
        _device.WaitIdle();

        return outputTexture;
    }

    /// <summary>
    /// Computes tile differences for alignment scoring.
    /// Corresponds to compute_tile_diff() in align.swift:136
    /// </summary>
    private IComputeTexture ComputeTileDiff(
        IComputeTexture refLayer,
        IComputeTexture compLayer,
        IComputeTexture prevAlignment,
        int downscaleFactor,
        bool uniformExposure,
        bool useSsd,
        TileInfo tileInfo)
    {
        // Create 3D texture for tile differences (align.swift:138-148)
        var tileDiff = _device.CreateTexture3D(
            tileInfo.NPos2D,
            tileInfo.NTilesX,
            tileInfo.NTilesY,
            TextureFormat.R32_Float,
            TextureUsage.ShaderReadWrite
        );
        tileDiff.Label = $"{GetTextureName(compLayer)}: Tile diff";

        // Dispatch compute shader (align.swift:150-170)
        var cmdBuffer = _device.CreateCommandBuffer();
        cmdBuffer.Label = "Compute Tile Diff";
        cmdBuffer.BeginCompute();

        // Choose optimized shader for 5×5 search or generic shader
        string shaderName = tileInfo.NPos2D == 25
            ? (uniformExposure ? "compute_tile_differences25" : "compute_tile_differences_exposure25")
            : "compute_tile_differences";

        var pipeline = _device.CreatePipeline(shaderName);
        cmdBuffer.SetPipeline(pipeline);

        cmdBuffer.SetTexture(refLayer, 0);
        cmdBuffer.SetTexture(compLayer, 1);
        cmdBuffer.SetTexture(prevAlignment, 2);
        cmdBuffer.SetTexture(tileDiff, 3);

        // TODO: Set buffer parameters

        int threadsZ = tileInfo.NPos2D == 25 ? 1 : tileInfo.NPos2D;
        cmdBuffer.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, threadsZ);

        cmdBuffer.EndCompute();
        _device.Submit(cmdBuffer);
        _device.WaitIdle();

        return tileDiff;
    }

    /// <summary>
    /// Corrects upsampling errors by testing 3 candidates.
    /// Corresponds to correct_upsampling_error() in align.swift:176
    /// </summary>
    private IComputeTexture CorrectUpsamplingError(
        IComputeTexture refLayer,
        IComputeTexture compLayer,
        IComputeTexture prevAlignment,
        int downscaleFactor,
        bool uniformExposure,
        bool useSsd,
        TileInfo tileInfo)
    {
        var corrected = _device.CreateTexture2D(
            prevAlignment.Width,
            prevAlignment.Height,
            TextureFormat.RG16_SInt,
            TextureUsage.ShaderReadWrite
        );
        corrected.Label = $"{GetTextureName(prevAlignment)}: Prev alignment upscaled corrected";

        var cmdBuffer = _device.CreateCommandBuffer();
        cmdBuffer.Label = "Correct Upsampling Error";
        cmdBuffer.BeginCompute();

        var pipeline = _device.CreatePipeline("correct_upsampling_error");
        cmdBuffer.SetPipeline(pipeline);

        cmdBuffer.SetTexture(refLayer, 0);
        cmdBuffer.SetTexture(compLayer, 1);
        cmdBuffer.SetTexture(prevAlignment, 2);
        cmdBuffer.SetTexture(corrected, 3);

        // TODO: Set buffer parameters

        cmdBuffer.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmdBuffer.EndCompute();

        _device.Submit(cmdBuffer);
        _device.WaitIdle();

        return corrected;
    }

    /// <summary>
    /// Finds the best alignment among candidates.
    /// Corresponds to find_best_tile_alignment() in align.swift:211
    /// </summary>
    private void FindBestTileAlignment(
        IComputeTexture tileDiff,
        IComputeTexture prevAlignment,
        IComputeTexture currentAlignment,
        int downscaleFactor,
        TileInfo tileInfo)
    {
        var cmdBuffer = _device.CreateCommandBuffer();
        cmdBuffer.Label = "Find Best Tile Alignment";
        cmdBuffer.BeginCompute();

        var pipeline = _device.CreatePipeline("find_best_tile_alignment");
        cmdBuffer.SetPipeline(pipeline);

        cmdBuffer.SetTexture(tileDiff, 0);
        cmdBuffer.SetTexture(prevAlignment, 1);
        cmdBuffer.SetTexture(currentAlignment, 2);

        // TODO: Set buffer parameters

        cmdBuffer.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmdBuffer.EndCompute();

        _device.Submit(cmdBuffer);
        _device.WaitIdle();
    }

    /// <summary>
    /// Warps a texture using alignment vectors.
    /// Corresponds to warp_texture() in align.swift:232
    /// </summary>
    private IComputeTexture WarpTexture(
        IComputeTexture textureToWarp,
        IComputeTexture alignment,
        TileInfo tileInfo,
        int downscaleFactor)
    {
        var warped = _device.CreateTexture2D(
            textureToWarp.Width,
            textureToWarp.Height,
            textureToWarp.Format,
            TextureUsage.ShaderReadWrite
        );
        warped.Label = $"{GetTextureName(textureToWarp)}: warped";

        var cmdBuffer = _device.CreateCommandBuffer();
        cmdBuffer.Label = "Warp Texture";
        cmdBuffer.BeginCompute(downscaleFactor == 2 ? "Warp Texture Bayer" : "Warp Texture XTrans");

        // Choose shader based on mosaic pattern
        string shaderName = downscaleFactor == 2 ? "warp_texture_bayer" : "warp_texture_xtrans";
        var pipeline = _device.CreatePipeline(shaderName);

        cmdBuffer.SetPipeline(pipeline);
        cmdBuffer.SetTexture(textureToWarp, 0);
        cmdBuffer.SetTexture(warped, 1);
        cmdBuffer.SetTexture(alignment, 2);

        // TODO: Set buffer parameters

        cmdBuffer.DispatchThreads(textureToWarp.Width, textureToWarp.Height, 1);
        cmdBuffer.EndCompute();

        _device.Submit(cmdBuffer);
        _device.WaitIdle();

        return warped;
    }

    // Placeholder methods (TODO: implement)
    private IComputeTexture Upsample(IComputeTexture texture, int width, int height, UpsampleMode mode)
    {
        // TODO: Implement upsampling
        return _device.CreateTexture2D(width, height, texture.Format, TextureUsage.ShaderReadWrite);
    }

    private IComputeTexture Blur(IComputeTexture texture, int patternWidth, int kernelSize)
    {
        // TODO: Implement blur
        return texture;
    }

    private string GetTextureName(IComputeTexture texture)
    {
        return texture.Label?.Split(':')[0] ?? "Texture";
    }
}

public enum UpsampleMode
{
    NearestNeighbor,
    Bilinear
}
