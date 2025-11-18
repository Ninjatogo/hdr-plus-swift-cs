using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.Vulkan;

/// <summary>
/// Vulkan implementation of GPU texture.
/// </summary>
public unsafe class VulkanTexture : IComputeTexture
{
    private readonly VulkanComputeDevice _computeDevice;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly VulkanMemoryAllocator _memoryAllocator;
    private Image _image;
    private DeviceMemory _memory;
    private ImageView _imageView;
    private readonly TextureUsage _usage;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public TextureFormat Format { get; }
    public string? Label { get; set; }

    internal Image GetImage() => _image;
    internal ImageView GetImageView() => _imageView;

    public VulkanTexture(
        VulkanComputeDevice computeDevice,
        Vk vk,
        Device device,
        VulkanMemoryAllocator memoryAllocator,
        int width,
        int height,
        int depth,
        TextureFormat format,
        TextureUsage usage)
    {
        _computeDevice = computeDevice;
        _vk = vk;
        _device = device;
        _memoryAllocator = memoryAllocator;
        Width = width;
        Height = height;
        Depth = depth;
        Format = format;
        _usage = usage;

        CreateImage();
        CreateImageView();
    }

    private void CreateImage()
    {
        var vkFormat = ConvertFormat(Format);
        var imageType = Depth > 1 ? ImageType.Type3D : ImageType.Type2D;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = imageType,
            Format = vkFormat,
            Extent = new Extent3D((uint)Width, (uint)Height, (uint)Depth),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        fixed (Image* imagePtr = &_image)
        {
            if (_vk.CreateImage(_device, &imageInfo, null, imagePtr) != Result.Success)
            {
                throw new Exception("Failed to create Vulkan image");
            }
        }

        // Allocate memory
        MemoryRequirements memRequirements;
        _vk.GetImageMemoryRequirements(_device, _image, &memRequirements);

        _memory = _memoryAllocator.AllocateMemory(memRequirements, MemoryPropertyFlags.DeviceLocalBit);

        // Bind image to memory
        if (_vk.BindImageMemory(_device, _image, _memory, 0) != Result.Success)
        {
            throw new Exception("Failed to bind image memory");
        }
    }

    private void CreateImageView()
    {
        var vkFormat = ConvertFormat(Format);
        var viewType = Depth > 1 ? ImageViewType.Type3D : ImageViewType.Type2D;

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = viewType,
            Format = vkFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        fixed (ImageView* viewPtr = &_imageView)
        {
            if (_vk.CreateImageView(_device, &viewInfo, null, viewPtr) != Result.Success)
            {
                throw new Exception("Failed to create image view");
            }
        }
    }

    private static Silk.NET.Vulkan.Format ConvertFormat(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.R16_Float => Silk.NET.Vulkan.Format.R16Sfloat,
            TextureFormat.R32_Float => Silk.NET.Vulkan.Format.R32Sfloat,
            TextureFormat.RG16_SInt => Silk.NET.Vulkan.Format.R16G16Sint,
            TextureFormat.RGBA16_Float => Silk.NET.Vulkan.Format.R16G16B16A16Sfloat,
            TextureFormat.RGBA32_Float => Silk.NET.Vulkan.Format.R32G32B32A32Sfloat,
            _ => throw new ArgumentException($"Unsupported texture format: {format}")
        };
    }

    public void ReadData<T>(Span<T> destination) where T : unmanaged
    {
        throw new NotImplementedException("Reading texture data requires staging buffer and command buffer implementation");
    }

    public void WriteData<T>(ReadOnlySpan<T> source) where T : unmanaged
    {
        throw new NotImplementedException("Writing texture data requires staging buffer and command buffer implementation");
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_imageView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _imageView, null);
        }

        if (_image.Handle != 0)
        {
            _vk.DestroyImage(_device, _image, null);
        }

        if (_memory.Handle != 0)
        {
            _memoryAllocator.FreeMemory(_memory);
        }

        _disposed = true;
    }
}
