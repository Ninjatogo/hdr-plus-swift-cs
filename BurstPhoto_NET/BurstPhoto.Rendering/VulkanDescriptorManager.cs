using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace BurstPhoto.Rendering;

public unsafe class VulkanDescriptorManager : IDisposable
{
    private readonly VulkanContext _ctx;
    private uint _maxSets;
    private int _allocatedCount;

    public DescriptorPool Pool { get; private set; }

    /// <summary>
    /// Current maximum number of descriptor sets the pool can hold.
    /// </summary>
    public uint MaxSets => _maxSets;

    // We can hold common layouts here, or create them per-pipeline.
    // For now, let's allow creating layouts dynamically.

    public VulkanDescriptorManager(VulkanContext ctx, uint maxSets = 100)
    {
        _ctx = ctx;
        _maxSets = maxSets;
        CreateDescriptorPool(maxSets);
    }

    /// <summary>
    /// Calculates the required pool size for a given workload.
    /// </summary>
    /// <param name="imageCount">Number of images in the burst</param>
    /// <param name="isHigherQuality">True for Higher Quality mode (4 iterations)</param>
    /// <returns>Recommended pool size with safety margin</returns>
    public static uint CalculateRequiredPoolSize(int imageCount, bool isHigherQuality)
    {
        // Detailed breakdown of descriptor set allocations:
        //
        // Per iteration (Higher Quality has 4, Fast has 1):
        //   - Reference frame setup: ~15 sets (prepare, pyramid, conversions, RMS, noise estimation)
        //   - Per comparison image (imageCount - 1 comparisons):
        //     - Alignment search (4 pyramid levels × 4 sets each): 16 sets
        //     - Warp: 1 set
        //     - Merge operations (frequency mode): ~15 sets (FFT, merge, mismatch, etc.)
        //     - Comparison pyramid building: ~5 sets
        //     - Total per comparison: ~37 sets
        //   - Post-iteration processing: ~10 sets (deconvolute, backward FFT, reduce artifacts, convert)
        //
        // Post-processing (once after all iterations):
        //   - Exposure correction: ~5 sets (max reduction X, max reduction Y, apply curve)
        //   - Noise estimation: ~3 sets
        //
        // Formula: iterations × (refSetup + (comparisons × setsPerComparison) + postIteration) + postProcessing

        const int refSetupSets = 15;
        const int setsPerComparison = 37;
        const int postIterationSets = 10;
        const int postProcessingSets = 10; // Exposure correction, final noise estimation, etc.

        var iterations = isHigherQuality ? 4 : 1;
        var comparisons = imageCount - 1; // Reference frame is not compared against itself

        var perIteration = refSetupSets + (comparisons * setsPerComparison) + postIterationSets;
        var required = (iterations * perIteration) + postProcessingSets;

        // Add 30% safety margin and round up to nearest 100
        var withMargin = (uint)(required * 1.3);
        var rounded = ((withMargin + 99) / 100) * 100;

        // Minimum of 300 sets for small workloads (accounts for base overhead)
        return Math.Max(300, rounded);
    }

    /// <summary>
    /// Ensures the pool can hold at least the specified number of sets.
    /// If the current pool is too small, it will be destroyed and recreated.
    /// This should only be called when the pool is empty (after reset or before first use).
    /// </summary>
    /// <param name="requiredSets">Minimum number of sets needed</param>
    public void EnsureCapacity(uint requiredSets)
    {
        if (_maxSets >= requiredSets)
        {
            return; // Current pool is large enough
        }

        Console.WriteLine($"[VulkanDescriptorManager] Resizing pool: {_maxSets} -> {requiredSets} sets");

        // Destroy old pool
        if (Pool.Handle != 0)
        {
            _ctx.Vk.DestroyDescriptorPool(_ctx.Device, Pool, null);
            Pool = default;
        }

        // Create new larger pool
        _maxSets = requiredSets;
        _allocatedCount = 0;
        CreateDescriptorPool(requiredSets);
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

            var result = _ctx.Vk.AllocateDescriptorSets(_ctx.Device, in allocInfo, out var set);
            if (result != Result.Success)
            {
                throw new Exception($"Failed to allocate descriptor set #{_allocatedCount + 1}! Result: {result}. Pool may be exhausted (max: {_maxSets} sets).");
            }
            _allocatedCount++;
            return set;
        }
    }

    /// <summary>
    /// Resets the descriptor pool, freeing all allocated descriptor sets.
    /// Call this between processing runs to prevent pool exhaustion.
    /// </summary>
    public void ResetPool()
    {
        if (Pool.Handle != 0)
        {
            Console.WriteLine($"[VulkanDescriptorManager] Resetting pool (was using {_allocatedCount}/{_maxSets} sets)");
            _ctx.Vk.ResetDescriptorPool(_ctx.Device, Pool, 0);
            _allocatedCount = 0;
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
