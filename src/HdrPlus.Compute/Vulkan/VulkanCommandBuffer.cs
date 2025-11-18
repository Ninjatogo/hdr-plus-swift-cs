using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.Vulkan;

/// <summary>
/// Vulkan implementation of compute command buffer.
/// Records GPU commands for execution.
/// </summary>
public unsafe class VulkanCommandBuffer : IComputeCommandBuffer
{
    private readonly VulkanComputeDevice _computeDevice;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly CommandPool _commandPool;
    private readonly Queue _queue;
    private readonly VulkanDescriptorManager _descriptorManager;
    private CommandBuffer _commandBuffer;
    private Fence _fence;
    private VulkanPipeline? _currentPipeline;
    private DescriptorSet _currentDescriptorSet;
    private bool _isRecording;
    private bool _disposed;

    public string? Label { get; set; }

    public VulkanCommandBuffer(
        VulkanComputeDevice computeDevice,
        Vk vk,
        Device device,
        CommandPool commandPool,
        Queue queue,
        VulkanDescriptorManager descriptorManager)
    {
        _computeDevice = computeDevice;
        _vk = vk;
        _device = device;
        _commandPool = commandPool;
        _queue = queue;
        _descriptorManager = descriptorManager;

        AllocateCommandBuffer();
        CreateFence();
    }

