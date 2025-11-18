using Silk.NET.Vulkan;

namespace HdrPlus.Compute.Vulkan;

/// <summary>
/// Manages Vulkan descriptor pools and sets.
/// Provides descriptor sets for binding resources to shaders.
/// </summary>
public unsafe class VulkanDescriptorManager : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private DescriptorPool _descriptorPool;
    private bool _disposed;

    private const int MaxSets = 1000;
    private const int MaxDescriptors = 10000;

    public VulkanDescriptorManager(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;

        CreateDescriptorPool();
    }

    private void CreateDescriptorPool()
    {
        // Create a large descriptor pool that can handle various types
        var poolSizes = stackalloc DescriptorPoolSize[3];

        poolSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = MaxDescriptors
        };

        poolSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = MaxDescriptors
        };

        poolSizes[2] = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = MaxDescriptors
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes,
            MaxSets = MaxSets,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
        };

        fixed (DescriptorPool* poolPtr = &_descriptorPool)
        {
            if (_vk.CreateDescriptorPool(_device, &poolInfo, null, poolPtr) != Result.Success)
            {
                throw new Exception("Failed to create descriptor pool");
            }
        }
    }

    public DescriptorSet AllocateDescriptorSet(DescriptorSetLayout layout)
    {
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };

        DescriptorSet descriptorSet;
        if (_vk.AllocateDescriptorSets(_device, &allocInfo, &descriptorSet) != Result.Success)
        {
            throw new Exception("Failed to allocate descriptor set");
        }

        return descriptorSet;
    }

    public void FreeDescriptorSet(DescriptorSet descriptorSet)
    {
        _vk.FreeDescriptorSets(_device, _descriptorPool, 1, &descriptorSet);
    }

    public void ResetPool()
    {
        _vk.ResetDescriptorPool(_device, _descriptorPool, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_descriptorPool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        }

        _disposed = true;
    }
}
