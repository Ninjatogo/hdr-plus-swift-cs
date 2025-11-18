using Silk.NET.Vulkan;

namespace HdrPlus.Compute.Vulkan;

/// <summary>
/// Manages Vulkan memory allocation and deallocation.
/// Simplified allocator for compute workloads.
/// </summary>
public unsafe class VulkanMemoryAllocator : IDisposable
{
    private readonly Vk _vk;
    private readonly Instance _instance;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private bool _disposed;

    public VulkanMemoryAllocator(Vk vk, Instance instance, PhysicalDevice physicalDevice, Device device)
    {
        _vk = vk;
        _instance = instance;
        _physicalDevice = physicalDevice;
        _device = device;

        // Get memory properties
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out _memoryProperties);
    }

    public DeviceMemory AllocateMemory(MemoryRequirements requirements, MemoryPropertyFlags properties)
    {
        uint memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, properties);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryTypeIndex
        };

        DeviceMemory memory;
        if (_vk.AllocateMemory(_device, &allocInfo, null, &memory) != Result.Success)
        {
            throw new Exception("Failed to allocate Vulkan memory");
        }

        return memory;
    }

    public void FreeMemory(DeviceMemory memory)
    {
        if (memory.Handle != 0)
        {
            _vk.FreeMemory(_device, memory, null);
        }
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (_memoryProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
            {
                return i;
            }
        }

        throw new Exception("Failed to find suitable memory type");
    }

    public void Dispose()
    {
        if (_disposed) return;
        // No cleanup needed - individual memory allocations are freed separately
        _disposed = true;
    }
}
