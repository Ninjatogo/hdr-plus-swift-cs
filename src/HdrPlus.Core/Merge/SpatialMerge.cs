using HdrPlus.Compute;
using HdrPlus.Core.Alignment;
using HdrPlus.Core.Utilities;

namespace HdrPlus.Core.Merge;

/// <summary>
/// Spatial-domain merging of images for HDR+ processing.
/// Ported from burstphoto/merge/spatial.swift
/// </summary>
public class SpatialMerge
{
    private readonly IComputeDevice _device;
    private readonly ImageAligner _aligner;

    public SpatialMerge(IComputeDevice device)
    {
        _device = device;
        _aligner = new ImageAligner(device);
    }

    /// <summary>
    /// Convenience function for the spatial merging approach.
    /// Supports non-Bayer raw files.
    /// </summary>
    public void AlignMergeSpatialDomain(
        int refIdx,
        int mosaicPatternWidth,
        int searchDistance,
        int tileSize,
        double noiseReduction,
        bool uniformExposure,
        int[] exposureBias,
        int[][] blackLevel,
        double[][] colorFactors,
        IComputeTexture[] textures,
        IComputeTexture hotpixelWeightTexture,
        IComputeTexture finalTexture)
    {
        Console.WriteLine("Merging in the spatial domain...");

        int kernelSize = 16; // kernel size of binomial filtering used for blurring

        // Derive normalized robustness value
        // Four steps in noise_reduction (-4.0) yield an increase by a factor of two in the robustness norm
        // The idea is that the sd of shot noise increases by a factor of sqrt(2) per ISO level
        double robustnessRev = 0.5 * (36.0 - Math.Round(noiseReduction));
        double robustness = 0.12 * Math.Pow(1.3, robustnessRev) - 0.4529822;

        // Set original texture size
        int textureWidthOrig = textures[refIdx].Width;
        int textureHeightOrig = textures[refIdx].Height;

        // Set alignment params
        int minImageDim = Math.Min(textureWidthOrig, textureHeightOrig);
        var downscaleFactorArray = new List<int> { mosaicPatternWidth };
        var searchDistArray = new List<int> { 2 };
        var tileSizeArray = new List<int> { tileSize };
        int res = minImageDim / downscaleFactorArray[0];

        // Generate pyramid parameters
        while (res > searchDistance)
        {
            downscaleFactorArray.Add(2);
            searchDistArray.Add(2);
            tileSizeArray.Add(Math.Max(tileSizeArray[^1] / 2, 8));
            res /= 2;
        }

        // Calculate padding for extension of the image frame
        int tileFactor = tileSizeArray[^1] * downscaleFactorArray.Aggregate(1, (a, b) => a * b);

        int padAlignX = (int)Math.Ceiling((float)textureWidthOrig / tileFactor);
        padAlignX = (padAlignX * tileFactor - textureWidthOrig) / 2;

        int padAlignY = (int)Math.Ceiling((float)textureHeightOrig / tileFactor);
        padAlignY = (padAlignY * tileFactor - textureHeightOrig) / 2;

        // Prepare reference texture
        var refTexture = TextureUtilities.PrepareTexture(
            _device,
            textures[refIdx],
            hotpixelWeightTexture,
            padAlignX, padAlignX,
            padAlignY, padAlignY,
            0,
            blackLevel[refIdx],
            mosaicPatternWidth);

        var refTextureCropped = TextureUtilities.CropTexture(
            _device,
            refTexture,
            padAlignX, padAlignX,
            padAlignY, padAlignY);

        double blackLevelMean = blackLevel[refIdx].Average();

        // Build reference pyramid
        var refPyramid = _aligner.BuildPyramid(
            refTexture,
            downscaleFactorArray.ToArray(),
            blackLevelMean,
            colorFactors[refIdx]);

        // Blur reference texture and estimate noise standard deviation
        var refTextureBlurred = TextureUtilities.Blur(
            _device,
            refTextureCropped,
            mosaicPatternWidth,
            kernelSize);

        var noiseSd = EstimateColorNoise(
            refTextureCropped,
            refTextureBlurred,
            mosaicPatternWidth);

        // Iterate over comparison images
        for (int compIdx = 0; compIdx < textures.Length; compIdx++)
        {
            // Add the reference texture to the output
            if (compIdx == refIdx)
            {
                TextureUtilities.AddTexture(
                    _device,
                    refTextureCropped,
                    finalTexture,
                    textures.Length);
                continue;
            }

            // Prepare comparison texture
            var compTexture = TextureUtilities.PrepareTexture(
                _device,
                textures[compIdx],
                hotpixelWeightTexture,
                padAlignX, padAlignX,
                padAlignY, padAlignY,
                exposureBias[refIdx] - exposureBias[compIdx],
                blackLevel[compIdx],
                mosaicPatternWidth);

            blackLevelMean = blackLevel[compIdx].Average();

            // Align comparison texture
            var alignedTexture = _aligner.AlignTexture(
                refPyramid,
                compTexture,
                downscaleFactorArray.ToArray(),
                tileSizeArray.ToArray(),
                searchDistArray.ToArray(),
                exposureBias[compIdx] == exposureBias[refIdx],
                blackLevelMean,
                colorFactors[compIdx]);

            var alignedTextureCropped = TextureUtilities.CropTexture(
                _device,
                alignedTexture,
                padAlignX, padAlignX,
                padAlignY, padAlignY);

            // Robust-merge the texture
            var mergedTexture = RobustMerge(
                refTextureCropped,
                refTextureBlurred,
                alignedTextureCropped,
                kernelSize,
                robustness,
                noiseSd,
                mosaicPatternWidth);

            // Add robust-merged texture to the output image
            TextureUtilities.AddTexture(
                _device,
                mergedTexture,
                finalTexture,
                textures.Length);
        }
    }

