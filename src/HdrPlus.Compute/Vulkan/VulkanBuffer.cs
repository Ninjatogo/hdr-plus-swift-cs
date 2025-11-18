using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.Vulkan;

/// <summary>
/// Vulkan implementation of GPU buffer.
/// </summary>
public unsafe class VulkanBuffer : IComputeBuffer
{
    private readonly VulkanComputeDevice _computeDevice;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly VulkanMemoryAllocator _memoryAllocator;
    private Buffer _buffer;
    private DeviceMemory _memory;
    private readonly BufferUsage _usage;
    private bool _disposed;
    private void* _mappedPtr;

    public int SizeInBytes { get; }

    internal Buffer GetBuffer() => _buffer;

    public VulkanBuffer(
        VulkanComputeDevice computeDevice,
        Vk vk,
        Device device,
        VulkanMemoryAllocator memoryAllocator,
        int sizeInBytes,
        BufferUsage usage)
    {
        _computeDevice = computeDevice;
        _vk = vk;
        _device = device;
        _memoryAllocator = memoryAllocator;
        SizeInBytes = sizeInBytes;
        _usage = usage;

        CreateBuffer();
    }

    private void CreateBuffer()
    {
        // Determine Vulkan buffer usage flags
        BufferUsageFlags vkUsage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)SizeInBytes,
            Usage = vkUsage,
            SharingMode = SharingMode.Exclusive
        };

        fixed (Buffer* bufferPtr = &_buffer)
        {
            if (_vk.CreateBuffer(_device, &bufferInfo, null, bufferPtr) != Result.Success)
            {
                throw new Exception("Failed to create Vulkan buffer");
            }
        }

        // Allocate memory
        MemoryRequirements memRequirements;
        _vk.GetBufferMemoryRequirements(_device, _buffer, &memRequirements);

        // Determine memory properties based on usage
        MemoryPropertyFlags memProperties = _usage switch
        {
            BufferUsage.Upload => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            BufferUsage.Readback => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            _ => MemoryPropertyFlags.DeviceLocalBit
        };

        _memory = _memoryAllocator.AllocateMemory(memRequirements, memProperties);

        // Bind buffer to memory
        if (_vk.BindBufferMemory(_device, _buffer, _memory, 0) != Result.Success)
        {
            throw new Exception("Failed to bind buffer memory");
        }
    }

    public void* Map()
    {
        if (_mappedPtr != null)
        {
            return _mappedPtr;
        }

        void* data;
        if (_vk.MapMemory(_device, _memory, 0, (ulong)SizeInBytes, 0, &data) != Result.Success)
        {
            throw new Exception("Failed to map buffer memory");
        }

        _mappedPtr = data;
        return data;
    }

    public void Unmap()
    {
        if (_mappedPtr != null)
        {
            _vk.UnmapMemory(_device, _memory);
            _mappedPtr = null;
        }
    }

    public void ReadData<T>(Span<T> destination) where T : unmanaged
    {
        int expectedSize = destination.Length * Marshal.SizeOf<T>();
        if (expectedSize > SizeInBytes)
        {
            throw new ArgumentException($"Destination buffer too large. Expected at most {SizeInBytes} bytes, got {expectedSize}");
        }

        // For readback, we can map directly
        if (_usage == BufferUsage.Readback)
        {
            void* mapped = Map();
            new Span<T>(mapped, destination.Length).CopyTo(destination);
            Unmap();
        }
        else
        {
            // For device-local buffers, we need a staging buffer
            throw new NotImplementedException("Reading from device-local buffers requires staging buffer implementation");
        }
    }

    public void WriteData<T>(ReadOnlySpan<T> source) where T : unmanaged
    {
        int dataSize = source.Length * Marshal.SizeOf<T>();
        if (dataSize > SizeInBytes)
        {
            throw new ArgumentException($"Source data too large. Buffer size: {SizeInBytes}, data size: {dataSize}");
        }

        // For upload buffers, we can map directly
        if (_usage == BufferUsage.Upload || _usage == BufferUsage.Default)
        {
            void* mapped = Map();
            source.CopyTo(new Span<T>(mapped, source.Length));
            Unmap();
        }
        else
        {
            // For device-local buffers, we need a staging buffer
            throw new NotImplementedException("Writing to device-local buffers requires staging buffer implementation");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Unmap();

        if (_buffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _buffer, null);
        }

        if (_memory.Handle != 0)
        {
            _memoryAllocator.FreeMemory(_memory);
        }

        _disposed = true;
    }
}
