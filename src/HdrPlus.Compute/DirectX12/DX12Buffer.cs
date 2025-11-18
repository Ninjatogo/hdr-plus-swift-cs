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
            throw new NotImplementedException("Reading from default buffers requires staging buffer (TODO)");
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
            // Need to use upload heap
            throw new NotImplementedException("Writing to default buffers requires upload heap (TODO)");
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

    public void Dispose()
    {
        if (_disposed) return;
        _resource.Dispose();
        _disposed = true;
    }
}
