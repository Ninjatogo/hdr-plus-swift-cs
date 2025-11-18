using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.DirectX12;

internal unsafe class DX12Texture : IComputeTexture
{
    private readonly DX12ComputeDevice _device;
    private readonly D3D12 _d3d12;
    private ComPtr<ID3D12Resource> _resource;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public TextureFormat Format { get; }
    public string? Label { get; set; }

    public DX12Texture(DX12ComputeDevice device, D3D12 d3d12, ComPtr<ID3D12Device> d3dDevice,
        int width, int height, int depth, TextureFormat format, TextureUsage usage)
    {
        _device = device;
        _d3d12 = d3d12;
        Width = width;
        Height = height;
        Depth = depth;
        Format = format;

        var heapProps = new HeapProperties
        {
            Type = HeapType.Default,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 0,
            VisibleNodeMask = 0
        };

        var resourceDesc = new ResourceDesc
        {
            Dimension = depth > 1 ? ResourceDimension.Texture3D : ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)width,
            Height = (uint)height,
            DepthOrArraySize = (ushort)depth,
            MipLevels = 1,
            Format = ConvertFormat(format),
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.Unknown,
            Flags = ResourceFlags.AllowUnorderedAccess
        };

        ID3D12Resource* resourcePtr;
        d3dDevice.Get()->CreateCommittedResource(
            &heapProps,
            HeapFlags.None,
            &resourceDesc,
            ResourceStates.Common,
            null,
            out resourcePtr
        ).ThrowHResult("Failed to create texture");

        _resource = new ComPtr<ID3D12Resource>(resourcePtr);
    }

    private static Silk.NET.DXGI.Format ConvertFormat(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.R16_Float => Silk.NET.DXGI.Format.FormatR16Float,
            TextureFormat.R32_Float => Silk.NET.DXGI.Format.FormatR32Float,
            TextureFormat.RG16_SInt => Silk.NET.DXGI.Format.FormatR16G16Sint,
            TextureFormat.RGBA16_Float => Silk.NET.DXGI.Format.FormatR16G16B16A16Float,
            TextureFormat.RGBA32_Float => Silk.NET.DXGI.Format.FormatR32G32B32A32Float,
            _ => throw new NotSupportedException($"Texture format {format} not supported")
        };
    }

    public void ReadData<T>(Span<T> destination) where T : unmanaged
    {
        // Reading from GPU textures requires readback buffer
        throw new NotImplementedException("Texture readback not yet implemented (requires staging buffer)");
    }

    public void WriteData<T>(ReadOnlySpan<T> source) where T : unmanaged
    {
        // Writing to GPU textures requires upload buffer
        throw new NotImplementedException("Texture upload not yet implemented (requires staging buffer)");
    }

    internal ComPtr<ID3D12Resource> GetResource() => _resource;

    public void Dispose()
    {
        if (_disposed) return;
        _resource.Dispose();
        _disposed = true;
    }
}
