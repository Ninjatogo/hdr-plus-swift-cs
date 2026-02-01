using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering;

/// <summary>
/// Represents a GPU image/texture managed through Vulkan.
/// </summary>
/// <remarks>
/// This class encapsulates Vulkan image creation, memory allocation, and data transfer operations.
/// It supports:
/// <list type="bullet">
///   <item><description>2D and 3D images with various formats</description></item>
///   <item><description>Automatic image view creation</description></item>
///   <item><description>Layout transitions with proper pipeline barriers</description></item>
///   <item><description>CPU-to-GPU and GPU-to-CPU data transfers via staging buffers</description></item>
/// </list>
/// </remarks>
public unsafe class VulkanImage : IDisposable
{
    #region Constants

    /// <summary>
    /// Number of mip levels for this image (always 1 for compute textures).
    /// </summary>
    private const uint MipLevelCount = 1;

    /// <summary>
    /// Number of array layers for this image (always 1 for non-array textures).
    /// </summary>
    private const uint ArrayLayerCount = 1;

    #endregion

    #region Private Fields

    private readonly VulkanContext _ctx;

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the native Vulkan image handle.
    /// </summary>
    public Image Handle { get; private set; }

    /// <summary>
    /// Gets the device memory backing this image.
    /// </summary>
    public DeviceMemory Memory { get; private set; }

    /// <summary>
    /// Gets the image view for shader access.
    /// </summary>
    public ImageView View { get; private set; }

    /// <summary>
    /// Gets the width of the image in pixels.
    /// </summary>
    public uint Width { get; }

    /// <summary>
    /// Gets the height of the image in pixels.
    /// </summary>
    public uint Height { get; }

    /// <summary>
    /// Gets the depth of the image (1 for 2D images, greater for 3D images).
    /// </summary>
    public uint Depth { get; }

    /// <summary>
    /// Gets the pixel format of the image.
    /// </summary>
    public Format Format { get; }

    /// <summary>
    /// Gets the current layout of the image. Layout transitions are tracked automatically.
    /// </summary>
    public ImageLayout CurrentLayout { get; private set; }

    /// <summary>
    /// Gets the view type (2D or 3D) for shader binding.
    /// </summary>
    public ImageViewType ViewType { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new 2D Vulkan image.
    /// </summary>
    /// <param name="ctx">The Vulkan context.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">Pixel format.</param>
    /// <param name="usage">Intended usage flags (sampled, storage, transfer, etc.).</param>
    /// <param name="properties">Memory property flags (default: device-local for GPU-only access).</param>
    public VulkanImage(VulkanContext ctx, uint width, uint height, Format format, ImageUsageFlags usage, MemoryPropertyFlags properties = MemoryPropertyFlags.DeviceLocalBit)
        : this(ctx, width, height, depth: 1, format, usage, ImageViewType.Type2D, properties)
    {
    }

    /// <summary>
    /// Creates a new 2D or 3D Vulkan image.
    /// </summary>
    /// <param name="ctx">The Vulkan context.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="depth">Depth in pixels (1 for 2D images).</param>
    /// <param name="format">Pixel format.</param>
    /// <param name="usage">Intended usage flags.</param>
    /// <param name="viewType">Image view type (2D or 3D).</param>
    /// <param name="properties">Memory property flags.</param>
    public VulkanImage(VulkanContext ctx, uint width, uint height, uint depth, Format format, ImageUsageFlags usage, ImageViewType viewType, MemoryPropertyFlags properties = MemoryPropertyFlags.DeviceLocalBit)
    {
        _ctx = ctx;
        Width = width;
        Height = height;
        Depth = depth;
        Format = format;
        ViewType = viewType;
        CurrentLayout = ImageLayout.Undefined;

        CreateNativeImage(usage);
        AllocateDeviceMemory(properties);
        CreateNativeImageView();
    }

    #endregion

    #region Private Initialization Methods

    /// <summary>
    /// Creates the native Vulkan image object.
    /// </summary>
    private void CreateNativeImage(ImageUsageFlags usage)
    {
        var imageType = Depth > 1 ? ImageType.Type3D : ImageType.Type2D;

        var imageCreateInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = imageType,
            Extent = new Extent3D(Width, Height, Depth),
            MipLevels = MipLevelCount,
            ArrayLayers = ArrayLayerCount,
            Format = Format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };

        if (_ctx.Vk.CreateImage(_ctx.Device, in imageCreateInfo, null, out var nativeImage) != Result.Success)
        {
            throw new Exception("Failed to create Vulkan image");
        }
        Handle = nativeImage;
    }

