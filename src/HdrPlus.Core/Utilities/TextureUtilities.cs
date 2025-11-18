using HdrPlus.Compute;

namespace HdrPlus.Core.Utilities;

/// <summary>
/// Utility functions for texture manipulation and processing.
/// Ported from burstphoto/texture/texture.swift
/// </summary>
public static class TextureUtilities
{
    public enum UpsampleType
    {
        Bilinear,
        NearestNeighbour
    }

    /// <summary>
    /// Add one texture to another, averaging by the number of textures.
    /// </summary>
    public static void AddTexture(
        IComputeDevice device,
        IComputeTexture inTexture,
        IComputeTexture outTexture,
        int numTextures)
    {
        var pipeline = device.CreatePipeline("add_texture");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(outTexture, 1);
        cmd.SetConstant(numTextures, 0);
        cmd.DispatchThreads(inTexture.Width, inTexture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();
    }

    /// <summary>
    /// Calculate the weighted average of two textures using spatially varying weights.
    /// Larger weights bias towards texture1.
    /// </summary>
    public static IComputeTexture AddTextureWeighted(
        IComputeDevice device,
        IComputeTexture texture1,
        IComputeTexture texture2,
        IComputeTexture weightTexture)
    {
        var outTexture = device.CreateTexture2D(
            texture1.Width,
            texture1.Height,
            TextureFormat.R32_Float);

        var pipeline = device.CreatePipeline("add_texture_weighted");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(texture1, 0);
        cmd.SetTexture(texture2, 1);
        cmd.SetTexture(weightTexture, 2);
        cmd.SetTexture(outTexture, 3);
        cmd.DispatchThreads(texture1.Width, texture1.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outTexture;
    }

    /// <summary>
    /// Blur a texture using a binomial filter with specified kernel size.
    /// Respects mosaic pattern structure (e.g., Bayer pattern).
    /// </summary>
    public static IComputeTexture Blur(
        IComputeDevice device,
        IComputeTexture inTexture,
        int mosaicPatternWidth,
        int kernelSize)
    {
        var blurredX = device.CreateTexture2D(
            inTexture.Width,
            inTexture.Height,
            inTexture.Format);

        var blurredXY = device.CreateTexture2D(
            inTexture.Width,
            inTexture.Height,
            inTexture.Format);

        int kernelSizeMapped = kernelSize == 16 ? 16 : Math.Max(0, Math.Min(8, kernelSize));

        var pipeline = device.CreatePipeline("blur_mosaic_texture");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);

        // Blur along X-axis
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(blurredX, 1);
        cmd.SetConstant(kernelSizeMapped, 0);
        cmd.SetConstant(mosaicPatternWidth, 1);
        cmd.SetConstant(inTexture.Width, 2);
        cmd.SetConstant(0, 3); // direction: 0 = X
        cmd.DispatchThreads(inTexture.Width, inTexture.Height, 1);

        // Blur along Y-axis
        cmd.SetTexture(blurredX, 0);
        cmd.SetTexture(blurredXY, 1);
        cmd.SetConstant(kernelSizeMapped, 0);
        cmd.SetConstant(mosaicPatternWidth, 1);
        cmd.SetConstant(inTexture.Height, 2);
        cmd.SetConstant(1, 3); // direction: 1 = Y
        cmd.DispatchThreads(inTexture.Width, inTexture.Height, 1);

        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return blurredXY;
    }

    /// <summary>
    /// For each super-pixel, calculate the sum of absolute differences between each color channel.
    /// </summary>
    public static IComputeTexture ColorDifference(
        IComputeDevice device,
        IComputeTexture texture1,
        IComputeTexture texture2,
        int mosaicPatternWidth)
    {
        var outputTexture = device.CreateTexture2D(
            texture1.Width / mosaicPatternWidth,
            texture1.Height / mosaicPatternWidth,
            TextureFormat.R32_Float);

        var pipeline = device.CreatePipeline("color_difference");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(texture1, 0);
        cmd.SetTexture(texture2, 1);
        cmd.SetTexture(outputTexture, 2);
        cmd.SetConstant(mosaicPatternWidth, 0);
        cmd.DispatchThreads(texture1.Width / 2, texture1.Height / 2, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outputTexture;
    }

    /// <summary>
    /// Create a deep copy of the passed in texture.
    /// </summary>
    public static IComputeTexture CopyTexture(
        IComputeDevice device,
        IComputeTexture inTexture)
    {
        var outTexture = device.CreateTexture2D(
            inTexture.Width,
            inTexture.Height,
            inTexture.Format);

        var pipeline = device.CreatePipeline("copy_texture");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(outTexture, 1);
        cmd.DispatchThreads(inTexture.Width, inTexture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outTexture;
    }

    /// <summary>
    /// Crop texture by removing padding from all sides.
    /// </summary>
    public static IComputeTexture CropTexture(
        IComputeDevice device,
        IComputeTexture inTexture,
        int padLeft,
        int padRight,
        int padTop,
        int padBottom)
    {
        var outTexture = device.CreateTexture2D(
            inTexture.Width - padLeft - padRight,
            inTexture.Height - padTop - padBottom,
            inTexture.Format);

        var pipeline = device.CreatePipeline("crop_texture");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(outTexture, 1);
        cmd.SetConstant(padLeft, 0);
        cmd.SetConstant(padTop, 1);
        cmd.DispatchThreads(outTexture.Width, outTexture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outTexture;
    }

    /// <summary>
    /// Convert Bayer pattern texture to RGBA format (4 channels packed).
    /// </summary>
    public static IComputeTexture ConvertToRgba(
        IComputeDevice device,
        IComputeTexture inTexture,
        int cropX,
        int cropY)
    {
        var outTexture = device.CreateTexture2D(
            (inTexture.Width - 2 * cropX) / 2,
            (inTexture.Height - 2 * cropY) / 2,
            TextureFormat.RGBA32_Float);

        var pipeline = device.CreatePipeline("convert_to_rgba");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(outTexture, 1);
        cmd.SetConstant(cropX, 0);
        cmd.SetConstant(cropY, 1);
        cmd.DispatchThreads(outTexture.Width, outTexture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outTexture;
    }

    /// <summary>
    /// Convert RGBA format back to Bayer pattern texture.
    /// </summary>
    public static IComputeTexture ConvertToBayer(
        IComputeDevice device,
        IComputeTexture inTexture)
    {
        var outTexture = device.CreateTexture2D(
            inTexture.Width * 2,
            inTexture.Height * 2,
            TextureFormat.R32_Float);

        var pipeline = device.CreatePipeline("convert_to_bayer");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(outTexture, 1);
        cmd.DispatchThreads(outTexture.Width, outTexture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outTexture;
    }

    /// <summary>
    /// Initialize the texture with zeros.
    /// </summary>
    public static void FillWithZeros(
        IComputeDevice device,
        IComputeTexture texture)
    {
        var pipeline = device.CreatePipeline("fill_with_zeros");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(texture, 0);
        cmd.DispatchThreads(texture.Width, texture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();
    }

    /// <summary>
    /// Prepare texture for processing: convert to float, correct hot pixels,
    /// equalize exposure, and extend with padding.
    /// </summary>
    public static IComputeTexture PrepareTexture(
        IComputeDevice device,
        IComputeTexture inTexture,
        IComputeTexture hotpixelWeightTexture,
        int padLeft,
        int padRight,
        int padTop,
        int padBottom,
        int exposureDiff,
        int[] blackLevel,
        int mosaicPatternWidth)
    {
        var outTexture = device.CreateTexture2D(
            inTexture.Width + padLeft + padRight,
            inTexture.Height + padTop + padBottom,
            TextureFormat.R32_Float);

        FillWithZeros(device, outTexture);

        var blackLevelBuffer = device.CreateBuffer(
            blackLevel.Select(b => b == -1 ? 0f : (float)b).ToArray());

        var pipeline = device.CreatePipeline("prepare_texture_bayer");
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(hotpixelWeightTexture, 1);
        cmd.SetTexture(outTexture, 2);
        cmd.SetBuffer(blackLevelBuffer, 0);
        cmd.SetConstant(padLeft, 1);
        cmd.SetConstant(padTop, 2);
        cmd.SetConstant(exposureDiff, 3);
        cmd.DispatchThreads(inTexture.Width, inTexture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outTexture;
    }

    /// <summary>
    /// Create a new texture with the same properties as the input.
    /// </summary>
    public static IComputeTexture TextureLike(
        IComputeDevice device,
        IComputeTexture inTexture)
    {
        return device.CreateTexture2D(
            inTexture.Width,
            inTexture.Height,
            inTexture.Format);
    }

    /// <summary>
    /// Calculate the mean pixel value over a texture.
    /// If perSubPixel is true, calculates independent mean for each sub-pixel in the mosaic.
    /// </summary>
    public static IComputeBuffer TextureMean(
        IComputeDevice device,
        IComputeTexture inTexture,
        bool perSubPixel,
        int mosaicPatternWidth)
    {
        // Create intermediate texture for column sums
        var summedY = device.CreateTexture2D(
            inTexture.Width,
            mosaicPatternWidth,
            TextureFormat.R32_Float);

        var cmd = device.CreateCommandBuffer();
        cmd.BeginCompute();

        // Sum along columns
        var sumColumnsPipeline = device.CreatePipeline("sum_rect_columns_float");
        cmd.SetPipeline(sumColumnsPipeline);
        cmd.SetTexture(inTexture, 0);
        cmd.SetTexture(summedY, 1);
        cmd.SetConstant(0, 0); // top
        cmd.SetConstant(0, 1); // left
        cmd.SetConstant(inTexture.Height, 2); // bottom
        cmd.SetConstant(mosaicPatternWidth, 3);
        cmd.DispatchThreads(summedY.Width, summedY.Height, 1);

        // Sum along rows
        int bufferSize = mosaicPatternWidth * mosaicPatternWidth;
        var sumBuffer = device.CreateBuffer(new float[bufferSize]);

        var sumRowPipeline = device.CreatePipeline("sum_row");
        cmd.SetPipeline(sumRowPipeline);
        cmd.SetTexture(summedY, 0);
        cmd.SetBuffer(sumBuffer, 0);
        cmd.SetConstant(summedY.Width, 1);
        cmd.SetConstant(mosaicPatternWidth, 2);
        cmd.DispatchThreads(mosaicPatternWidth, mosaicPatternWidth, 1);

        // Calculate average
        string pipelineName = perSubPixel ? "divide_buffer" : "sum_divide_buffer";
        int outputSize = perSubPixel ? bufferSize : 1;
        var avgBuffer = device.CreateBuffer(new float[outputSize]);

        var avgPipeline = device.CreatePipeline(pipelineName);
        cmd.SetPipeline(avgPipeline);
        cmd.SetBuffer(sumBuffer, 0);
        cmd.SetBuffer(avgBuffer, 1);

        float numPixelsPerValue = (float)(inTexture.Width * inTexture.Height);
        if (perSubPixel)
            numPixelsPerValue /= (mosaicPatternWidth * mosaicPatternWidth);

        cmd.SetConstant(numPixelsPerValue, 2);
        cmd.SetConstant(bufferSize, 3);
        cmd.DispatchThreads(outputSize, 1, 1);

        cmd.EndCompute();
        device.Submit(cmd);
        device.WaitForCompletion();

        return avgBuffer;
    }

    /// <summary>
    /// Upsample texture to specified dimensions using bilinear or nearest neighbor interpolation.
    /// </summary>
    public static IComputeTexture Upsample(
        IComputeDevice device,
        IComputeTexture inputTexture,
        int toWidth,
        int toHeight,
        UpsampleType mode)
    {
        float scaleX = (float)toWidth / inputTexture.Width;
        float scaleY = (float)toHeight / inputTexture.Height;

        var outputTexture = device.CreateTexture2D(
            toWidth,
            toHeight,
            inputTexture.Format);

        string pipelineName = mode == UpsampleType.Bilinear
            ? "upsample_bilinear_float"
            : "upsample_nearest_int";

        var pipeline = device.CreatePipeline(pipelineName);
        var cmd = device.CreateCommandBuffer();

        cmd.BeginCompute();
        cmd.SetPipeline(pipeline);
        cmd.SetTexture(inputTexture, 0);
        cmd.SetTexture(outputTexture, 1);
        cmd.SetConstant(scaleX, 0);
        cmd.SetConstant(scaleY, 1);
        cmd.DispatchThreads(outputTexture.Width, outputTexture.Height, 1);
        cmd.EndCompute();

        device.Submit(cmd);
        device.WaitForCompletion();

        return outputTexture;
    }
}
