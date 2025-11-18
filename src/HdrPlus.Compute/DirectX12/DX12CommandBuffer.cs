using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace HdrPlus.Compute.DirectX12;

internal unsafe class DX12CommandBuffer : IComputeCommandBuffer
{
    private readonly DX12ComputeDevice _device;
    private readonly D3D12 _d3d12;
    private ComPtr<ID3D12CommandAllocator> _allocator;
    private ComPtr<ID3D12GraphicsCommandList> _commandList;
    private ComPtr<ID3D12CommandQueue> _commandQueue;
    private readonly DX12DescriptorManager _descriptorManager;
    private DX12Pipeline? _currentPipeline;
    private bool _isRecording;
    private bool _disposed;
    private readonly Dictionary<int, GpuDescriptorHandle> _boundDescriptors;

    public string? Label { get; set; }

    public DX12CommandBuffer(DX12ComputeDevice device, D3D12 d3d12, ComPtr<ID3D12Device> d3dDevice, ComPtr<ID3D12CommandQueue> commandQueue, DX12DescriptorManager descriptorManager)
    {
        _device = device;
        _d3d12 = d3d12;
        _commandQueue = commandQueue;
        _descriptorManager = descriptorManager;
        _boundDescriptors = new Dictionary<int, GpuDescriptorHandle>();

        // Create command allocator
        ID3D12CommandAllocator* allocatorPtr;
        d3dDevice.Get()->CreateCommandAllocator(CommandListType.Compute, out allocatorPtr)
            .ThrowHResult("Failed to create command allocator");
        _allocator = new ComPtr<ID3D12CommandAllocator>(allocatorPtr);

        // Create command list
        ID3D12GraphicsCommandList* listPtr;
        d3dDevice.Get()->CreateCommandList(0, CommandListType.Compute, _allocator.Get(), null, out listPtr)
            .ThrowHResult("Failed to create command list");
        _commandList = new ComPtr<ID3D12GraphicsCommandList>(listPtr);

        // Close immediately (will be reset in BeginCompute)
        _commandList.Get()->Close();
        _isRecording = false;
    }

    public void BeginCompute(string? label = null)
    {
        if (_isRecording)
        {
            throw new InvalidOperationException("Command buffer is already recording");
        }

        Label = label;

        // Reset descriptor allocations for this frame
        _descriptorManager.ResetFrame();
        _boundDescriptors.Clear();

        // Reset allocator and command list
        _allocator.Get()->Reset().ThrowHResult("Failed to reset command allocator");
        _commandList.Get()->Reset(_allocator.Get(), null).ThrowHResult("Failed to reset command list");

        // Bind descriptor heaps
        var heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _descriptorManager.GetCbvSrvUavHeap().Get();
        heaps[1] = _descriptorManager.GetSamplerHeap().Get();
        _commandList.Get()->SetDescriptorHeaps(2, heaps);

        _isRecording = true;
    }

    public void SetPipeline(IComputePipeline pipeline)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (pipeline is not DX12Pipeline dx12Pipeline)
        {
            throw new ArgumentException("Pipeline must be a DirectX 12 pipeline");
        }

