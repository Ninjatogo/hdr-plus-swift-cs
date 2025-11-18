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
    private GpuDescriptorHandle _uavDescriptor;
    private bool _hasUAVDescriptor;
    private bool _disposed;
    private readonly Silk.NET.DXGI.Format _dxgiFormat;

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
        _dxgiFormat = ConvertFormat(format);

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
        int elementSize = Marshal.SizeOf<T>();
        int expectedElements = Width * Height * Depth;

        if (destination.Length < expectedElements)
        {
            throw new ArgumentException($"Destination buffer too small. Expected at least {expectedElements} elements.");
        }

        ulong dataSize = (ulong)(expectedElements * elementSize);

        // Get staging buffer from pool
        var stagingBuffer = _device.GetStagingPool().GetReadbackBuffer(dataSize);

        // Create a temporary command allocator and list for the copy
        ID3D12CommandAllocator* allocator;
        _device.GetDevice().Get()->CreateCommandAllocator(CommandListType.Direct, out allocator)
            .ThrowHResult("Failed to create command allocator");

        ID3D12GraphicsCommandList* cmdList;
        _device.GetDevice().Get()->CreateCommandList(0, CommandListType.Direct, allocator, null, out cmdList)
            .ThrowHResult("Failed to create command list");

        // Transition texture to copy source
        var barrier = new ResourceBarrier();
        barrier.Type = ResourceBarrierType.Transition;
        barrier.Flags = ResourceBarrierFlags.None;
        barrier.Anonymous.Transition.PResource = _resource.Get();
        barrier.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        barrier.Anonymous.Transition.StateBefore = ResourceStates.Common;
        barrier.Anonymous.Transition.StateAfter = ResourceStates.CopySource;
        cmdList->ResourceBarrier(1, &barrier);

        // Copy texture to staging buffer
        var srcLocation = new TextureCopyLocation
        {
            PResource = _resource.Get(),
            Type = TextureCopyType.SubresourceIndex
        };
        srcLocation.Anonymous.SubresourceIndex = 0;

        var dstLocation = new TextureCopyLocation
        {
            PResource = stagingBuffer.GetResource().Get(),
            Type = TextureCopyType.PlacedFootprint
        };

        // Calculate footprint
        var footprint = new PlacedSubresourceFootprint
        {
            Offset = 0,
            Footprint = new SubresourceFootprint
            {
                Format = _dxgiFormat,
                Width = (uint)Width,
                Height = (uint)Height,
                Depth = (uint)Depth,
                RowPitch = (uint)((Width * GetFormatBytes(_dxgiFormat) + 255) & ~255) // 256-byte aligned
            }
        };
        dstLocation.Anonymous.PlacedFootprint = footprint;

        cmdList->CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, null);

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

    public void WriteData<T>(ReadOnlySpan<T> source) where T : unmanaged
    {
        int elementSize = Marshal.SizeOf<T>();
        int expectedElements = Width * Height * Depth;

        if (source.Length < expectedElements)
        {
            throw new ArgumentException($"Source buffer too small. Expected at least {expectedElements} elements.");
        }

        ulong dataSize = (ulong)(expectedElements * elementSize);

        // Get staging buffer from pool
        var stagingBuffer = _device.GetStagingPool().GetUploadBuffer(dataSize);

        // Write to staging buffer
        stagingBuffer.WriteData(source);

        // Create a temporary command allocator and list for the copy
        ID3D12CommandAllocator* allocator;
        _device.GetDevice().Get()->CreateCommandAllocator(CommandListType.Direct, out allocator)
            .ThrowHResult("Failed to create command allocator");

        ID3D12GraphicsCommandList* cmdList;
        _device.GetDevice().Get()->CreateCommandList(0, CommandListType.Direct, allocator, null, out cmdList)
            .ThrowHResult("Failed to create command list");

        // Transition texture to copy dest
        var barrier = new ResourceBarrier();
        barrier.Type = ResourceBarrierType.Transition;
        barrier.Flags = ResourceBarrierFlags.None;
        barrier.Anonymous.Transition.PResource = _resource.Get();
        barrier.Anonymous.Transition.Subresource = 0xFFFFFFFF;
        barrier.Anonymous.Transition.StateBefore = ResourceStates.Common;
        barrier.Anonymous.Transition.StateAfter = ResourceStates.CopyDest;
        cmdList->ResourceBarrier(1, &barrier);

        // Copy staging buffer to texture
        var srcLocation = new TextureCopyLocation
        {
            PResource = stagingBuffer.GetResource().Get(),
            Type = TextureCopyType.PlacedFootprint
        };

        // Calculate footprint
        var footprint = new PlacedSubresourceFootprint
        {
            Offset = 0,
            Footprint = new SubresourceFootprint
            {
                Format = _dxgiFormat,
                Width = (uint)Width,
                Height = (uint)Height,
                Depth = (uint)Depth,
                RowPitch = (uint)((Width * GetFormatBytes(_dxgiFormat) + 255) & ~255) // 256-byte aligned
            }
        };
        srcLocation.Anonymous.PlacedFootprint = footprint;

        var dstLocation = new TextureCopyLocation
        {
            PResource = _resource.Get(),
            Type = TextureCopyType.SubresourceIndex
        };
        dstLocation.Anonymous.SubresourceIndex = 0;

        cmdList->CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, null);

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

    private static int GetFormatBytes(Silk.NET.DXGI.Format format)
    {
        return format switch
        {
            Silk.NET.DXGI.Format.FormatR16Float => 2,
            Silk.NET.DXGI.Format.FormatR32Float => 4,
            Silk.NET.DXGI.Format.FormatR16G16Sint => 4,
            Silk.NET.DXGI.Format.FormatR16G16B16A16Float => 8,
            Silk.NET.DXGI.Format.FormatR32G32B32A32Float => 16,
            _ => throw new NotSupportedException($"Format {format} bytes per pixel not defined")
        };
    }

    internal ComPtr<ID3D12Resource> GetResource() => _resource;

    /// <summary>
    /// Gets or creates a UAV descriptor for this texture.
    /// Always creates a fresh descriptor from the frame heap.
    /// </summary>
    internal GpuDescriptorHandle GetOrCreateUAVDescriptor(DX12DescriptorManager descriptorManager)
    {
        // Always create a fresh descriptor since the descriptor heap may be reset each frame
        if (Depth > 1)
        {
            var (cpu, gpu) = descriptorManager.CreateTexture3DUAV(_resource.Get(), _dxgiFormat);
            return gpu;
        }
        else
        {
            var (cpu, gpu) = descriptorManager.CreateTexture2DUAV(_resource.Get(), _dxgiFormat);
            return gpu;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _resource.Dispose();
        _disposed = true;
    }
}
