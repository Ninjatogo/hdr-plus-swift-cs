using BurstPhoto.Rendering;
using Silk.NET.Vulkan;

namespace BurstPhoto.Tests.TestHelpers;

/// <summary>
/// Factory for creating VulkanImage textures from test patterns.
/// Handles GPU memory allocation and data upload.
/// </summary>
public class TestTextureFactory : IDisposable
{
    private readonly VulkanContext _ctx;
    private readonly List<VulkanImage> _trackedImages = [];

    public TestTextureFactory(VulkanContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Creates a single-channel VulkanImage filled with the specified pattern.
    /// The image is tracked and will be disposed when the factory is disposed.
    /// </summary>
    /// <param name="width">Texture width in pixels</param>
    /// <param name="height">Texture height in pixels</param>
    /// <param name="pattern">The test pattern to generate</param>
    /// <param name="format">Vulkan format (default R32Sfloat)</param>
    /// <param name="minValue">Minimum value in the pattern</param>
    /// <param name="maxValue">Maximum value in the pattern</param>
    /// <param name="seed">Random seed for WhiteNoise pattern</param>
    /// <param name="track">If true, dispose with factory; if false, caller must dispose</param>
    /// <returns>VulkanImage with pattern data uploaded</returns>
    public VulkanImage CreateTexture(
        int width,
        int height,
        TestPattern pattern,
        Format format = Format.R32Sfloat,
        float minValue = 0f,
        float maxValue = 65535f,
        int seed = 42,
        bool track = true)
    {
        var data = TestPatternGenerator.GenerateSingleChannel(width, height, pattern, minValue, maxValue, seed);
        return CreateTextureFromData(data, width, height, format, track);
    }

    /// <summary>
    /// Creates a 4-channel RGBA VulkanImage filled with the specified pattern.
    /// </summary>
    public VulkanImage CreateRgbaTexture(
        int width,
        int height,
        TestPattern pattern,
        float minValue = 0f,
        float maxValue = 65535f,
        int seed = 42,
        bool track = true)
    {
        var data = TestPatternGenerator.GenerateRgba(width, height, pattern, minValue, maxValue, seed);
        return CreateTextureFromData(data, width, height, Format.R32G32B32A32Sfloat, track);
    }

    /// <summary>
    /// Creates a synthetic Bayer texture with specific channel values.
    /// Useful for testing demosaicing accuracy with known inputs.
    /// </summary>
    public VulkanImage CreateSyntheticBayer(
        int width,
        int height,
        float redValue,
        float green1Value,
        float green2Value,
        float blueValue,
        bool track = true)
    {
        var data = TestPatternGenerator.GenerateSyntheticBayer(width, height, redValue, green1Value, green2Value, blueValue);
        return CreateTextureFromData(data, width, height, Format.R32Sfloat, track);
    }

    /// <summary>
    /// Creates a VulkanImage that is a shifted copy of the source texture data.
    /// Useful for testing alignment detection with known shifts.
    /// </summary>
    /// <param name="sourceData">Source texture data</param>
    /// <param name="width">Texture width</param>
    /// <param name="height">Texture height</param>
    /// <param name="dx">Horizontal shift (positive = right)</param>
    /// <param name="dy">Vertical shift (positive = down)</param>
    /// <param name="format">Vulkan format</param>
    /// <param name="track">If true, dispose with factory</param>
    public VulkanImage CreateShiftedTexture(
        float[] sourceData,
        int width,
        int height,
        int dx,
        int dy,
        Format format = Format.R32Sfloat,
        bool track = true)
    {
        var shiftedData = TestPatternGenerator.CreateShiftedCopy(sourceData, width, height, dx, dy);
        return CreateTextureFromData(shiftedData, width, height, format, track);
    }

    /// <summary>
    /// Creates a VulkanImage that is a shifted copy of the source RGBA texture data.
    /// </summary>
    public VulkanImage CreateShiftedRgbaTexture(
        float[] sourceData,
        int width,
        int height,
        int dx,
        int dy,
        bool track = true)
    {
        var shiftedData = TestPatternGenerator.CreateShiftedCopyRgba(sourceData, width, height, dx, dy);
        return CreateTextureFromData(shiftedData, width, height, Format.R32G32B32A32Sfloat, track);
    }

    /// <summary>
    /// Creates an empty (uninitialized) VulkanImage for use as output texture.
    /// </summary>
    public VulkanImage CreateEmptyTexture(
        int width,
        int height,
        Format format = Format.R32Sfloat,
        bool track = true)
    {
        var image = new VulkanImage(
            _ctx,
            (uint)width,
            (uint)height,
            format,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

        // Transition to General layout for compute shader access
        image.TransitionLayout(ImageLayout.General);

        if (track)
            _trackedImages.Add(image);

        return image;
    }

    /// <summary>
    /// Creates an empty RGBA VulkanImage for use as output texture.
    /// </summary>
    public VulkanImage CreateEmptyRgbaTexture(int width, int height, bool track = true)
    {
        return CreateEmptyTexture(width, height, Format.R32G32B32A32Sfloat, track);
    }

    /// <summary>
    /// Creates a VulkanImage from raw float data.
    /// </summary>
    public VulkanImage CreateTextureFromData(
        float[] data,
        int width,
        int height,
        Format format,
        bool track = true)
    {
        var image = new VulkanImage(
            _ctx,
            (uint)width,
            (uint)height,
            format,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

        image.SetData(data);

        if (track)
            _trackedImages.Add(image);

        return image;
    }

    /// <summary>
    /// Creates a zero-filled texture.
    /// </summary>
    public VulkanImage CreateZeroTexture(
        int width,
        int height,
        Format format = Format.R32Sfloat,
        bool track = true)
    {
        var elementCount = format switch
        {
            Format.R32Sfloat => width * height,
            Format.R32G32Sfloat => width * height * 2,
            Format.R32G32B32Sfloat => width * height * 3,
            Format.R32G32B32A32Sfloat => width * height * 4,
            _ => throw new NotSupportedException($"Format {format} not supported for zero texture")
        };

        var data = new float[elementCount];
        return CreateTextureFromData(data, width, height, format, track);
    }

    /// <summary>
    /// Creates a texture filled with a constant value.
    /// </summary>
    public VulkanImage CreateConstantTexture(
        int width,
        int height,
        float value,
        Format format = Format.R32Sfloat,
        bool track = true)
    {
        var elementCount = format switch
        {
            Format.R32Sfloat => width * height,
            Format.R32G32Sfloat => width * height * 2,
            Format.R32G32B32Sfloat => width * height * 3,
            Format.R32G32B32A32Sfloat => width * height * 4,
            _ => throw new NotSupportedException($"Format {format} not supported for constant texture")
        };

        var data = new float[elementCount];
        Array.Fill(data, value);
        return CreateTextureFromData(data, width, height, format, track);
    }

    /// <summary>
    /// Stops tracking a specific image (for manual lifetime management).
    /// </summary>
    public void Untrack(VulkanImage image)
    {
        _trackedImages.Remove(image);
    }

    public void Dispose()
    {
        foreach (var image in _trackedImages)
        {
            image.Dispose();
        }
        _trackedImages.Clear();
        GC.SuppressFinalize(this);
    }
}