        _currentPipeline = dx12Pipeline;
        _commandList.Get()->SetPipelineState(dx12Pipeline.GetPipelineState().Get());
        _commandList.Get()->SetComputeRootSignature(dx12Pipeline.GetRootSignature().Get());
    }

    public void SetBuffer(IComputeBuffer buffer, int slot)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (buffer is not DX12Buffer dx12Buffer)
        {
            throw new ArgumentException("Buffer must be a DirectX 12 buffer");
        }

        // Get or create UAV descriptor for this buffer
        var gpuHandle = dx12Buffer.GetOrCreateUAVDescriptor(_descriptorManager);

        // Bind to root signature
        _boundDescriptors[slot] = gpuHandle;
        _commandList.Get()->SetComputeRootDescriptorTable((uint)slot, gpuHandle);
    }

    public void SetTexture(IComputeTexture texture, int slot)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (texture is not DX12Texture dx12Texture)
        {
            throw new ArgumentException("Texture must be a DirectX 12 texture");
        }

        // Get or create UAV descriptor for this texture
        var gpuHandle = dx12Texture.GetOrCreateUAVDescriptor(_descriptorManager);

        // Bind to root signature
        _boundDescriptors[slot] = gpuHandle;
        _commandList.Get()->SetComputeRootDescriptorTable((uint)slot, gpuHandle);
    }

    public void SetBytes<T>(T data, int slot) where T : unmanaged
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        // Set root constants directly
        // TODO: Implement root constants binding
    }

    public void Dispatch(int threadGroupsX, int threadGroupsY, int threadGroupsZ)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (_currentPipeline == null)
        {
            throw new InvalidOperationException("No pipeline set");
        }

        _commandList.Get()->Dispatch((uint)threadGroupsX, (uint)threadGroupsY, (uint)threadGroupsZ);
    }

    public void DispatchThreads(int threadsX, int threadsY, int threadsZ)
    {
        if (_currentPipeline == null)
        {
            throw new InvalidOperationException("No pipeline set");
        }

        var (groupSizeX, groupSizeY, groupSizeZ) = _currentPipeline.GetThreadGroupSize();

        int threadGroupsX = (threadsX + groupSizeX - 1) / groupSizeX;
        int threadGroupsY = (threadsY + groupSizeY - 1) / groupSizeY;
        int threadGroupsZ = (threadsZ + groupSizeZ - 1) / groupSizeZ;

        Dispatch(threadGroupsX, threadGroupsY, threadGroupsZ);
    }

    public void EndCompute()
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        _commandList.Get()->Close().ThrowHResult("Failed to close command list");
        _isRecording = false;
    }

    public void CopyBuffer(IComputeBuffer source, IComputeBuffer destination, int size)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (source is not DX12Buffer srcBuffer || destination is not DX12Buffer dstBuffer)
        {
            throw new ArgumentException("Buffers must be DirectX 12 buffers");
        }

        _commandList.Get()->CopyBufferRegion(
            dstBuffer.GetResource().Get(), 0,
            srcBuffer.GetResource().Get(), 0,
            (ulong)size
        );
    }

    public void CopyTexture(IComputeTexture source, IComputeTexture destination)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (source is not DX12Texture srcTex || destination is not DX12Texture dstTex)
        {
            throw new ArgumentException("Textures must be DirectX 12 textures");
        }

        _commandList.Get()->CopyResource(dstTex.GetResource().Get(), srcTex.GetResource().Get());
    }

    /// <summary>
    /// Inserts a UAV barrier to ensure writes complete before subsequent reads.
    /// </summary>
    public void UAVBarrier(IComputeBuffer? buffer = null, IComputeTexture? texture = null)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Uav,
            Flags = ResourceBarrierFlags.None
        };

        if (buffer is DX12Buffer dx12Buffer)
        {
            barrier.Anonymous.UAV.PResource = dx12Buffer.GetResource().Get();
        }
        else if (texture is DX12Texture dx12Texture)
        {
            barrier.Anonymous.UAV.PResource = dx12Texture.GetResource().Get();
        }
        else
        {
            // Global UAV barrier (affects all resources)
            barrier.Anonymous.UAV.PResource = null;
        }

        _commandList.Get()->ResourceBarrier(1, &barrier);
    }

    /// <summary>
    /// Transitions a buffer from one state to another.
    /// </summary>
    public void TransitionBuffer(IComputeBuffer buffer, ResourceStates stateBefore, ResourceStates stateAfter)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (buffer is not DX12Buffer dx12Buffer)
        {
            throw new ArgumentException("Buffer must be a DirectX 12 buffer");
        }

        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Transition,
            Flags = ResourceBarrierFlags.None
        };

        barrier.Anonymous.Transition.PResource = dx12Buffer.GetResource().Get();
        barrier.Anonymous.Transition.Subresource = 0xFFFFFFFF; // All subresources
        barrier.Anonymous.Transition.StateBefore = stateBefore;
        barrier.Anonymous.Transition.StateAfter = stateAfter;

        _commandList.Get()->ResourceBarrier(1, &barrier);
    }

    /// <summary>
    /// Transitions a texture from one state to another.
    /// </summary>
    public void TransitionTexture(IComputeTexture texture, ResourceStates stateBefore, ResourceStates stateAfter)
    {
        if (!_isRecording)
        {
            throw new InvalidOperationException("Command buffer is not recording");
        }

        if (texture is not DX12Texture dx12Texture)
        {
            throw new ArgumentException("Texture must be a DirectX 12 texture");
        }

        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Transition,
            Flags = ResourceBarrierFlags.None
        };

        barrier.Anonymous.Transition.PResource = dx12Texture.GetResource().Get();
        barrier.Anonymous.Transition.Subresource = 0xFFFFFFFF; // All subresources
        barrier.Anonymous.Transition.StateBefore = stateBefore;
        barrier.Anonymous.Transition.StateAfter = stateAfter;

        _commandList.Get()->ResourceBarrier(1, &barrier);
    }

    internal void Execute()
    {
        if (_isRecording)
        {
            throw new InvalidOperationException("Command buffer is still recording. Call EndCompute() first.");
        }

        // Execute the command list
        var cmdLists = stackalloc ID3D12CommandList*[1];
        cmdLists[0] = (ID3D12CommandList*)_commandList.Get();
        _commandQueue.Get()->ExecuteCommandLists(1, cmdLists);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _commandList.Dispose();
        _allocator.Dispose();
        _disposed = true;
    }
}
