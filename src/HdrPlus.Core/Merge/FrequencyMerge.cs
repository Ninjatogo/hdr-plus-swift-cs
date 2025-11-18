using HdrPlus.Compute;
using HdrPlus.Core.Alignment;
using HdrPlus.Core.Utilities;

namespace HdrPlus.Core.Merge;

/// <summary>
/// Frequency-domain merging of images for HDR+ processing.
/// Performs the merging 4 times with slight displacement to suppress artifacts.
/// Currently only supports Bayer raw files.
/// Ported from burstphoto/merge/frequency.swift
/// </summary>
public class FrequencyMerge
{
    private readonly IComputeDevice _device;
    private readonly ImageAligner _aligner;

    public FrequencyMerge(IComputeDevice device)
    {
        _device = device;
        _aligner = new ImageAligner(device);
    }

    /// <summary>
    /// Convenience function for the frequency-based merging approach.
    /// Perform the merging 4 times with a slight displacement to suppress artifacts.
    /// </summary>
    public void AlignMergeFrequencyDomain(
        int refIdx,
        int mosaicPatternWidth,
        int searchDistance,
        int tileSize,
        double noiseReduction,
        bool uniformExposure,
        int[] exposureBias,
        int whiteLevel,
        int[][] blackLevel,
        double[][] colorFactors,
        IComputeTexture[] textures,
        IComputeTexture hotpixelWeightTexture,
        IComputeTexture finalTexture)
    {
        Console.WriteLine("Merging in the frequency domain...");

        // Fixed tile size of 8x8 for frequency domain merging
        // Smaller tile size reduces artifacts at specular highlights but slightly reduces
        // low-frequency noise suppression in shadows
        int tileSizeMerge = 8;

        // Exposure corrections for bracketed bursts
        double exposureCorr1 = 0.0;
        double exposureCorr2 = 0.0;

        for (int compIdx = 0; compIdx < exposureBias.Length; compIdx++)
        {
            double exposureFactor = Math.Pow(2.0, (exposureBias[compIdx] - exposureBias[refIdx]) / 100.0);
            exposureCorr1 += (0.5 + 0.5 / exposureFactor);
            exposureCorr2 += Math.Min(4.0, exposureFactor);
        }
        exposureCorr1 /= exposureBias.Length;
        exposureCorr2 /= exposureBias.Length;

        // Derive normalized robustness value
        double robustnessRev = 0.5 * ((uniformExposure ? 26.5 : 28.5) - Math.Round(noiseReduction));
        double robustnessNorm = exposureCorr1 / exposureCorr2 * Math.Pow(2.0, -robustnessRev + 7.5);

        // Estimate read noise
        double readNoise = Math.Pow(Math.Pow(2.0, -robustnessRev + 10.0), 1.6);

        // Maximum motion norm for adaptive denoising
        double maxMotionNorm = Math.Max(1.0, Math.Pow(1.3, 11.0 - robustnessRev));

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

        // Calculate padding
        int tileFactor = tileSizeArray[^1] * downscaleFactorArray.Aggregate(1, (a, b) => a * b);

        int padAlignX = (int)Math.Ceiling((float)(textureWidthOrig + tileSizeMerge) / tileFactor);
        padAlignX = (padAlignX * tileFactor - textureWidthOrig - tileSizeMerge) / 2;

        int padAlignY = (int)Math.Ceiling((float)(textureHeightOrig + tileSizeMerge) / tileFactor);
        padAlignY = (padAlignY * tileFactor - textureHeightOrig - tileSizeMerge) / 2;

        // Calculate padding for merging
        int cropMergeX = (int)Math.Floor((float)padAlignX / (2 * tileSizeMerge));
        cropMergeX = cropMergeX * 2 * tileSizeMerge;
        int cropMergeY = (int)Math.Floor((float)padAlignY / (2 * tileSizeMerge));
        cropMergeY = cropMergeY * 2 * tileSizeMerge;
        int padMergeX = padAlignX - cropMergeX;
        int padMergeY = padAlignY - cropMergeY;

        // Set tile information for merging
        var tileInfoMerge = new TileInfo(
            tileSize,
            tileSizeMerge,
            0,
            (textureWidthOrig + tileSizeMerge + 2 * padMergeX) / (2 * tileSizeMerge),
            (textureHeightOrig + tileSizeMerge + 2 * padMergeY) / (2 * tileSizeMerge),
            0,
            0);

        // Perform 4-pass merging with offset to suppress artifacts
        for (int i = 1; i <= 4; i++)
        {
            var startTime = DateTime.Now;

            // Set shift values
            int shiftLeft = (i % 2 == 0) ? tileSizeMerge : 0;
            int shiftRight = (i % 2 == 1) ? tileSizeMerge : 0;
            int shiftTop = (i < 3) ? tileSizeMerge : 0;
            int shiftBottom = (i >= 3) ? tileSizeMerge : 0;

            // Add shifts for artifact suppression
            int padLeft = padAlignX + shiftLeft;
            int padRight = padAlignX + shiftRight;
            int padTop = padAlignY + shiftTop;
            int padBottom = padAlignY + shiftBottom;

            // Prepare reference texture
            var refTexture = TextureUtilities.PrepareTexture(
                _device,
                textures[refIdx],
                hotpixelWeightTexture,
                padLeft, padRight, padTop, padBottom,
                0,
                blackLevel[refIdx],
                mosaicPatternWidth);

            // Convert to RGBA for SIMD operations
            var refTextureRgba = TextureUtilities.ConvertToRgba(
                _device,
                refTexture,
                cropMergeX,
                cropMergeY);

            double blackLevelMean = blackLevel[refIdx].Average();

            // Build reference pyramid
            var refPyramid = _aligner.BuildPyramid(
                refTexture,
                downscaleFactorArray.ToArray(),
                blackLevelMean,
                colorFactors[refIdx]);

            // Estimate noise level
            var rmsTexture = CalculateRmsRgba(refTextureRgba, tileInfoMerge);

            // Generate texture to accumulate total mismatch
            var totalMismatchTexture = TextureUtilities.TextureLike(_device, rmsTexture);
            TextureUtilities.FillWithZeros(_device, totalMismatchTexture);

            // Transform reference to frequency domain
            var refTextureFt = ForwardFt(refTextureRgba, tileInfoMerge);

            // Initialize final output in frequency domain
            var finalTextureFt = TextureUtilities.CopyTexture(_device, refTextureFt);

            // Iterate over comparison images
            for (int compIdx = 0; compIdx < textures.Length; compIdx++)
            {
                if (compIdx == refIdx)
                    continue;

                // Prepare comparison texture
                var compTexture = TextureUtilities.PrepareTexture(
                    _device,
                    textures[compIdx],
                    hotpixelWeightTexture,
                    padLeft, padRight, padTop, padBottom,
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

                var alignedTextureRgba = TextureUtilities.ConvertToRgba(
                    _device,
                    alignedTexture,
                    cropMergeX,
                    cropMergeY);

                // Calculate exposure factor
                double exposureFactor = Math.Pow(2.0, (exposureBias[compIdx] - exposureBias[refIdx]) / 100.0);

                // Calculate mismatch
                var mismatchTexture = CalculateMismatchRgba(
                    alignedTextureRgba,
                    refTextureRgba,
                    rmsTexture,
                    exposureFactor,
                    tileInfoMerge);

                // Normalize mismatch
                var mismatchCropped = TextureUtilities.CropTexture(
                    _device,
                    mismatchTexture,
                    shiftLeft / tileSizeMerge,
                    shiftRight / tileSizeMerge,
                    shiftTop / tileSizeMerge,
                    shiftBottom / tileSizeMerge);

                var meanMismatch = TextureUtilities.TextureMean(
                    _device,
                    mismatchCropped,
                    false,
                    mosaicPatternWidth);

                NormalizeMismatch(mismatchTexture, meanMismatch);

                // Add to total mismatch
                TextureUtilities.AddTexture(_device, mismatchTexture, totalMismatchTexture, textures.Length);

                // Calculate highlights norm
                var highlightsNormTexture = CalculateHighlightsNormRgba(
                    alignedTextureRgba,
                    exposureFactor,
                    tileInfoMerge,
                    whiteLevel == -1 ? 1000000 : whiteLevel,
                    blackLevelMean);

                // Transform to frequency domain
                var alignedTextureFt = ForwardFt(alignedTextureRgba, tileInfoMerge);

                // Adapt max motion norm for bracketed exposure
                double maxMotionNormExposure = uniformExposure
                    ? maxMotionNorm
                    : Math.Min(4.0, exposureFactor) * Math.Sqrt(maxMotionNorm);

                // Merge in frequency domain
                MergeFrequencyDomain(
                    refTextureFt,
                    alignedTextureFt,
                    finalTextureFt,
                    rmsTexture,
                    mismatchTexture,
                    highlightsNormTexture,
                    robustnessNorm,
                    readNoise,
                    maxMotionNormExposure,
                    uniformExposure,
                    tileInfoMerge);
            }

            // Apply deconvolution
            DeconvoluteFrequencyDomain(finalTextureFt, totalMismatchTexture, tileInfoMerge);

            // Transform back to image domain
            var outputTexture = BackwardFt(finalTextureFt, tileInfoMerge, textures.Length);

            // Reduce artifacts at tile borders
            ReduceArtifactsTileBorder(outputTexture, refTextureRgba, tileInfoMerge, blackLevel[refIdx]);

            // Convert back to Bayer and crop
            outputTexture = TextureUtilities.ConvertToBayer(_device, outputTexture);
            outputTexture = TextureUtilities.CropTexture(
                _device,
                outputTexture,
                padLeft - cropMergeX,
                padRight - cropMergeX,
                padTop - cropMergeY,
                padBottom - cropMergeY);

            // Add to final output
            TextureUtilities.AddTexture(_device, outputTexture, finalTexture, 1);

            Console.WriteLine($"Align+merge ({i}/4): {(DateTime.Now - startTime).TotalSeconds:F2}s");
        }
    }

    // Helper methods for frequency domain operations

    private IComputeTexture ForwardFt(IComputeTexture inTexture, TileInfo tileInfo)
    {
        var outTextureFt = _device.CreateTexture2D(
            inTexture.Width * 2,
            inTexture.Height,
            TextureFormat.RGBA32_Float);

        string pipelineName = tileInfo.TileSizeMerge <= 8 ? "forward_fft" : "forward_dft";
        var pipeline = _device.CreatePipeline(pipelineName);
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(outTextureFt, 1);
        cmd.SetConstant(tileInfo.TileSizeMerge, 0);
        cmd.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();

        return outTextureFt;
    }

    private IComputeTexture BackwardFt(IComputeTexture inTextureFt, TileInfo tileInfo, int numTextures)
    {
        var outTexture = _device.CreateTexture2D(
            inTextureFt.Width / 2,
            inTextureFt.Height,
            TextureFormat.RGBA32_Float);

        string pipelineName = tileInfo.TileSizeMerge <= 8 ? "backward_fft" : "backward_dft";
        var pipeline = _device.CreatePipeline(pipelineName);
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTextureFt, 0);
        cmd.SetTexture(outTexture, 1);
        cmd.SetConstant(tileInfo.TileSizeMerge, 0);
        cmd.SetConstant(numTextures, 1);
        cmd.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();

        return outTexture;
    }

