using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.DirectX12;

/// <summary>
/// Manages staging buffers for CPU-GPU data transfers in DirectX 12.
/// Staging buffers are temporary upload/readback heaps used to transfer data
/// to/from default (GPU-only) resources.
/// </summary>
internal unsafe class DX12StagingBuffer : IDisposable
{
    private readonly D3D12 _d3d12;
    private ComPtr<ID3D12Resource> _resource;
    private readonly bool _isUpload; // true = upload (CPU->GPU), false = readback (GPU->CPU)
    private readonly ulong _size;
    private bool _disposed;

    public DX12StagingBuffer(D3D12 d3d12, ComPtr<ID3D12Device> device, ulong size, bool isUpload)
    {
        _d3d12 = d3d12;
        _size = size;
        _isUpload = isUpload;

        var heapProps = new HeapProperties
        {
            Type = isUpload ? HeapType.Upload : HeapType.Readback,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 0,
            VisibleNodeMask = 0
        };

        var resourceDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = size,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            SampleDesc = new Silk.NET.DXGI.SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.RowMajor,
            Flags = ResourceFlags.None
        };

        ID3D12Resource* resourcePtr;
        var initialState = isUpload ? ResourceStates.GenericRead : ResourceStates.CopyDest;

        device.Get()->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &resourceDesc,
            initialState,
            null,
            out resourcePtr
        ).ThrowHResult($"Failed to create {(isUpload ? "upload" : "readback")} staging buffer");

        _resource = new ComPtr<ID3D12Resource>(resourcePtr);
    }

    public ComPtr<ID3D12Resource> GetResource() => _resource;
    public ulong GetSize() => _size;

    /// <summary>
    /// Maps the staging buffer for CPU access and copies data to it.
    /// Only valid for upload staging buffers.
    /// </summary>
    public void WriteData<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!_isUpload)
        {
            throw new InvalidOperationException("Cannot write to readback staging buffer");
        }

        ulong dataSize = (ulong)(data.Length * Marshal.SizeOf<T>());
        if (dataSize > _size)
        {
            throw new ArgumentException($"Data size ({dataSize}) exceeds staging buffer size ({_size})");
        }

        void* mappedData;
        _resource.Get()->Map(0, null, &mappedData).ThrowHResult("Failed to map staging buffer");
        data.CopyTo(new Span<T>(mappedData, data.Length));
        _resource.Get()->Unmap(0, null);
    }

    /// <summary>
    /// Maps the staging buffer for CPU access and reads data from it.
    /// Only valid for readback staging buffers.
    /// </summary>
    public void ReadData<T>(Span<T> destination) where T : unmanaged
    {
        if (_isUpload)
        {
            throw new InvalidOperationException("Cannot read from upload staging buffer");
        }

        ulong dataSize = (ulong)(destination.Length * Marshal.SizeOf<T>());
        if (dataSize > _size)
        {
            throw new ArgumentException($"Data size ({dataSize}) exceeds staging buffer size ({_size})");
        }

        void* mappedData;
        _resource.Get()->Map(0, null, &mappedData).ThrowHResult("Failed to map staging buffer");
        new Span<T>(mappedData, destination.Length).CopyTo(destination);
        _resource.Get()->Unmap(0, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _resource.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Manages a pool of staging buffers to avoid frequent allocations.
/// </summary>
internal unsafe class DX12StagingBufferPool : IDisposable
{
    private readonly D3D12 _d3d12;
    private readonly ComPtr<ID3D12Device> _device;
    private readonly List<DX12StagingBuffer> _uploadBuffers;
    private readonly List<DX12StagingBuffer> _readbackBuffers;
    private readonly ulong _defaultBufferSize;
    private bool _disposed;

    public DX12StagingBufferPool(D3D12 d3d12, ComPtr<ID3D12Device> device, ulong defaultBufferSize = 64 * 1024 * 1024) // 64 MB default
    {
        _d3d12 = d3d12;
        _device = device;
        _defaultBufferSize = defaultBufferSize;
        _uploadBuffers = new List<DX12StagingBuffer>();
        _readbackBuffers = new List<DX12StagingBuffer>();
    }

    /// <summary>
    /// Gets or creates an upload staging buffer with at least the specified size.
    /// </summary>
    public DX12StagingBuffer GetUploadBuffer(ulong minSize)
    {
        // Try to find an existing buffer that's large enough
        foreach (var buffer in _uploadBuffers)
        {
            if (buffer.GetSize() >= minSize)
            {
                return buffer;
            }
        }

        // Create a new buffer
        ulong size = Math.Max(minSize, _defaultBufferSize);
        var newBuffer = new DX12StagingBuffer(_d3d12, _device, size, isUpload: true);
        _uploadBuffers.Add(newBuffer);
        return newBuffer;
    }

    /// <summary>
    /// Gets or creates a readback staging buffer with at least the specified size.
    /// </summary>
    public DX12StagingBuffer GetReadbackBuffer(ulong minSize)
    {
        // Try to find an existing buffer that's large enough
        foreach (var buffer in _readbackBuffers)
        {
            if (buffer.GetSize() >= minSize)
            {
                return buffer;
            }
        }

        // Create a new buffer
        ulong size = Math.Max(minSize, _defaultBufferSize);
        var newBuffer = new DX12StagingBuffer(_d3d12, _device, size, isUpload: false);
        _readbackBuffers.Add(newBuffer);
        return newBuffer;
    }

    /// <summary>
    /// Clears all cached staging buffers (useful for memory management).
    /// </summary>
    public void Clear()
    {
        foreach (var buffer in _uploadBuffers)
        {
            buffer.Dispose();
        }
        _uploadBuffers.Clear();

        foreach (var buffer in _readbackBuffers)
        {
            buffer.Dispose();
        }
        _readbackBuffers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }
}
