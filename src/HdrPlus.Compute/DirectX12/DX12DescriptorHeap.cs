using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace HdrPlus.Compute.DirectX12;

/// <summary>
/// Manages descriptor heaps for DirectX 12 resources.
/// Descriptors are "views" that describe GPU resources to shaders.
/// </summary>
internal unsafe class DX12DescriptorHeap : IDisposable
{
    private readonly D3D12 _d3d12;
    private readonly ComPtr<ID3D12Device> _device;
    private ComPtr<ID3D12DescriptorHeap> _heap;
    private readonly DescriptorHeapType _type;
    private readonly uint _descriptorSize;
    private readonly uint _capacity;
    private uint _currentOffset;
    private bool _disposed;

    public DX12DescriptorHeap(D3D12 d3d12, ComPtr<ID3D12Device> device, DescriptorHeapType type, uint capacity, bool shaderVisible = true)
    {
        _d3d12 = d3d12;
        _device = device;
        _type = type;
        _capacity = capacity;
        _currentOffset = 0;

        // Get descriptor size (varies by hardware)
        _descriptorSize = device.Get()->GetDescriptorHandleIncrementSize(type);

        // Create descriptor heap
        var heapDesc = new DescriptorHeapDesc
        {
            Type = type,
            NumDescriptors = capacity,
            Flags = shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None,
            NodeMask = 0
        };

        ID3D12DescriptorHeap* heapPtr;
        device.Get()->CreateDescriptorHeap(&heapDesc, out heapPtr)
            .ThrowHResult($"Failed to create descriptor heap (type: {type})");

        _heap = new ComPtr<ID3D12DescriptorHeap>(heapPtr);
    }

    /// <summary>
    /// Allocates a descriptor from the heap and returns its CPU and GPU handles.
    /// </summary>
    public (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) Allocate()
    {
        if (_currentOffset >= _capacity)
        {
            throw new InvalidOperationException($"Descriptor heap is full (capacity: {_capacity})");
        }

        var cpuBase = _heap.Get()->GetCPUDescriptorHandleForHeapStart();
        var gpuBase = _heap.Get()->GetGPUDescriptorHandleForHeapStart();

        var cpu = new CpuDescriptorHandle
        {
            Ptr = cpuBase.Ptr + (_currentOffset * _descriptorSize)
        };

        var gpu = new GpuDescriptorHandle
        {
            Ptr = gpuBase.Ptr + (_currentOffset * _descriptorSize)
        };

        _currentOffset++;
        return (cpu, gpu);
    }

    /// <summary>
    /// Creates a UAV (Unordered Access View) for a buffer resource.
    /// </summary>
    public void CreateBufferUAV(ID3D12Resource* resource, uint numElements, uint elementSize, CpuDescriptorHandle handle)
    {
        var uavDesc = new UnorderedAccessViewDesc
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = UavDimension.Buffer
        };

        uavDesc.Anonymous.Buffer.FirstElement = 0;
        uavDesc.Anonymous.Buffer.NumElements = numElements;
        uavDesc.Anonymous.Buffer.StructureByteStride = elementSize;
        uavDesc.Anonymous.Buffer.CounterOffsetInBytes = 0;
        uavDesc.Anonymous.Buffer.Flags = BufferUavFlags.None;

