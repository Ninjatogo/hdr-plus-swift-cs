using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace BurstPhoto.Rendering;

public unsafe class VulkanDescriptorManager : IDisposable
{
    private readonly VulkanContext _ctx;
    public DescriptorPool Pool { get; private set; }
    
    // We can hold common layouts here, or create them per-pipeline.
    // For now, let's allow creating layouts dynamically.

    public VulkanDescriptorManager(VulkanContext ctx, uint maxSets = 100)
    {
        _ctx = ctx;
        CreateDescriptorPool(maxSets);
    }

    private void CreateDescriptorPool(uint maxSets)
    {
        var poolSizes = new DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.StorageBuffer, DescriptorCount = maxSets * 4 },
            new() { Type = DescriptorType.StorageImage, DescriptorCount = maxSets * 4 },
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = maxSets * 4 },
            new() { Type = DescriptorType.SampledImage, DescriptorCount = maxSets * 8 },  // Frequency shaders use up to 5 sampled images
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = maxSets * 4 },  // For future use
        };

        fixed (DescriptorPoolSize* pPoolSizes = poolSizes)
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = pPoolSizes,
                MaxSets = maxSets
            };

            if (_ctx.Vk.CreateDescriptorPool(_ctx.Device, in poolInfo, null, out var pool) != Result.Success)
            {
                throw new Exception("Failed to create descriptor pool!");
            }
            Pool = pool;
        }
    }

    public DescriptorSetLayout CreateLayout(DescriptorSetLayoutBinding[] bindings)
    {
        fixed (DescriptorSetLayoutBinding* pBindings = bindings)
        {
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = pBindings
            };

            if (_ctx.Vk.CreateDescriptorSetLayout(_ctx.Device, in layoutInfo, null, out var layout) != Result.Success)
            {
                throw new Exception("Failed to create descriptor set layout!");
            }
            return layout;
        }
    }

    public DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        var layouts = new[] { layout };
        fixed (DescriptorSetLayout* pLayouts = layouts)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = Pool,
                DescriptorSetCount = 1,
                PSetLayouts = pLayouts
            };

            if (_ctx.Vk.AllocateDescriptorSets(_ctx.Device, in allocInfo, out var set) != Result.Success)
            {
                throw new Exception("Failed to allocate descriptor sets!");
            }
            return set;
        }
    }

    public void UpdateBuffer(DescriptorSet set, uint binding, Buffer buffer, ulong range = Vk.WholeSize, DescriptorType type = DescriptorType.StorageBuffer, ulong offset = 0)
    {
        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = buffer,
            Offset = offset,
            Range = range
        };

        var writeDescriptorSet = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = type,
            DescriptorCount = 1,
            PBufferInfo = &bufferInfo
        };

        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, in writeDescriptorSet, 0, null);
    }
    
    public void UpdateImage(DescriptorSet set, uint binding, ImageView view, ImageLayout layout, DescriptorType type = DescriptorType.StorageImage)
    {
        var imageInfo = new DescriptorImageInfo
        {
            ImageView = view,
            ImageLayout = layout
            // Sampler can be null if type is StorageImage or SampledImage (if using immutable or separate sampler).
            // For SampledImage with CombinedSampler we need a sampler.
            // But usually Texture2D in HLSL implies SampledImage without sampler if used with Load.
            // If used with Sample(), we need a sampler.
            // Metal code uses 'read' (Load).
        };

        var writeDescriptorSet = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorType = type, 
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };

        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, in writeDescriptorSet, 0, null);
    }

    public void Dispose()
    {
        if (Pool.Handle != 0)
        {
            _ctx.Vk.DestroyDescriptorPool(_ctx.Device, Pool, null);
            Pool = default;
        }
    }
}
