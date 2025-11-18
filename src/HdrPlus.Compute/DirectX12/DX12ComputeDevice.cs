using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.DirectX12;

/// <summary>
/// DirectX 12 implementation of compute device for Windows.
/// Provides GPU compute capabilities using DX12 compute shaders.
/// </summary>
public unsafe class DX12ComputeDevice : IComputeDevice
{
    private readonly D3D12 _d3d12;
    private readonly Dxgi _dxgi;
    private ComPtr<ID3D12Device> _device;
    private ComPtr<ID3D12CommandQueue> _commandQueue;
    private ComPtr<ID3D12Fence> _fence;
    private ulong _fenceValue;
    private IntPtr _fenceEvent;
    private bool _disposed;

    public string DeviceName { get; private set; }
    public ComputeBackend Backend => ComputeBackend.DirectX12;

    public DX12ComputeDevice()
    {
        _d3d12 = D3D12.GetApi();
        _dxgi = Dxgi.GetApi();

        // Enable debug layer in debug builds
#if DEBUG
        EnableDebugLayer();
#endif

        // Create DXGI factory
        ComPtr<IDXGIFactory4> factory = default;
        _dxgi.CreateDXGIFactory2(0, out factory).ThrowHResult("Failed to create DXGI factory");

        // Find the best adapter (GPU)
        ComPtr<IDXGIAdapter1> adapter = default;
        uint adapterIndex = 0;
        while (_dxgi.EnumAdapters1(factory, adapterIndex++, ref adapter) != Dxgi.ErrorNotFound)
        {
            AdapterDesc1 desc;
            adapter.Get()->GetDesc1(&desc).ThrowHResult();

            // Skip software adapters
            if ((desc.Flags & (uint)AdapterFlag.Software) != 0)
            {
                continue;
            }

            // Try to create device with this adapter
            ID3D12Device* devicePtr = null;
            var hr = _d3d12.CreateDevice((IUnknown*)adapter.Get(), D3DFeatureLevel.Level120, out devicePtr);

            if (hr == 0) // S_OK
            {
                _device = new ComPtr<ID3D12Device>(devicePtr);
                DeviceName = Marshal.PtrToStringUni((IntPtr)desc.Description) ?? "Unknown GPU";
                break;
            }
        }

        if (_device.Handle == null)
        {
            throw new Exception("Failed to create DirectX 12 device. No compatible GPU found.");
        }

        // Create command queue for compute
        var queueDesc = new CommandQueueDesc
        {
            Type = CommandListType.Compute,
            Priority = 0,
            Flags = CommandQueueFlags.None,
            NodeMask = 0
        };

        ID3D12CommandQueue* queuePtr;
        _d3d12.CreateCommandQueue(_device.Get(), &queueDesc, out queuePtr).ThrowHResult("Failed to create command queue");
        _commandQueue = new ComPtr<ID3D12CommandQueue>(queuePtr);

        // Create fence for synchronization
        ID3D12Fence* fencePtr;
        _d3d12.CreateFence(_device.Get(), 0, FenceFlags.None, out fencePtr).ThrowHResult("Failed to create fence");
        _fence = new ComPtr<ID3D12Fence>(fencePtr);
        _fenceValue = 1;

        // Create event for CPU-GPU sync
        _fenceEvent = PlatformMethods.CreateEventW(null, false, false, null);
        if (_fenceEvent == IntPtr.Zero)
        {
            throw new Exception("Failed to create fence event");
        }
    }

#if DEBUG
    private void EnableDebugLayer()
    {
        ID3D12Debug* debug;
        if (_d3d12.GetDebugInterface(out debug) == 0)
        {
            debug->EnableDebugLayer();
            debug->Release();
        }
    }
#endif

    public IComputeBuffer CreateBuffer<T>(ReadOnlySpan<T> data, BufferUsage usage = BufferUsage.Default) where T : unmanaged
    {
        int sizeInBytes = data.Length * Marshal.SizeOf<T>();
        var buffer = new DX12Buffer(this, _d3d12, _device, sizeInBytes, usage);

        if (data.Length > 0)
        {
            buffer.WriteData(data);
        }

        return buffer;
    }

    public IComputeBuffer CreateBuffer(int sizeInBytes, BufferUsage usage = BufferUsage.Default)
    {
        return new DX12Buffer(this, _d3d12, _device, sizeInBytes, usage);
    }

    public IComputeTexture CreateTexture2D(int width, int height, TextureFormat format, TextureUsage usage = TextureUsage.Default)
    {
        return new DX12Texture(this, _d3d12, _device, width, height, 1, format, usage);
    }

    public IComputeTexture CreateTexture3D(int width, int height, int depth, TextureFormat format, TextureUsage usage = TextureUsage.Default)
    {
        return new DX12Texture(this, _d3d12, _device, width, height, depth, format, usage);
    }

    public IComputePipeline CreatePipeline(string shaderName, string entryPoint = "main")
    {
        return new DX12Pipeline(this, _d3d12, _device, shaderName, entryPoint);
    }

    public IComputeCommandBuffer CreateCommandBuffer()
    {
        return new DX12CommandBuffer(this, _d3d12, _device, _commandQueue);
    }

    public void Submit(IComputeCommandBuffer commandBuffer)
    {
        if (commandBuffer is not DX12CommandBuffer dx12CmdBuffer)
        {
            throw new ArgumentException("Command buffer must be a DirectX 12 command buffer");
        }

        dx12CmdBuffer.Execute();
    }

    public void WaitIdle()
    {
        // Signal fence
        _commandQueue.Get()->Signal(_fence.Get(), _fenceValue).ThrowHResult();

        // Wait for fence
        if (_fence.Get()->GetCompletedValue() < _fenceValue)
        {
            _fence.Get()->SetEventOnCompletion(_fenceValue, _fenceEvent).ThrowHResult();
            PlatformMethods.WaitForSingleObject(_fenceEvent, 0xFFFFFFFF); // INFINITE
        }

        _fenceValue++;
    }

    internal ComPtr<ID3D12Device> GetDevice() => _device;
    internal ComPtr<ID3D12CommandQueue> GetCommandQueue() => _commandQueue;

    public void Dispose()
    {
        if (_disposed) return;

        WaitIdle();

        if (_fenceEvent != IntPtr.Zero)
        {
            PlatformMethods.CloseHandle(_fenceEvent);
        }

        _fence.Dispose();
        _commandQueue.Dispose();
        _device.Dispose();

        _disposed = true;
    }
}

// Helper extension for HRESULT
internal static class HResultExtensions
{
    public static void ThrowHResult(this int hr, string? message = null)
    {
        if (hr != 0)
        {
            throw new Exception($"{message ?? "DirectX 12 operation failed"} (HRESULT: 0x{hr:X8})");
        }
    }
}

// Platform methods for Win32 APIs
internal static class PlatformMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(void* lpEventAttributes, bool bManualReset, bool bInitialState, void* lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
