using HdrPlus.Compute;
using HdrPlus.Core.Utilities;

namespace HdrPlus.Core.Exposure;

/// <summary>
/// Functions related to handling of exposure differences in bracketed bursts.
/// Applies tone mapping if the reference image is underexposed.
/// Inspired by https://www-old.cs.utah.edu/docs/techreports/2002/pdf/UUCS-02-001.pdf
/// Ported from burstphoto/exposure/exposure.swift
/// </summary>
public class ExposureCorrection
{
    private readonly IComputeDevice _device;

    public ExposureCorrection(IComputeDevice device)
    {
        _device = device;
    }

    /// <summary>
    /// Apply tone mapping if the reference image is underexposed.
    /// A curve is applied to lift the shadows and protect the highlights from burning.
    /// By lifting the shadows they suffer less from quantization errors,
    /// which is especially beneficial as the bit-depth of the image decreases.
    /// </summary>
    public void CorrectExposure(
        IComputeTexture finalTexture,
        int whiteLevel,
        int[][] blackLevel,
        string exposureControl,
        int[] exposureBias,
        bool uniformExposure,
        double[][] colorFactors,
        int refIdx,
        int mosaicPatternWidth)
    {
        // Only apply exposure correction if reference image has lower exposure than target
        if (exposureControl == "Off" || whiteLevel == -1 || blackLevel[0][0] == -1)
            return;

        var finalTextureBlurred = TextureUtilities.Blur(
            _device,
            finalTexture,
            mosaicPatternWidth: 2,
            kernelSize: 2);

        var maxTextureBuffer = TextureMax(finalTextureBlurred);

        // Find index of image with longest exposure for most robust black level
        int expIdx = 0;
        for (int compIdx = 0; compIdx < exposureBias.Length; compIdx++)
        {
            if (exposureBias[compIdx] > exposureBias[expIdx])
                expIdx = compIdx;
        }

        double[] blackLevelsMean;

        // Calculate mean black level
        if (uniformExposure)
        {
            blackLevelsMean = new double[blackLevel[expIdx].Length];
            for (int imgIdx = 0; imgIdx < blackLevel.Length; imgIdx++)
            {
                for (int channelIdx = 0; channelIdx < blackLevelsMean.Length; channelIdx++)
                {
                    blackLevelsMean[channelIdx] += blackLevel[imgIdx][channelIdx];
                }
            }

            for (int channelIdx = 0; channelIdx < blackLevelsMean.Length; channelIdx++)
            {
                blackLevelsMean[channelIdx] /= blackLevel.Length;
            }
        }
        else
        {
            blackLevelsMean = blackLevel[expIdx].Select(b => (double)b).ToArray();
        }

        double blackLevelMin = blackLevelsMean.Min();
        var blackLevelsMeanBuffer = _device.CreateBuffer(
            blackLevelsMean.Select(b => (float)b).ToArray());

        string pipelineName;
        var cmd = _device.CreateCommandBuffer();
        cmd.BeginCompute();

        if (exposureControl == "Curve0EV" || exposureControl == "Curve1EV")
        {
            pipelineName = "correct_exposure";

            double blackLevelMean = blackLevelsMean.Average();
            double colorFactorMean;
            int kernelSize;

            if (mosaicPatternWidth == 6)
            {
                colorFactorMean = (8.0 * colorFactors[refIdx][0] +
                                  20.0 * colorFactors[refIdx][1] +
                                   8.0 * colorFactors[refIdx][2]) / 36.0;
                kernelSize = 2;
            }
            else if (mosaicPatternWidth == 2)
            {
                colorFactorMean = (colorFactors[refIdx][0] +
                                  2.0 * colorFactors[refIdx][1] +
                                  colorFactors[refIdx][2]) / 4.0;
                kernelSize = 1;
            }
            else
            {
                colorFactorMean = (colorFactors[refIdx][0] +
                                  colorFactors[refIdx][1] +
                                  colorFactors[refIdx][2]) / 3.0;
                kernelSize = 1;
            }

            // Blur texture serves as approximation of local luminance
            finalTextureBlurred = TextureUtilities.Blur(
                _device,
                finalTexture,
                mosaicPatternWidth: 1,
                kernelSize);

            var pipeline = _device.CreatePipeline(pipelineName);
            cmd.SetPipeline(pipeline);
            cmd.SetTexture(finalTextureBlurred, 0);
            cmd.SetTexture(finalTexture, 1);
            cmd.SetConstant(exposureBias[refIdx], 0);
            cmd.SetConstant(exposureControl == "Curve0EV" ? 0 : 100, 1);
            cmd.SetConstant(mosaicPatternWidth, 2);
            cmd.SetConstant((float)whiteLevel, 3);
            cmd.SetConstant((float)colorFactorMean, 4);
            cmd.SetConstant((float)blackLevelMean, 5);
            cmd.SetConstant((float)blackLevelMin, 6);
            cmd.SetBuffer(blackLevelsMeanBuffer, 7);
            cmd.SetBuffer(maxTextureBuffer, 8);
        }
        else
        {
            pipelineName = "correct_exposure_linear";

            var pipeline = _device.CreatePipeline(pipelineName);
            cmd.SetPipeline(pipeline);
            cmd.SetTexture(finalTexture, 0);
            cmd.SetConstant((float)whiteLevel, 0);
            cmd.SetConstant(exposureControl == "LinearFullRange" ? -1.0f : 2.0f, 1);
            cmd.SetConstant(mosaicPatternWidth, 2);
            cmd.SetConstant((float)blackLevelMin, 3);
            cmd.SetBuffer(blackLevelsMeanBuffer, 4);
            cmd.SetBuffer(maxTextureBuffer, 5);
        }

        cmd.DispatchThreads(finalTexture.Width, finalTexture.Height, 1);
        cmd.EndCompute();

        _device.Submit(cmd);
        _device.WaitForCompletion();
    }

    /// <summary>
    /// Calculate the maximum value of the texture.
    /// Used for adjusting exposure of final image to prevent channels from being clipped.
    /// </summary>
    private IComputeBuffer TextureMax(IComputeTexture inTexture)
    {
        // Create 1D texture for maxima along X-axis
        var maxY = _device.CreateTexture2D(
            inTexture.Width,
            1,
            inTexture.Format);

        var cmd = _device.CreateCommandBuffer();
        cmd.BeginCompute();

        // Find max along Y-axis
        var maxYPipeline = _device.CreatePipeline("max_y");
        cmd.SetPipeline(maxYPipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(maxY, 1);
        cmd.DispatchThreads(inTexture.Width, 1, 1);

        // Find max along X-axis
        var maxBuffer = _device.CreateBuffer(new float[1]);
        var maxXPipeline = _device.CreatePipeline("max_x");
        cmd.SetPipeline(maxXPipeline);
        cmd.SetTexture(maxY, 0);
        cmd.SetBuffer(maxBuffer, 0);
        cmd.SetConstant(inTexture.Width, 1);
        cmd.DispatchThreads(inTexture.Width, 1, 1);

        cmd.EndCompute();
        _device.Submit(cmd);
        _device.WaitForCompletion();

        return maxBuffer;
    }
}