    /// <summary>
    /// Estimate noise standard deviation by comparing original and blurred textures.
    /// </summary>
    private IComputeBuffer EstimateColorNoise(
        IComputeTexture texture,
        IComputeTexture textureBlurred,
        int mosaicPatternWidth)
    {
        // Compute the color difference of each mosaic superpixel between original and blurred
        var textureDiff = TextureUtilities.ColorDifference(
            _device,
            texture,
            textureBlurred,
            mosaicPatternWidth);

        // Compute the average of the difference
        var meanDiff = TextureUtilities.TextureMean(
            _device,
            textureDiff,
            perSubPixel: false,
            mosaicPatternWidth);

        return meanDiff;
    }

    /// <summary>
    /// Robust merge of reference and comparison textures with motion detection.
    /// Uses adaptive weighting based on texture differences to detect and reject motion.
    /// </summary>
    private IComputeTexture RobustMerge(
        IComputeTexture refTexture,
        IComputeTexture refTextureBlurred,
        IComputeTexture compTexture,
        int kernelSize,
        double robustness,
        IComputeBuffer noiseSd,
        int mosaicPatternWidth)
    {
        // Blur comparison texture
        var compTextureBlurred = TextureUtilities.Blur(
            _device,
            compTexture,
            mosaicPatternWidth,
            kernelSize);

        // Compute the color difference of each superpixel between blurred textures
        var textureDiff = TextureUtilities.ColorDifference(
            _device,
            refTextureBlurred,
            compTextureBlurred,
            mosaicPatternWidth);

        // Create a weight texture
        var weightTexture = _device.CreateTexture2D(
            textureDiff.Width,
            textureDiff.Height,
            TextureFormat.R32_Float);

        // Compute merge weight
        var pipeline = _device.CreatePipeline("compute_merge_weight");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(textureDiff, 0);
        cmd.SetTexture(weightTexture, 1);
        cmd.SetBuffer(noiseSd, 0);
        cmd.SetConstant((float)robustness, 1);
        cmd.DispatchThreads(textureDiff.Width, textureDiff.Height, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();

        // Upsample merge weight to full image resolution
        var weightTextureUpsampled = TextureUtilities.Upsample(
            _device,
            weightTexture,
            refTexture.Width,
            refTexture.Height,
            TextureUtilities.UpsampleType.Bilinear);

        // Average the input textures based on the weight
        var mergedTexture = TextureUtilities.AddTextureWeighted(
            _device,
            refTexture,
            compTexture,
            weightTextureUpsampled);

        return mergedTexture;
    }
}