    /// <summary>
    /// Allocates and binds device memory for the image.
    /// </summary>
    private void AllocateDeviceMemory(MemoryPropertyFlags properties)
    {
        _ctx.Vk.GetImageMemoryRequirements(_ctx.Device, Handle, out var memoryRequirements);

        var memoryAllocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = FindSuitableMemoryType(memoryRequirements.MemoryTypeBits, properties)
        };

        if (_ctx.Vk.AllocateMemory(_ctx.Device, in memoryAllocateInfo, null, out var deviceMemory) != Result.Success)
        {
            throw new Exception("Failed to allocate image device memory");
        }
        Memory = deviceMemory;

        _ctx.Vk.BindImageMemory(_ctx.Device, Handle, Memory, memoryOffset: 0);
    }

    /// <summary>
    /// Creates an image view for shader access.
    /// </summary>
    private void CreateNativeImageView()
    {
        var imageViewCreateInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Handle,
            ViewType = ViewType,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = MipLevelCount,
                BaseArrayLayer = 0,
                LayerCount = ArrayLayerCount
            }
        };

        if (_ctx.Vk.CreateImageView(_ctx.Device, in imageViewCreateInfo, null, out var nativeView) != Result.Success)
        {
            throw new Exception("Failed to create image view");
        }
        View = nativeView;
    }

    #endregion

    #region Layout Transition

    /// <summary>
    /// Transitions the image to a new layout with appropriate pipeline barriers.
    /// </summary>
    /// <param name="newLayout">The target layout.</param>
    /// <param name="cmdBuffer">Optional command buffer. If null, a single-time command buffer is used.</param>
    /// <remarks>
    /// Layout transitions ensure proper synchronization between pipeline stages.
    /// The method automatically determines appropriate access masks and pipeline stages
    /// based on the current and target layouts.
    /// </remarks>
    public void TransitionLayout(ImageLayout newLayout, CommandBuffer? cmdBuffer = null)
    {
        if (CurrentLayout == newLayout) return;

        var usingSingleTimeBuffer = cmdBuffer == null;
        var commandBuffer = cmdBuffer ?? _ctx.BeginSingleTimeCommands();

        var imageBarrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = CurrentLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Handle,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = MipLevelCount,
                BaseArrayLayer = 0,
                LayerCount = ArrayLayerCount
            }
        };

        PipelineStageFlags sourcePipelineStage;
        PipelineStageFlags destinationPipelineStage;

        // Determine barrier parameters based on layout transition type
        if (CurrentLayout == ImageLayout.Undefined && newLayout == ImageLayout.General)
        {
            // Undefined -> General: First use in compute shader
            imageBarrier.SrcAccessMask = 0;
            imageBarrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            sourcePipelineStage = PipelineStageFlags.TopOfPipeBit;
            destinationPipelineStage = PipelineStageFlags.ComputeShaderBit;
        }
        else if (CurrentLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            // Undefined -> TransferDst: Preparing for CPU-to-GPU upload
            imageBarrier.SrcAccessMask = 0;
            imageBarrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourcePipelineStage = PipelineStageFlags.TopOfPipeBit;
            destinationPipelineStage = PipelineStageFlags.TransferBit;
        }
        else if (CurrentLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.General)
        {
            // TransferDst -> General: After upload, ready for compute
            imageBarrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            imageBarrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            sourcePipelineStage = PipelineStageFlags.TransferBit;
            destinationPipelineStage = PipelineStageFlags.ComputeShaderBit;
        }
        else if (CurrentLayout == ImageLayout.General && newLayout == ImageLayout.TransferSrcOptimal)
        {
            // General -> TransferSrc: Preparing for GPU-to-CPU download
            imageBarrier.SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            imageBarrier.DstAccessMask = AccessFlags.TransferReadBit;
            sourcePipelineStage = PipelineStageFlags.ComputeShaderBit;
            destinationPipelineStage = PipelineStageFlags.TransferBit;
        }
        else
        {
            // Generic fallback for unhandled transitions
            imageBarrier.SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit;
            imageBarrier.DstAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit;
            sourcePipelineStage = PipelineStageFlags.AllCommandsBit;
            destinationPipelineStage = PipelineStageFlags.AllCommandsBit;
        }

        _ctx.Vk.CmdPipelineBarrier(commandBuffer, sourcePipelineStage, destinationPipelineStage, 0, 0, null, 0, null, 1, in imageBarrier);

        if (usingSingleTimeBuffer)
        {
            _ctx.EndSingleTimeCommands(commandBuffer);
        }

        CurrentLayout = newLayout;
    }

    #endregion

    #region Memory Type Selection

    /// <summary>
    /// Finds a memory type that satisfies the specified requirements.
    /// </summary>
    /// <param name="typeFilter">Bitmask of acceptable memory types.</param>
    /// <param name="requiredProperties">Required memory property flags.</param>
    /// <returns>Index of a suitable memory type.</returns>
    private uint FindSuitableMemoryType(uint typeFilter, MemoryPropertyFlags requiredProperties)
    {
        _ctx.Vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, out var deviceMemoryProperties);

        for (var memoryTypeIndex = 0; memoryTypeIndex < deviceMemoryProperties.MemoryTypeCount; memoryTypeIndex++)
        {
            var isTypeSupported = (typeFilter & (1 << memoryTypeIndex)) != 0;
            var hasRequiredProperties = (deviceMemoryProperties.MemoryTypes[memoryTypeIndex].PropertyFlags & requiredProperties) == requiredProperties;

            if (isTypeSupported && hasRequiredProperties)
            {
                return (uint)memoryTypeIndex;
            }
        }

        throw new Exception("Failed to find suitable memory type for image");
    }

    #endregion

    #region Data Transfer Methods

    /// <summary>
    /// Uploads data from CPU memory to this image.
    /// </summary>
    /// <typeparam name="T">Element type (must be unmanaged).</typeparam>
    /// <param name="sourceData">Source data array.</param>
    /// <param name="cmdBuffer">Optional command buffer for batching.</param>
    /// <remarks>
    /// Uses a staging buffer to transfer data from host-visible memory to device-local memory.
    /// The image layout is automatically transitioned as needed.
    /// </remarks>
    public void SetData<T>(T[] sourceData, CommandBuffer? cmdBuffer = null) where T : unmanaged
    {
        var dataSizeBytes = (ulong)(sourceData.Length * sizeof(T));

        // Create host-visible staging buffer for CPU-to-GPU transfer
        using var stagingBuffer = new VulkanBuffer(_ctx, dataSizeBytes, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        stagingBuffer.SetData(sourceData);

        TransitionLayout(ImageLayout.TransferDstOptimal, cmdBuffer);

        var usingSingleTimeBuffer = cmdBuffer == null;
        var commandBuffer = cmdBuffer ?? _ctx.BeginSingleTimeCommands();

        var copyRegion = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,  // Tightly packed
            BufferImageHeight = 0,  // Tightly packed

            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = ArrayLayerCount
            },

            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(Width, Height, 1)
        };

        _ctx.Vk.CmdCopyBufferToImage(commandBuffer, stagingBuffer.Handle, Handle, ImageLayout.TransferDstOptimal, 1, in copyRegion);

        if (usingSingleTimeBuffer)
        {
            _ctx.EndSingleTimeCommands(commandBuffer);
        }

        TransitionLayout(ImageLayout.General, cmdBuffer);
    }

    /// <summary>
    /// Gets the number of bytes per pixel for the current image format.
    /// </summary>
    private int GetBytesPerPixel()
    {
        return Format switch
        {
            // 32-bit float formats
            Format.R32Sfloat => 4,
            Format.R32G32Sfloat => 8,
            Format.R32G32B32Sfloat => 12,
            Format.R32G32B32A32Sfloat => 16,

            // 16-bit float formats
            Format.R16Sfloat => 2,
            Format.R16G16Sfloat => 4,
            Format.R16G16B16A16Sfloat => 8,

            // 16-bit integer formats
            Format.R16G16B16A16Sint => 8,  // 4 x 16-bit signed integers
            Format.R16Uint => 2,

            // 8-bit formats
            Format.R8Unorm => 1,
            Format.R8G8B8A8Unorm => 4,

            _ => throw new NotSupportedException($"Format {Format} not supported for data transfer")
        };
    }

    /// <summary>
    /// Downloads data from this image to CPU memory.
    /// </summary>
    /// <typeparam name="T">Element type (must be unmanaged).</typeparam>
    /// <param name="cmdBuffer">Optional command buffer for batching.</param>
    /// <param name="wait">Whether to wait for transfer completion (default: true).</param>
    /// <returns>Array containing the image data.</returns>
    /// <remarks>
    /// Uses a staging buffer to transfer data from device-local memory to host-visible memory.
    /// The returned array size is based on the image dimensions and format, not sizeof(T).
    /// </remarks>
    public T[] GetData<T>(CommandBuffer? cmdBuffer = null, bool wait = true) where T : unmanaged
    {
        // Calculate image size based on format, not the generic type T
        var bytesPerPixel = GetBytesPerPixel();
        var imageSizeBytes = (ulong)(Width * Height * bytesPerPixel);

        using var stagingBuffer = new VulkanBuffer(_ctx, imageSizeBytes, BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        TransitionLayout(ImageLayout.TransferSrcOptimal, cmdBuffer);

        var usingSingleTimeBuffer = cmdBuffer == null;
        var commandBuffer = cmdBuffer ?? _ctx.BeginSingleTimeCommands();

        var copyRegion = new BufferImageCopy
        {
            BufferImageHeight = 0,  // Tightly packed
            BufferRowLength = 0,  // Tightly packed
            BufferOffset = 0,

            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = ArrayLayerCount
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(Width, Height, 1)
        };

        _ctx.Vk.CmdCopyImageToBuffer(commandBuffer, Handle, ImageLayout.TransferSrcOptimal, stagingBuffer.Handle, 1, in copyRegion);

        if (usingSingleTimeBuffer)
        {
            _ctx.EndSingleTimeCommands(commandBuffer);
        }

        TransitionLayout(ImageLayout.General, cmdBuffer);

        // Calculate element count based on actual image data size
        var elementCount = imageSizeBytes / (ulong)sizeof(T);
        return stagingBuffer.GetData<T>(elementCount);
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Releases all Vulkan resources associated with this image.
    /// </summary>
    public void Dispose()
    {
        if (View.Handle != 0)
        {
            _ctx.Vk.DestroyImageView(_ctx.Device, View, null);
            View = default;
        }
        if (Handle.Handle != 0)
        {
            _ctx.Vk.DestroyImage(_ctx.Device, Handle, null);
            Handle = default;
        }
        if (Memory.Handle != 0)
        {
            _ctx.Vk.FreeMemory(_ctx.Device, Memory, null);
            Memory = default;
        }
    }

    #endregion
}
