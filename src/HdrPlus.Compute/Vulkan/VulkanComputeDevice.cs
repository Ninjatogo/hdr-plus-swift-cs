using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Runtime.InteropServices;

namespace HdrPlus.Compute.Vulkan;

/// <summary>
/// Vulkan implementation of compute device for cross-platform support (Windows/Linux/macOS).
/// Provides GPU compute capabilities using Vulkan compute shaders.
/// </summary>
public unsafe class VulkanComputeDevice : IComputeDevice
{
    private readonly Vk _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _computeQueue;
    private uint _queueFamilyIndex;
    private CommandPool _commandPool;
    private VulkanDescriptorManager? _descriptorManager;
    private VulkanMemoryAllocator? _memoryAllocator;
    private bool _disposed;

    public string DeviceName { get; private set; }
    public ComputeBackend Backend => ComputeBackend.Vulkan;

    public VulkanComputeDevice()
    {
        _vk = Vk.GetApi();
        DeviceName = "Unknown GPU";

        CreateInstance();
        SelectPhysicalDevice();
        CreateLogicalDevice();
        CreateCommandPool();

        // Initialize managers
        _descriptorManager = new VulkanDescriptorManager(_vk, _device);
        _memoryAllocator = new VulkanMemoryAllocator(_vk, _instance, _physicalDevice, _device);
    }

    private void CreateInstance()
    {
        // Application info
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("HDR+ Compute"),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("HdrPlus.Compute"),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version12
        };

        // Instance create info
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo
        };

        // Enable validation layers in debug builds
#if DEBUG
        var layers = new[] { "VK_LAYER_KHRONOS_validation" };
        var layerNames = stackalloc byte*[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            layerNames[i] = (byte*)Marshal.StringToHGlobalAnsi(layers[i]);
        }
        createInfo.EnabledLayerCount = (uint)layers.Length;
        createInfo.PpEnabledLayerNames = layerNames;
#endif

        fixed (Instance* instancePtr = &_instance)
        {
            if (_vk.CreateInstance(&createInfo, null, instancePtr) != Result.Success)
            {
                throw new Exception("Failed to create Vulkan instance");
            }
        }

        // Free allocated strings
        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
#if DEBUG
        for (int i = 0; i < layers.Length; i++)
        {
            Marshal.FreeHGlobal((IntPtr)layerNames[i]);
        }
#endif
    }

    private void SelectPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, null);

        if (deviceCount == 0)
        {
            throw new Exception("No Vulkan-compatible GPU found");
        }

        var devices = stackalloc PhysicalDevice[(int)deviceCount];
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devices);

        // Select the first discrete GPU, or fallback to any GPU
        for (int i = 0; i < deviceCount; i++)
        {
            PhysicalDeviceProperties props;
            _vk.GetPhysicalDeviceProperties(devices[i], &props);

            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                _physicalDevice = devices[i];
                DeviceName = Marshal.PtrToStringAnsi((IntPtr)props.DeviceName) ?? "Unknown GPU";
                break;
            }
        }

        // If no discrete GPU found, use the first available
        if (_physicalDevice.Handle == 0)
        {
            _physicalDevice = devices[0];
            PhysicalDeviceProperties props;
            _vk.GetPhysicalDeviceProperties(_physicalDevice, &props);
            DeviceName = Marshal.PtrToStringAnsi((IntPtr)props.DeviceName) ?? "Unknown GPU";
        }
    }

    private void CreateLogicalDevice()
    {
        // Find compute queue family
        uint queueFamilyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, null);

        var queueFamilies = stackalloc QueueFamilyProperties[(int)queueFamilyCount];
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, queueFamilies);

        _queueFamilyIndex = uint.MaxValue;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if ((queueFamilies[i].QueueFlags & QueueFlags.ComputeBit) != 0)
            {
                _queueFamilyIndex = i;
                break;
            }
        }

        if (_queueFamilyIndex == uint.MaxValue)
        {
            throw new Exception("No compute queue family found");
        }

        // Create device
        float queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        var deviceCreateInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo
        };

        fixed (Device* devicePtr = &_device)
        {
            if (_vk.CreateDevice(_physicalDevice, &deviceCreateInfo, null, devicePtr) != Result.Success)
            {
                throw new Exception("Failed to create Vulkan device");
            }
        }

        // Get compute queue
        fixed (Queue* queuePtr = &_computeQueue)
        {
            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, queuePtr);
        }
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };

        fixed (CommandPool* poolPtr = &_commandPool)
        {
            if (_vk.CreateCommandPool(_device, &poolInfo, null, poolPtr) != Result.Success)
            {
                throw new Exception("Failed to create command pool");
            }
        }
    }

    public IComputeBuffer CreateBuffer<T>(ReadOnlySpan<T> data, BufferUsage usage = BufferUsage.Default) where T : unmanaged
    {
        int sizeInBytes = data.Length * Marshal.SizeOf<T>();
        var buffer = new VulkanBuffer(this, _vk, _device, _memoryAllocator!, sizeInBytes, usage);

        if (data.Length > 0)
        {
            buffer.WriteData(data);
        }

        return buffer;
    }

    public IComputeBuffer CreateBuffer(int sizeInBytes, BufferUsage usage = BufferUsage.Default)
    {
        return new VulkanBuffer(this, _vk, _device, _memoryAllocator!, sizeInBytes, usage);
    }

    public IComputeTexture CreateTexture2D(int width, int height, TextureFormat format, TextureUsage usage = TextureUsage.Default)
    {
        return new VulkanTexture(this, _vk, _device, _memoryAllocator!, width, height, 1, format, usage);
    }

    public IComputeTexture CreateTexture3D(int width, int height, int depth, TextureFormat format, TextureUsage usage = TextureUsage.Default)
    {
        return new VulkanTexture(this, _vk, _device, _memoryAllocator!, width, height, depth, format, usage);
    }

    public IComputePipeline CreatePipeline(string shaderName, string entryPoint = "main")
    {
        return new VulkanPipeline(this, _vk, _device, shaderName, entryPoint);
    }

    public IComputeCommandBuffer CreateCommandBuffer()
    {
        return new VulkanCommandBuffer(this, _vk, _device, _commandPool, _computeQueue, _descriptorManager!);
    }

    public void Submit(IComputeCommandBuffer commandBuffer)
    {
        if (commandBuffer is not VulkanCommandBuffer vkCmdBuffer)
        {
            throw new ArgumentException("Command buffer must be a Vulkan command buffer");
        }

        vkCmdBuffer.Execute();
    }

    public void WaitIdle()
    {
        _vk.DeviceWaitIdle(_device);
    }

    internal Vk GetVk() => _vk;
    internal Instance GetInstance() => _instance;
    internal PhysicalDevice GetPhysicalDevice() => _physicalDevice;
    internal Device GetDevice() => _device;
    internal Queue GetComputeQueue() => _computeQueue;
    internal uint GetQueueFamilyIndex() => _queueFamilyIndex;
    internal CommandPool GetCommandPool() => _commandPool;
    internal VulkanDescriptorManager GetDescriptorManager() => _descriptorManager!;
    internal VulkanMemoryAllocator GetMemoryAllocator() => _memoryAllocator!;

    public void Dispose()
    {
        if (_disposed) return;

        WaitIdle();

        _descriptorManager?.Dispose();
        _memoryAllocator?.Dispose();

        if (_commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
        }

        if (_device.Handle != 0)
        {
            _vk.DestroyDevice(_device, null);
        }

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
        }

        _disposed = true;
    }
}