    private IComputeTexture CalculateRmsRgba(IComputeTexture inTexture, TileInfo tileInfo)
    {
        var rmsTexture = _device.CreateTexture2D(
            tileInfo.NTilesX,
            tileInfo.NTilesY,
            TextureFormat.RGBA32_Float);

        var pipeline = _device.CreatePipeline("calculate_rms_rgba");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(rmsTexture, 1);
        cmd.SetConstant(tileInfo.TileSizeMerge, 0);
        cmd.DispatchThreads(rmsTexture.Width, rmsTexture.Height, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();

        return rmsTexture;
    }

    private IComputeTexture CalculateMismatchRgba(
        IComputeTexture alignedTexture,
        IComputeTexture refTexture,
        IComputeTexture rmsTexture,
        double exposureFactor,
        TileInfo tileInfo)
    {
        var mismatchTexture = _device.CreateTexture2D(
            tileInfo.NTilesX,
            tileInfo.NTilesY,
            TextureFormat.R32_Float);

        // Calculate absolute difference
        var absDiffTexture = _device.CreateTexture2D(
            refTexture.Width,
            refTexture.Height,
            refTexture.Format);

        var absDiffPipeline = _device.CreatePipeline("calculate_abs_diff_rgba");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(absDiffPipeline);
        cmd.SetTexture(refTexture, 0);
        cmd.SetTexture(alignedTexture, 1);
        cmd.SetTexture(absDiffTexture, 2);
        cmd.DispatchThreads(refTexture.Width, refTexture.Height, 1);
        cmd.EndCompute();

        // Calculate mismatch from absolute difference
        var mismatchPipeline = _device.CreatePipeline("calculate_mismatch_rgba");
        cmd.SetPipeline(mismatchPipeline);
        cmd.SetTexture(absDiffTexture, 0);
        cmd.SetTexture(rmsTexture, 1);
        cmd.SetTexture(mismatchTexture, 2);
        cmd.SetConstant(tileInfo.TileSizeMerge, 0);
        cmd.SetConstant((float)exposureFactor, 1);
        cmd.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();

        return mismatchTexture;
    }

    private IComputeTexture CalculateHighlightsNormRgba(
        IComputeTexture alignedTexture,
        double exposureFactor,
        TileInfo tileInfo,
        int whiteLevel,
        double blackLevelMean)
    {
        var highlightsNormTexture = _device.CreateTexture2D(
            tileInfo.NTilesX,
            tileInfo.NTilesY,
            TextureFormat.R32_Float);

        var pipeline = _device.CreatePipeline("calculate_highlights_norm_rgba");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(alignedTexture, 0);
        cmd.SetTexture(highlightsNormTexture, 1);
        cmd.SetConstant(tileInfo.TileSizeMerge, 0);
        cmd.SetConstant((float)exposureFactor, 1);
        cmd.SetConstant((float)whiteLevel, 2);
        cmd.SetConstant((float)blackLevelMean, 3);
        cmd.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();

        return highlightsNormTexture;
    }

    private void MergeFrequencyDomain(
        IComputeTexture refTextureFt,
        IComputeTexture alignedTextureFt,
        IComputeTexture outTextureFt,
        IComputeTexture rmsTexture,
        IComputeTexture mismatchTexture,
        IComputeTexture highlightsNormTexture,
        double robustnessNorm,
        double readNoise,
        double maxMotionNorm,
        bool uniformExposure,
        TileInfo tileInfo)
    {
        var pipeline = _device.CreatePipeline("merge_frequency_domain");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(refTextureFt, 0);
        cmd.SetTexture(alignedTextureFt, 1);
        cmd.SetTexture(outTextureFt, 2);
        cmd.SetTexture(rmsTexture, 3);
        cmd.SetTexture(mismatchTexture, 4);
        cmd.SetTexture(highlightsNormTexture, 5);
        cmd.SetConstant((float)robustnessNorm, 0);
        cmd.SetConstant((float)readNoise, 1);
        cmd.SetConstant((float)maxMotionNorm, 2);
        cmd.SetConstant(tileInfo.TileSizeMerge, 3);
        cmd.SetConstant(uniformExposure ? 1 : 0, 4);
        cmd.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();
    }

    private void NormalizeMismatch(IComputeTexture mismatchTexture, IComputeBuffer meanMismatchBuffer)
    {
        var pipeline = _device.CreatePipeline("normalize_mismatch");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(mismatchTexture, 0);
        cmd.SetBuffer(meanMismatchBuffer, 0);
        cmd.DispatchThreads(mismatchTexture.Width, mismatchTexture.Height, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();
    }

    private void DeconvoluteFrequencyDomain(
        IComputeTexture finalTextureFt,
        IComputeTexture totalMismatchTexture,
        TileInfo tileInfo)
    {
        var pipeline = _device.CreatePipeline("deconvolute_frequency_domain");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(finalTextureFt, 0);
        cmd.SetTexture(totalMismatchTexture, 1);
        cmd.SetConstant(tileInfo.TileSizeMerge, 0);
        cmd.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();
    }

    private void ReduceArtifactsTileBorder(
        IComputeTexture outTexture,
        IComputeTexture refTexture,
        TileInfo tileInfo,
        int[] blackLevel)
    {
        if (blackLevel[0] == -1)
            return;

        var pipeline = _device.CreatePipeline("reduce_artifacts_tile_border");
        var cmd = _device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(outTexture, 0);
        cmd.SetTexture(refTexture, 1);
        cmd.SetConstant(tileInfo.TileSizeMerge, 0);
        cmd.SetConstant(blackLevel[0], 1);
        cmd.SetConstant(blackLevel[1], 2);
        cmd.SetConstant(blackLevel[2], 3);
        cmd.SetConstant(blackLevel[3], 4);
        cmd.DispatchThreads(tileInfo.NTilesX, tileInfo.NTilesY, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();
    }
}
