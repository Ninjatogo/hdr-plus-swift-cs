using System;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Silk.NET.Core.Native;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace BurstPhoto.Rendering;

public unsafe class VulkanBuffer : IDisposable
{
    private readonly VulkanContext _ctx;
    public Buffer Handle { get; private set; }
    public DeviceMemory Memory { get; private set; }
    public ulong Size { get; private set; }
    public BufferUsageFlags Usage { get; private set; }
    public MemoryPropertyFlags MemoryProperties { get; private set; }

    public VulkanBuffer(VulkanContext ctx, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        _ctx = ctx;
        Size = size;
        Usage = usage;
        MemoryProperties = properties;

        CreateBuffer();
        AllocateMemory();
    }

    private void CreateBuffer()
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = Size,
            Usage = Usage,
            SharingMode = SharingMode.Exclusive
        };

        if (_ctx.Vk.CreateBuffer(_ctx.Device, in bufferInfo, null, out var buffer) != Result.Success)
        {
            throw new Exception("Failed to create buffer!");
        }
        Handle = buffer;
    }

    private void AllocateMemory()
    {
        _ctx.Vk.GetBufferMemoryRequirements(_ctx.Device, Handle, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryProperties)
        };

        if (_ctx.Vk.AllocateMemory(_ctx.Device, in allocInfo, null, out var memory) != Result.Success)
        {
            throw new Exception("Failed to allocate buffer memory!");
        }
        Memory = memory;

        _ctx.Vk.BindBufferMemory(_ctx.Device, Handle, Memory, 0);
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

    public void UploadData<T>(T[] data) where T : unmanaged
    {
        ulong dataSize = (ulong)(data.Length * sizeof(T));

        // Use staging buffer for better performance on discrete GPUs
        using var stagingBuffer = new VulkanBuffer(_ctx, dataSize, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        void* mappedData;
        _ctx.Vk.MapMemory(_ctx.Device, stagingBuffer.Memory, 0, dataSize, 0, &mappedData);
        fixed (T* pData = data)
        {
            System.Buffer.MemoryCopy(pData, mappedData, dataSize, dataSize);
        }
        _ctx.Vk.UnmapMemory(_ctx.Device, stagingBuffer.Memory);

        CopyBuffer(stagingBuffer.Handle, Handle, dataSize);
    }
    
    // For direct mapping (only if HostVisible)
    public void MapAndWrite<T>(T[] data) where T : unmanaged
    {
         if ((MemoryProperties & MemoryPropertyFlags.HostVisibleBit) == 0)
         {
             throw new InvalidOperationException("Cannot map non-host-visible memory directly. Use UploadData instead.");
         }
         
         ulong dataSize = (ulong)(data.Length * sizeof(T));
         void* mappedData;
         _ctx.Vk.MapMemory(_ctx.Device, Memory, 0, dataSize, 0, &mappedData);
         fixed (T* pData = data)
         {
             System.Buffer.MemoryCopy(pData, mappedData, dataSize, dataSize);
         }
         _ctx.Vk.UnmapMemory(_ctx.Device, Memory);
    }
    
    // Aliases for compatibility
    public void SetData<T>(T[] data) where T : unmanaged => UploadData(data);
    public T[] GetData<T>(ulong count) where T : unmanaged => DownloadData<T>(count);

    public T[] DownloadData<T>(ulong count) where T : unmanaged
    {
        ulong dataSize = count * (ulong)sizeof(T);
        var result = new T[count];

        // If host visible, map directly
        if ((MemoryProperties & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
             void* mappedData;
             _ctx.Vk.MapMemory(_ctx.Device, Memory, 0, dataSize, 0, &mappedData);
             fixed (T* pResult = result)
             {
                 System.Buffer.MemoryCopy(mappedData, pResult, dataSize, dataSize);
             }
             _ctx.Vk.UnmapMemory(_ctx.Device, Memory);
        }
        else
        {
            // Copy to staging buffer first
            using var stagingBuffer = new VulkanBuffer(_ctx, dataSize, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            
            CopyBuffer(Handle, stagingBuffer.Handle, dataSize);
            
            void* mappedData;
            _ctx.Vk.MapMemory(_ctx.Device, stagingBuffer.Memory, 0, dataSize, 0, &mappedData);
            fixed (T* pResult = result)
            {
                System.Buffer.MemoryCopy(mappedData, pResult, dataSize, dataSize);
            }
            _ctx.Vk.UnmapMemory(_ctx.Device, stagingBuffer.Memory);
        }

        return result;
    }

    private void CopyBuffer(Buffer srcBuffer, Buffer dstBuffer, ulong size)
    {
        var commandBuffer = _ctx.BeginSingleTimeCommands();

        var copyRegion = new BufferCopy { Size = size };
        _ctx.Vk.CmdCopyBuffer(commandBuffer, srcBuffer, dstBuffer, 1, in copyRegion);

        _ctx.EndSingleTimeCommands(commandBuffer);
    }

    public void Dispose()
    {
        if (Handle.Handle != 0)
        {
            _ctx.Vk.DestroyBuffer(_ctx.Device, Handle, null);
            Handle = default;
        }
        if (Memory.Handle != 0)
        {
            _ctx.Vk.FreeMemory(_ctx.Device, Memory, null);
            Memory = default;
        }
    }
}