        _device.Get()->CreateUnorderedAccessView(resource, null, &uavDesc, handle);
    }

    /// <summary>
    /// Creates a UAV for a 2D texture resource.
    /// </summary>
    public void CreateTexture2DUAV(ID3D12Resource* resource, Silk.NET.DXGI.Format format, CpuDescriptorHandle handle)
    {
        var uavDesc = new UnorderedAccessViewDesc
        {
            Format = format,
            ViewDimension = UavDimension.Texture2D
        };

        uavDesc.Anonymous.Texture2D.MipSlice = 0;
        uavDesc.Anonymous.Texture2D.PlaneSlice = 0;

        _device.Get()->CreateUnorderedAccessView(resource, null, &uavDesc, handle);
    }

    /// <summary>
    /// Creates a UAV for a 3D texture resource.
    /// </summary>
    public void CreateTexture3DUAV(ID3D12Resource* resource, Silk.NET.DXGI.Format format, CpuDescriptorHandle handle)
    {
        var uavDesc = new UnorderedAccessViewDesc
        {
            Format = format,
            ViewDimension = UavDimension.Texture3D
        };

        uavDesc.Anonymous.Texture3D.MipSlice = 0;
        uavDesc.Anonymous.Texture3D.FirstWSlice = 0;
        uavDesc.Anonymous.Texture3D.WSize = unchecked((uint)-1); // All slices

        _device.Get()->CreateUnorderedAccessView(resource, null, &uavDesc, handle);
    }

    /// <summary>
    /// Creates a CBV (Constant Buffer View) for a buffer resource.
    /// </summary>
    public void CreateConstantBufferView(ID3D12Resource* resource, uint sizeInBytes, CpuDescriptorHandle handle)
    {
        var cbvDesc = new ConstantBufferViewDesc
        {
            BufferLocation = resource->GetGPUVirtualAddress(),
            SizeInBytes = (sizeInBytes + 255) & ~255u // Must be 256-byte aligned
        };

        _device.Get()->CreateConstantBufferView(&cbvDesc, handle);
    }

    /// <summary>
    /// Resets the allocation offset (useful for per-frame heaps).
    /// </summary>
    public void Reset()
    {
        _currentOffset = 0;
    }

    public ComPtr<ID3D12DescriptorHeap> GetHeap() => _heap;
    public uint GetDescriptorSize() => _descriptorSize;
    public uint GetCapacity() => _capacity;
    public uint GetUsedCount() => _currentOffset;

    public void Dispose()
    {
        if (_disposed) return;
        _heap.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Manages multiple descriptor heaps and provides a simple allocation interface.
/// </summary>
internal unsafe class DX12DescriptorManager : IDisposable
{
    private readonly D3D12 _d3d12;
    private readonly ComPtr<ID3D12Device> _device;
    private DX12DescriptorHeap? _cbvSrvUavHeap;
    private DX12DescriptorHeap? _samplerHeap;
    private bool _disposed;

    // Descriptors per frame (reset each frame)
    private const uint CBV_SRV_UAV_HEAP_SIZE = 1024;
    private const uint SAMPLER_HEAP_SIZE = 16;

    public DX12DescriptorManager(D3D12 d3d12, ComPtr<ID3D12Device> device)
    {
        _d3d12 = d3d12;
        _device = device;

        // Create shader-visible heaps for CBV/SRV/UAV
        _cbvSrvUavHeap = new DX12DescriptorHeap(
            d3d12,
            device,
            DescriptorHeapType.CbvSrvUav,
            CBV_SRV_UAV_HEAP_SIZE,
            shaderVisible: true
        );

        // Create shader-visible heap for samplers
        _samplerHeap = new DX12DescriptorHeap(
            d3d12,
            device,
            DescriptorHeapType.Sampler,
            SAMPLER_HEAP_SIZE,
            shaderVisible: true
        );
    }

    /// <summary>
    /// Allocates a descriptor for a UAV resource.
    /// </summary>
    public (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) AllocateUAV()
    {
        return _cbvSrvUavHeap!.Allocate();
    }

    /// <summary>
    /// Allocates a descriptor for a CBV resource.
    /// </summary>
    public (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) AllocateCBV()
    {
        return _cbvSrvUavHeap!.Allocate();
    }

    /// <summary>
    /// Creates and allocates a UAV for a buffer.
    /// </summary>
    public (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) CreateBufferUAV(ID3D12Resource* resource, uint numElements, uint elementSize)
    {
        var (cpu, gpu) = AllocateUAV();
        _cbvSrvUavHeap!.CreateBufferUAV(resource, numElements, elementSize, cpu);
        return (cpu, gpu);
    }

    /// <summary>
    /// Creates and allocates a UAV for a 2D texture.
    /// </summary>
    public (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) CreateTexture2DUAV(ID3D12Resource* resource, Silk.NET.DXGI.Format format)
    {
        var (cpu, gpu) = AllocateUAV();
        _cbvSrvUavHeap!.CreateTexture2DUAV(resource, format, cpu);
        return (cpu, gpu);
    }

    /// <summary>
    /// Creates and allocates a UAV for a 3D texture.
    /// </summary>
    public (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) CreateTexture3DUAV(ID3D12Resource* resource, Silk.NET.DXGI.Format format)
    {
        var (cpu, gpu) = AllocateUAV();
        _cbvSrvUavHeap!.CreateTexture3DUAV(resource, format, cpu);
        return (cpu, gpu);
    }

    /// <summary>
    /// Creates and allocates a CBV for a constant buffer.
    /// </summary>
    public (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) CreateConstantBufferView(ID3D12Resource* resource, uint sizeInBytes)
    {
        var (cpu, gpu) = AllocateCBV();
        _cbvSrvUavHeap!.CreateConstantBufferView(resource, sizeInBytes, cpu);
        return (cpu, gpu);
    }

    /// <summary>
    /// Gets the CBV/SRV/UAV descriptor heap (for binding to command list).
    /// </summary>
    public ComPtr<ID3D12DescriptorHeap> GetCbvSrvUavHeap() => _cbvSrvUavHeap!.GetHeap();

    /// <summary>
    /// Gets the sampler descriptor heap (for binding to command list).
    /// </summary>
    public ComPtr<ID3D12DescriptorHeap> GetSamplerHeap() => _samplerHeap!.GetHeap();

    /// <summary>
    /// Resets all descriptor allocations (call at the beginning of each frame).
    /// </summary>
    public void ResetFrame()
    {
        _cbvSrvUavHeap?.Reset();
        _samplerHeap?.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cbvSrvUavHeap?.Dispose();
        _samplerHeap?.Dispose();
        _disposed = true;
    }
}
