using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering.Utilities;

/// <summary>
/// Utility methods for texture operations.
/// Extracted from VulkanComputePipeline for better code organization.
/// </summary>
public unsafe class TextureUtilities
{
    private readonly VulkanContext _ctx;

    public TextureUtilities(VulkanContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Fills a texture with zeros.
    /// </summary>
    public void FillWithZeros(VulkanImage texture)
    {
        var channels = texture.Format == Format.R32Sfloat ? 1 : 4; // R32 or RGBA32
        var size = (int)(texture.Width * texture.Height * channels);
        texture.SetData(new float[size]);
    }

    /// <summary>
    /// Adds source texture to accumulator with optional weight (CPU-based).
    /// </summary>
    public void AddTexture(VulkanImage source, VulkanImage accumulator, float weight = 1.0f)
    {
        var srcData = source.GetData<float>();
        var accData = accumulator.GetData<float>();
        for (var i = 0; i < srcData.Length; i++)
        {
            accData[i] += srcData[i] * weight;
        }
        accumulator.SetData(accData);
    }

    /// <summary>
    /// Calculates mean value of texture (uses first channel only for multi-channel textures).
    /// Used for mismatch normalization.
    /// </summary>
    public float TextureMean(VulkanImage texture)
    {
        var data = texture.GetData<float>();
        double sum = 0;
        var channels = texture.Format == Format.R32Sfloat ? 1 : 4;
        for (var i = 0; i < data.Length; i += channels)
        {
            sum += data[i]; // Use first channel only
        }

        return (float)(sum / (data.Length / channels));
    }

    /// <summary>
    /// GPU-based image copy using Vulkan's vkCmdCopyImage.
    /// </summary>
    public void CopyImage(VulkanImage src, VulkanImage dst, int width, int height)
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
}
