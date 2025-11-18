using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.DirectX12;

internal unsafe class DX12Buffer : IComputeBuffer
{
    private readonly DX12ComputeDevice _device;
    private readonly D3D12 _d3d12;
    private ComPtr<ID3D12Resource> _resource;
    private readonly BufferUsage _usage;
    private GpuDescriptorHandle _uavDescriptor;
    private bool _hasUAVDescriptor;
    private bool _disposed;

    public int SizeInBytes { get; }

    public DX12Buffer(DX12ComputeDevice device, D3D12 d3d12, ComPtr<ID3D12Device> d3dDevice, int sizeInBytes, BufferUsage usage)
    {
        _device = device;
        _d3d12 = d3d12;
        _usage = usage;
        SizeInBytes = sizeInBytes;

        // Create heap properties based on usage
        var heapProps = new HeapProperties
        {
            Type = usage switch
            {
                BufferUsage.Upload => HeapType.Upload,
                BufferUsage.Readback => HeapType.Readback,
                _ => HeapType.Default
            },
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 0,
            VisibleNodeMask = 0
        };

        // Create resource description
        var resourceDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = (ulong)sizeInBytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.RowMajor,
            Flags = usage == BufferUsage.Default ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None
        };

        ID3D12Resource* resourcePtr;
        d3dDevice.Get()->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &resourceDesc,
            usage == BufferUsage.Upload ? ResourceStates.GenericRead : ResourceStates.Common,
            null,
            out resourcePtr
        ).ThrowHResult("Failed to create buffer");

        _resource = new ComPtr<ID3D12Resource>(resourcePtr);
    }

    public void ReadData<T>(Span<T> destination) where T : unmanaged
    {
        int expectedSize = destination.Length * Marshal.SizeOf<T>();
        if (expectedSize > SizeInBytes)
        {
            throw new ArgumentException("Destination buffer is too small");
        }

        if (_usage == BufferUsage.Readback)
        {
            // Direct map for readback buffers
            void* mappedData;
            _resource.Get()->Map(0, null, &mappedData).ThrowHResult("Failed to map buffer");
            new Span<T>(mappedData, destination.Length).CopyTo(destination);
            _resource.Get()->Unmap(0, null);
        }
        else
        {
            // Need to copy to staging buffer first
            var stagingBuffer = _device.GetStagingPool().GetReadbackBuffer((ulong)expectedSize);

            // Create temporary command allocator and list for copy
            ID3D12CommandAllocator* allocator;
            _device.GetDevice().Get()->CreateCommandAllocator(CommandListType.Direct, out allocator)
                .ThrowHResult("Failed to create command allocator");

            ID3D12GraphicsCommandList* cmdList;
            _device.GetDevice().Get()->CreateCommandList(0, CommandListType.Direct, allocator, null, out cmdList)
                .ThrowHResult("Failed to create command list");

            // Transition buffer to copy source if needed
            var barrier = new ResourceBarrier();
            barrier.Type = ResourceBarrierType.Transition;
            barrier.Flags = ResourceBarrierFlags.None;
            barrier.Anonymous.Transition.PResource = _resource.Get();
            barrier.Anonymous.Transition.Subresource = 0xFFFFFFFF;
            barrier.Anonymous.Transition.StateBefore = ResourceStates.Common;
            barrier.Anonymous.Transition.StateAfter = ResourceStates.CopySource;
            cmdList->ResourceBarrier(1, &barrier);

            // Copy buffer to staging
            cmdList->CopyBufferRegion(
                stagingBuffer.GetResource().Get(), 0,
                _resource.Get(), 0,
                (ulong)expectedSize
            );

            // Transition back
            barrier.Anonymous.Transition.StateBefore = ResourceStates.CopySource;
            barrier.Anonymous.Transition.StateAfter = ResourceStates.Common;
            cmdList->ResourceBarrier(1, &barrier);

            cmdList->Close();

            // Execute and wait
            var cmdLists = stackalloc ID3D12CommandList*[1];
            cmdLists[0] = (ID3D12CommandList*)cmdList;
            _device.GetCommandQueue().Get()->ExecuteCommandLists(1, cmdLists);
            _device.WaitIdle();

            // Read from staging buffer
            stagingBuffer.ReadData(destination);

            // Cleanup
            cmdList->Release();
            allocator->Release();
        }
    }

    public void WriteData<T>(ReadOnlySpan<T> source) where T : unmanaged
    {
        int dataSize = source.Length * Marshal.SizeOf<T>();
        if (dataSize > SizeInBytes)
        {
            throw new ArgumentException("Source data is too large for buffer");
        }

        if (_usage == BufferUsage.Upload || _usage == BufferUsage.Readback)
        {
            // Direct map for upload/readback buffers
            void* mappedData;
            _resource.Get()->Map(0, null, &mappedData).ThrowHResult("Failed to map buffer");
            source.CopyTo(new Span<T>(mappedData, source.Length));
            _resource.Get()->Unmap(0, null);
        }
        else
        {
            // Use upload staging buffer
            var stagingBuffer = _device.GetStagingPool().GetUploadBuffer((ulong)dataSize);

            // Write to staging buffer
            stagingBuffer.WriteData(source);

            // Create temporary command allocator and list for copy
            ID3D12CommandAllocator* allocator;
            _device.GetDevice().Get()->CreateCommandAllocator(CommandListType.Direct, out allocator)
                .ThrowHResult("Failed to create command allocator");

            ID3D12GraphicsCommandList* cmdList;
            _device.GetDevice().Get()->CreateCommandList(0, CommandListType.Direct, allocator, null, out cmdList)
                .ThrowHResult("Failed to create command list");

            // Transition buffer to copy dest if needed
            var barrier = new ResourceBarrier();
            barrier.Type = ResourceBarrierType.Transition;
            barrier.Flags = ResourceBarrierFlags.None;
            barrier.Anonymous.Transition.PResource = _resource.Get();
            barrier.Anonymous.Transition.Subresource = 0xFFFFFFFF;
            barrier.Anonymous.Transition.StateBefore = ResourceStates.Common;
            barrier.Anonymous.Transition.StateAfter = ResourceStates.CopyDest;
            cmdList->ResourceBarrier(1, &barrier);

            // Copy staging to buffer
            cmdList->CopyBufferRegion(
                _resource.Get(), 0,
                stagingBuffer.GetResource().Get(), 0,
                (ulong)dataSize
            );

            // Transition back
            barrier.Anonymous.Transition.StateBefore = ResourceStates.CopyDest;
            barrier.Anonymous.Transition.StateAfter = ResourceStates.Common;
            cmdList->ResourceBarrier(1, &barrier);

            cmdList->Close();

            // Execute and wait
            var cmdLists = stackalloc ID3D12CommandList*[1];
            cmdLists[0] = (ID3D12CommandList*)cmdList;
            _device.GetCommandQueue().Get()->ExecuteCommandLists(1, cmdLists);
            _device.WaitIdle();

            // Cleanup
            cmdList->Release();
            allocator->Release();
        }
    }

    public void* Map()
    {
        void* mappedData;
        _resource.Get()->Map(0, null, &mappedData).ThrowHResult("Failed to map buffer");
        return mappedData;
    }

    public void Unmap()
    {
        _resource.Get()->Unmap(0, null);
    }

    internal ComPtr<ID3D12Resource> GetResource() => _resource;

    /// <summary>
    /// Gets or creates a UAV descriptor for this buffer.
    /// Note: Descriptors are created from the descriptor manager's frame heap,
    /// so they should be recreated each frame if the heap is reset.
    /// </summary>
    internal GpuDescriptorHandle GetOrCreateUAVDescriptor(DX12DescriptorManager descriptorManager)
    {
        // For default (GPU) buffers, always create a fresh descriptor
        // since the descriptor heap may be reset each frame
        if (_usage == BufferUsage.Default)
        {
            uint numElements = (uint)(SizeInBytes / 4); // Assume 4-byte elements
            var (cpu, gpu) = descriptorManager.CreateBufferUAV(_resource.Get(), numElements, 4);
            return gpu;
        }

        // For upload/readback buffers, cache the descriptor
        if (!_hasUAVDescriptor)
        {
            uint numElements = (uint)(SizeInBytes / 4);
            var (cpu, gpu) = descriptorManager.CreateBufferUAV(_resource.Get(), numElements, 4);
            _uavDescriptor = gpu;
            _hasUAVDescriptor = true;
        }

        return _uavDescriptor;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _resource.Dispose();
        _disposed = true;
    }
}
