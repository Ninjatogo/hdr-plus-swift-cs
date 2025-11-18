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
    private DX12Pipeline? _currentPipeline;
    private bool _isRecording;
    private bool _disposed;

    public string? Label { get; set; }

    public DX12CommandBuffer(DX12ComputeDevice device, D3D12 d3d12, ComPtr<ID3D12Device> d3dDevice, ComPtr<ID3D12CommandQueue> commandQueue)
    {
        _device = device;
        _d3d12 = d3d12;
        _commandQueue = commandQueue;

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

        // Reset allocator and command list
        _allocator.Get()->Reset().ThrowHResult("Failed to reset command allocator");
        _commandList.Get()->Reset(_allocator.Get(), null).ThrowHResult("Failed to reset command list");

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

        // Set as UAV (unordered access view)
        // TODO: Create descriptor heap and properly bind resources
        // For now, this is a placeholder
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

        // Set as UAV
        // TODO: Create descriptor heap and properly bind resources
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