    private void AllocateCommandBuffer()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        fixed (CommandBuffer* cmdBufferPtr = &_commandBuffer)
        {
            if (_vk.AllocateCommandBuffers(_device, &allocInfo, cmdBufferPtr) != Result.Success)
            {
                throw new Exception("Failed to allocate command buffer");
            }
        }
    }

    private void CreateFence()
    {
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit // Start signaled
        };

        fixed (Fence* fencePtr = &_fence)
        {
            if (_vk.CreateFence(_device, &fenceInfo, null, fencePtr) != Result.Success)
            {
                throw new Exception("Failed to create fence");
            }
        }
    }

    public void BeginCompute(string? label = null)
    {
        Label = label;

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        if (_vk.BeginCommandBuffer(_commandBuffer, &beginInfo) != Result.Success)
        {
            throw new Exception("Failed to begin command buffer");
        }

        _isRecording = true;
    }

    public void SetPipeline(IComputePipeline pipeline)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Cannot set pipeline outside of compute pass");
        }

        if (pipeline is not VulkanPipeline vkPipeline)
        {
            throw new ArgumentException("Pipeline must be a Vulkan pipeline");
        }

        _currentPipeline = vkPipeline;

        // Bind pipeline
        _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Compute, vkPipeline.GetPipeline());

        // Allocate descriptor set for this pipeline
        _currentDescriptorSet = _descriptorManager.AllocateDescriptorSet(vkPipeline.GetDescriptorSetLayout());
    }

    public void SetBuffer(IComputeBuffer buffer, int slot)
    {
        if (!_isRecording || _currentPipeline == null)
        {
            throw new InvalidOperationException("Must set pipeline before setting buffers");
        }

        if (buffer is not VulkanBuffer vkBuffer)
        {
            throw new ArgumentException("Buffer must be a Vulkan buffer");
        }

        // Update descriptor set
        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = vkBuffer.GetBuffer(),
            Offset = 0,
            Range = (ulong)vkBuffer.SizeInBytes
        };

        var writeDescriptor = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _currentDescriptorSet,
            DstBinding = (uint)slot,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &bufferInfo
        };

        _vk.UpdateDescriptorSets(_device, 1, &writeDescriptor, 0, null);
    }

    public void SetTexture(IComputeTexture texture, int slot)
    {
        if (!_isRecording || _currentPipeline == null)
        {
            throw new InvalidOperationException("Must set pipeline before setting textures");
        }

        if (texture is not VulkanTexture vkTexture)
        {
            throw new ArgumentException("Texture must be a Vulkan texture");
        }

        // Update descriptor set
        var imageInfo = new DescriptorImageInfo
        {
            ImageView = vkTexture.GetImageView(),
            ImageLayout = ImageLayout.General
        };

        var writeDescriptor = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _currentDescriptorSet,
            DstBinding = (uint)slot,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &imageInfo
        };

        _vk.UpdateDescriptorSets(_device, 1, &writeDescriptor, 0, null);
    }

    public void SetBytes<T>(T data, int slot) where T : unmanaged
    {
        if (!_isRecording || _currentPipeline == null)
        {
            throw new InvalidOperationException("Must set pipeline before setting bytes");
        }

        // For Vulkan, we need to use push constants or a small uniform buffer
        // For simplicity, we'll create a small buffer
        int size = Marshal.SizeOf<T>();
        var buffer = new VulkanBuffer(_computeDevice, _vk, _device, _computeDevice.GetMemoryAllocator(), size, BufferUsage.Upload);

        Span<T> span = stackalloc T[1];
        span[0] = data;
        buffer.WriteData(span);

        SetBuffer(buffer, slot);
    }

    public void Dispatch(int threadGroupsX, int threadGroupsY, int threadGroupsZ)
    {
        if (!_isRecording || _currentPipeline == null)
        {
            throw new InvalidOperationException("Must set pipeline before dispatching");
        }

        // Bind descriptor sets
        fixed (DescriptorSet* setPtr = &_currentDescriptorSet)
        {
            _vk.CmdBindDescriptorSets(
                _commandBuffer,
                PipelineBindPoint.Compute,
                _currentPipeline.GetPipelineLayout(),
                0,
                1,
                setPtr,
                0,
                null
            );
        }

        // Dispatch compute work
        _vk.CmdDispatch(_commandBuffer, (uint)threadGroupsX, (uint)threadGroupsY, (uint)threadGroupsZ);
    }

    public void DispatchThreads(int threadsX, int threadsY, int threadsZ)
    {
        if (_currentPipeline == null)
        {
            throw new InvalidOperationException("Must set pipeline before dispatching");
        }

        var (groupSizeX, groupSizeY, groupSizeZ) = _currentPipeline.GetThreadGroupSize();

        int groupsX = (threadsX + groupSizeX - 1) / groupSizeX;
        int groupsY = (threadsY + groupSizeY - 1) / groupSizeY;
        int groupsZ = (threadsZ + groupSizeZ - 1) / groupSizeZ;

        Dispatch(groupsX, groupsY, groupsZ);
    }

    public void EndCompute()
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Not currently recording");
        }

        if (_vk.EndCommandBuffer(_commandBuffer) != Result.Success)
        {
            throw new Exception("Failed to end command buffer");
        }

        _isRecording = false;
    }

    public void CopyBuffer(IComputeBuffer source, IComputeBuffer destination, int size)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Cannot copy buffer outside of compute pass");
        }

        if (source is not VulkanBuffer vkSrc || destination is not VulkanBuffer vkDst)
        {
            throw new ArgumentException("Buffers must be Vulkan buffers");
        }

        var copyRegion = new BufferCopy
        {
            SrcOffset = 0,
            DstOffset = 0,
            Size = (ulong)size
        };

        _vk.CmdCopyBuffer(_commandBuffer, vkSrc.GetBuffer(), vkDst.GetBuffer(), 1, &copyRegion);
    }

    public void CopyTexture(IComputeTexture source, IComputeTexture destination)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Cannot copy texture outside of compute pass");
        }

        if (source is not VulkanTexture vkSrc || destination is not VulkanTexture vkDst)
        {
            throw new ArgumentException("Textures must be Vulkan textures");
        }

        var copyRegion = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcOffset = new Offset3D(0, 0, 0),
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            DstOffset = new Offset3D(0, 0, 0),
            Extent = new Extent3D((uint)source.Width, (uint)source.Height, (uint)source.Depth)
        };

        _vk.CmdCopyImage(
            _commandBuffer,
            vkSrc.GetImage(), ImageLayout.TransferSrcOptimal,
            vkDst.GetImage(), ImageLayout.TransferDstOptimal,
            1, &copyRegion
        );
    }

    internal void Execute()
    {
        // Wait for previous execution to complete
        _vk.WaitForFences(_device, 1, in _fence, true, ulong.MaxValue);
        _vk.ResetFences(_device, 1, in _fence);

        // Submit command buffer
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = (CommandBuffer*)Unsafe.AsPointer(ref _commandBuffer)
        };

        if (_vk.QueueSubmit(_queue, 1, &submitInfo, _fence) != Result.Success)
        {
            throw new Exception("Failed to submit command buffer");
        }

        // Wait for completion
        _vk.WaitForFences(_device, 1, in _fence, true, ulong.MaxValue);
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_fence.Handle != 0)
        {
            _vk.WaitForFences(_device, 1, in _fence, true, ulong.MaxValue);
            _vk.DestroyFence(_device, _fence, null);
        }

        if (_commandBuffer.Handle != 0)
        {
            _vk.FreeCommandBuffers(_device, _commandPool, 1, in _commandBuffer);
        }

        _disposed = true;
    }
}
