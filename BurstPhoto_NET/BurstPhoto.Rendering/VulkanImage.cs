using System;
using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering;

public unsafe class VulkanImage : IDisposable
{
    private readonly VulkanContext _ctx;
    public Image Handle { get; private set; }
    public DeviceMemory Memory { get; private set; }
    public ImageView View { get; private set; }
    public uint Width { get; }
    public uint Height { get; }
    public uint Depth { get; }
    public Format Format { get; }
    public ImageLayout CurrentLayout { get; private set; }
    public ImageViewType ViewType { get; }

    public VulkanImage(VulkanContext ctx, uint width, uint height, Format format, ImageUsageFlags usage, MemoryPropertyFlags properties = MemoryPropertyFlags.DeviceLocalBit)
        : this(ctx, width, height, 1, format, usage, ImageViewType.Type2D, properties)
    {
    }

    public VulkanImage(VulkanContext ctx, uint width, uint height, uint depth, Format format, ImageUsageFlags usage, ImageViewType viewType, MemoryPropertyFlags properties = MemoryPropertyFlags.DeviceLocalBit)
    {
        _ctx = ctx;
        Width = width;
        Height = height;
        Depth = depth;
        Format = format;
        ViewType = viewType;
        CurrentLayout = ImageLayout.Undefined;

        CreateImage(usage);
        AllocateMemory(properties);
        CreateImageView();
    }

    private void CreateImage(ImageUsageFlags usage)
    {
        var imageType = Depth > 1 ? ImageType.Type3D : ImageType.Type2D;
        
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = imageType,
            Extent = new Extent3D(Width, Height, Depth),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };

        if (_ctx.Vk.CreateImage(_ctx.Device, in imageInfo, null, out var image) != Result.Success)
        {
            throw new Exception("Failed to create image!");
        }
        Handle = image;
    }

    private void AllocateMemory(MemoryPropertyFlags properties)
    {
        _ctx.Vk.GetImageMemoryRequirements(_ctx.Device, Handle, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties)
        };

        if (_ctx.Vk.AllocateMemory(_ctx.Device, in allocInfo, null, out var memory) != Result.Success)
        {
            throw new Exception("Failed to allocate image memory!");
        }
        Memory = memory;

        _ctx.Vk.BindImageMemory(_ctx.Device, Handle, Memory, 0);
    }

    private void CreateImageView()
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Handle,
            ViewType = ViewType,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        if (_ctx.Vk.CreateImageView(_ctx.Device, in viewInfo, null, out var view) != Result.Success)
        {
            throw new Exception("Failed to create image view!");
        }
        View = view;
    }

    public void TransitionLayout(ImageLayout newLayout, CommandBuffer? cmdBuffer = null)
    {
        if (CurrentLayout == newLayout) return;

        bool singleTime = cmdBuffer == null;
        var cmd = cmdBuffer ?? _ctx.BeginSingleTimeCommands();

        var barrier = new ImageMemoryBarrier
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
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        PipelineStageFlags sourceStage;
        PipelineStageFlags destinationStage;

        if (CurrentLayout == ImageLayout.Undefined && newLayout == ImageLayout.General)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.ComputeShaderBit;
        }
        else if (CurrentLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            sourceStage = PipelineStageFlags.TopOfPipeBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else if (CurrentLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.General)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            sourceStage = PipelineStageFlags.TransferBit;
            destinationStage = PipelineStageFlags.ComputeShaderBit;
        }
        else if (CurrentLayout == ImageLayout.General && newLayout == ImageLayout.TransferSrcOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
            barrier.DstAccessMask = AccessFlags.TransferReadBit;
            sourceStage = PipelineStageFlags.ComputeShaderBit;
            destinationStage = PipelineStageFlags.TransferBit;
        }
        else
        {
            // Default generic barrier
            barrier.SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit;
            barrier.DstAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit;
            sourceStage = PipelineStageFlags.AllCommandsBit;
            destinationStage = PipelineStageFlags.AllCommandsBit;
        }

        _ctx.Vk.CmdPipelineBarrier(cmd, sourceStage, destinationStage, 0, 0, null, 0, null, 1, in barrier);

        if (singleTime)
        {
            _ctx.EndSingleTimeCommands(cmd);
        }

        CurrentLayout = newLayout;
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _ctx.Vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, out var memProperties);

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }

        throw new Exception("Failed to find suitable memory type!");
    }

    public void SetData<T>(T[] data, CommandBuffer? cmdBuffer = null) where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        
        // create staging buffer
        using var stagingBuffer = new VulkanBuffer(_ctx, size, BufferUsageFlags.TransferSrcBit, 
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            
        stagingBuffer.SetData(data);
        
        TransitionLayout(ImageLayout.TransferDstOptimal, cmdBuffer);
        
        bool singleTime = cmdBuffer == null;
        var cmd = cmdBuffer ?? _ctx.BeginSingleTimeCommands();
        
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(Width, Height, 1)
        };
        
        _ctx.Vk.CmdCopyBufferToImage(cmd, stagingBuffer.Handle, Handle, ImageLayout.TransferDstOptimal, 1, in region);
        
        if (singleTime)
        {
            _ctx.EndSingleTimeCommands(cmd);
        }
        
        TransitionLayout(ImageLayout.General, cmdBuffer);
    }

    public T[] GetData<T>(CommandBuffer? cmdBuffer = null, bool wait = true) where T : unmanaged
    {
        ulong size = (ulong)(Width * Height * sizeof(T)); // Assuming packed
         // Note: row alignment might be issue for T[] but for tightly packed formats usually fine.
         // Image copies to buffer are tightly packed usually if row pitch = width.
         
        using var stagingBuffer = new VulkanBuffer(_ctx, size, BufferUsageFlags.TransferDstBit, 
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            
        TransitionLayout(ImageLayout.TransferSrcOptimal, cmdBuffer);
        
        bool singleTime = cmdBuffer == null;
        var cmd = cmdBuffer ?? _ctx.BeginSingleTimeCommands();
        
        var region = new BufferImageCopy
        {
            BufferImageHeight = 0,
            BufferRowLength = 0,
            BufferOffset = 0,
            
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0, // Base level only
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(Width, Height, 1)
        };
        
        _ctx.Vk.CmdCopyImageToBuffer(cmd, Handle, ImageLayout.TransferSrcOptimal, stagingBuffer.Handle, 1, in region);
        
        if (singleTime)
        {
            _ctx.EndSingleTimeCommands(cmd);
        }
        
        TransitionLayout(ImageLayout.General, cmdBuffer);
        
        ulong count = size / (ulong)sizeof(T);
        return stagingBuffer.GetData<T>(count);
    }

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
}
