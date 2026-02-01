using Silk.NET.Core;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering;

public unsafe class VulkanContext : IDisposable
{
    public Vk Vk { get; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public Queue ComputeQueue { get; private set; }
    public uint ComputeQueueFamilyIndex { get; private set; }

    public CommandPool CommandPool { get; private set; }

    public bool SupportsStorageImageWriteWithoutFormat { get; private set; }

    public VulkanContext(int? preferredDeviceIndex = null)
    {
        Console.WriteLine("Initializing Vulkan...");
        Vk = Vk.GetApi();
        CreateInstance();
        PickPhysicalDevice(preferredDeviceIndex);
        CreateLogicalDevice();
        CreateCommandPool();
        Console.WriteLine("Vulkan Initialized.");
    }

    private void CreateInstance()
    {
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("BurstPhoto"),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("No Engine"),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12
        };

        // Try to enable validation layers for debugging
        string[] validationLayers = ["VK_LAYER_KHRONOS_validation"];
        var enableValidation = false;

        // Check if validation layer is available
        uint layerCount = 0;
        Vk.EnumerateInstanceLayerProperties(&layerCount, null);
        if (layerCount > 0)
        {
            var availableLayers = new LayerProperties[layerCount];
            fixed (LayerProperties* pLayers = availableLayers)
            {
                Vk.EnumerateInstanceLayerProperties(&layerCount, pLayers);
            }

            foreach (var layer in availableLayers)
            {
                var layerName = System.Text.Encoding.UTF8.GetString(layer.LayerName, 256).TrimEnd('\0');
                if (layerName == "VK_LAYER_KHRONOS_validation")
                {
                    enableValidation = true;
                    Console.WriteLine("Vulkan validation layer available - enabling for debug");
                    break;
                }
            }
        }

        InstanceCreateInfo createInfo;
        var layerNamePtr = IntPtr.Zero;
        byte** ppLayerNames = null;

        if (enableValidation)
        {
            layerNamePtr = Marshal.StringToHGlobalAnsi("VK_LAYER_KHRONOS_validation");
            ppLayerNames = (byte**)Marshal.AllocHGlobal(IntPtr.Size);
            *ppLayerNames = (byte*)layerNamePtr;

            createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = 0,
                PpEnabledExtensionNames = null,
                EnabledLayerCount = 1,
                PpEnabledLayerNames = ppLayerNames
            };
        }
        else
        {
            createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = 0,
                PpEnabledExtensionNames = null,
                EnabledLayerCount = 0,
                PpEnabledLayerNames = null
            };
        }

        if (Vk.CreateInstance(in createInfo, null, out var instance) != Result.Success)
        {
            throw new Exception("Failed to create Vulkan instance");
        }
        Instance = instance;

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        if (layerNamePtr != IntPtr.Zero) Marshal.FreeHGlobal(layerNamePtr);
        if (ppLayerNames != null) Marshal.FreeHGlobal((IntPtr)ppLayerNames);
    }

    private void PickPhysicalDevice(int? preferredDeviceIndex)
    {
        uint deviceCount = 0;
        Vk.EnumeratePhysicalDevices(Instance, &deviceCount, null);
        if (deviceCount == 0) throw new Exception("No Vulkan devices found");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = devices)
        {
            Vk.EnumeratePhysicalDevices(Instance, &deviceCount, pDevices);
        }

        Console.WriteLine($"\n=== Available Vulkan Devices ({deviceCount}) ===");

        // Display all available devices
        for (var i = 0; i < deviceCount; i++)
        {
            Vk.GetPhysicalDeviceProperties(devices[i], out var props);
            var deviceName = System.Text.Encoding.UTF8.GetString(props.DeviceName, 256).TrimEnd('\0');
            var deviceType = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => "Discrete GPU",
                PhysicalDeviceType.IntegratedGpu => "Integrated GPU",
                PhysicalDeviceType.VirtualGpu => "Virtual GPU",
                PhysicalDeviceType.Cpu => "CPU",
                _ => "Other"
            };

            Console.WriteLine($"  [{i}] {deviceName}");
            Console.WriteLine($"      Type: {deviceType}");
            Console.WriteLine($"      Vendor ID: 0x{props.VendorID:X} (Device ID: 0x{props.DeviceID:X})");
            Console.WriteLine($"      API Version: {props.ApiVersion >> 22}.{(props.ApiVersion >> 12) & 0x3FF}.{props.ApiVersion & 0xFFF}");
            Console.WriteLine($"      Driver Version: {props.DriverVersion}");
        }

        // Select device based on preference or automatic selection
        int selectedIndex;
        if (preferredDeviceIndex.HasValue)
        {
            if (preferredDeviceIndex.Value < 0 || preferredDeviceIndex.Value >= deviceCount)
            {
                throw new Exception($"Invalid device index {preferredDeviceIndex.Value}. Valid range: 0-{deviceCount - 1}");
            }
            selectedIndex = preferredDeviceIndex.Value;
            Console.WriteLine($"\n✓ Using user-specified device [{selectedIndex}]");
        }
        else
        {
            // Automatic selection: prefer discrete GPU, otherwise use first device
            selectedIndex = 0;
            for (var i = 0; i < deviceCount; i++)
            {
                Vk.GetPhysicalDeviceProperties(devices[i], out var props);
                if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
                {
                    selectedIndex = i;
                    break;
                }
            }
            Console.WriteLine($"\n✓ Auto-selected device [{selectedIndex}] (discrete GPU preferred)");
        }

        PhysicalDevice = devices[selectedIndex];

        // Display selected device info
        Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var selectedProps);
        var selectedName = System.Text.Encoding.UTF8.GetString(selectedProps.DeviceName, 256).TrimEnd('\0');
        Console.WriteLine($"   Selected: {selectedName}\n");
    }

    private void CreateLogicalDevice()
    {
        // Find compute queue
        uint queueFamilyCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, null);
        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, pQueueFamilies);
        }

        var i = 0;
        var found = false;
        foreach (var queueFamily in queueFamilies)
        {
            if ((queueFamily.QueueFlags & QueueFlags.ComputeBit) != 0)
            {
                ComputeQueueFamilyIndex = (uint)i;
                found = true;
                break;
            }
            i++;
        }

        if (!found) throw new Exception("No compute queue family found");

        var queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = ComputeQueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        // Query supported features first
        PhysicalDeviceFeatures supportedFeatures;
        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, &supportedFeatures);

        // Enable required features for storage image writes without format specifier
        // This is CRITICAL for RWTexture2D<float4> writes in HLSL/Vulkan compute shaders
        var enabledFeatures = new PhysicalDeviceFeatures
        {
            ShaderStorageImageWriteWithoutFormat = true
        };

        // Verify the feature is actually supported
        SupportsStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat;

        if (!SupportsStorageImageWriteWithoutFormat)
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════");
            Console.WriteLine("║ ⚠️  FEATURE NOT SUPPORTED");
            Console.WriteLine("╠════════════════════════════════════════════════════════════════");
            Console.WriteLine("║ ShaderStorageImageWriteWithoutFormat is NOT supported!");
            Console.WriteLine("║");
            Console.WriteLine("║ Impact:");
            Console.WriteLine("║   ✅ 'Fast' (Spatial) algorithm: WILL WORK");
            Console.WriteLine("║   ❌ 'HigherQuality' (Frequency) algorithm: WILL NOT WORK");
            Console.WriteLine("║");
            Console.WriteLine("║ Recommended action:");
            Console.WriteLine("║   Use --algorithm Fast for processing on this device");
            Console.WriteLine("║");
            Console.WriteLine("║ Advanced options:");
            Console.WriteLine("║   1. Update GPU drivers to the latest version");
            Console.WriteLine("║   2. Use --gpu <index> to try a different GPU");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════\n");

            // Don't enable the feature if not supported - it would be silently ignored anyway
            enabledFeatures.ShaderStorageImageWriteWithoutFormat = false;
        }
        else
        {
            Console.WriteLine("✓ ShaderStorageImageWriteWithoutFormat feature is supported and enabled");
        }

        // Validate R32G32B32A32Sfloat format supports storage image operations
        // This is CRITICAL for RWTexture2D<float4> in frequency domain shaders
        Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, Format.R32G32B32A32Sfloat, out var formatProps);
        var supportsStorage = (formatProps.OptimalTilingFeatures & FormatFeatureFlags.StorageImageBit) != 0;
        Console.WriteLine($"R32G32B32A32Sfloat format properties:");
        Console.WriteLine($"  Optimal tiling features: {formatProps.OptimalTilingFeatures}");
        Console.WriteLine($"  Supports storage images: {(supportsStorage ? "YES" : "NO")}");

        if (!supportsStorage)
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════");
            Console.WriteLine("║ ⚠️  FORMAT NOT SUPPORTED FOR STORAGE IMAGES");
            Console.WriteLine("╠════════════════════════════════════════════════════════════════");
            Console.WriteLine("║ R32G32B32A32Sfloat does not support StorageImageBit!");
            Console.WriteLine("║ Frequency domain processing WILL FAIL.");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════\n");
        }

        var deviceCreateInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PQueueCreateInfos = &queueCreateInfo,
            QueueCreateInfoCount = 1,
            EnabledExtensionCount = 0,
            EnabledLayerCount = 0,
            PEnabledFeatures = &enabledFeatures
        };

        if (Vk.CreateDevice(PhysicalDevice, in deviceCreateInfo, null, out var device) != Result.Success)
        {
            throw new Exception("Failed to create logical device");
        }
        Device = device;

        Vk.GetDeviceQueue(Device, ComputeQueueFamilyIndex, 0, out var queue);
        ComputeQueue = queue;
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = ComputeQueueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };

        if (Vk.CreateCommandPool(Device, in poolInfo, null, out var pool) != Result.Success)
        {
            throw new Exception("Failed to create command pool");
        }
        CommandPool = pool;
    }

    public CommandBuffer BeginSingleTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = CommandPool,
            CommandBufferCount = 1
        };

        Vk.AllocateCommandBuffers(Device, in allocInfo, out var commandBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        Vk.BeginCommandBuffer(commandBuffer, in beginInfo);

        return commandBuffer;
    }

    public void EndSingleTimeCommands(CommandBuffer commandBuffer)
    {
        Vk.EndCommandBuffer(commandBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };

        Vk.QueueSubmit(ComputeQueue, 1, in submitInfo, default);
        Vk.QueueWaitIdle(ComputeQueue);

        Vk.FreeCommandBuffers(Device, CommandPool, 1, in commandBuffer);
    }

    public void Dispose()
    {
        Vk.DestroyCommandPool(Device, CommandPool, null);
        Vk.DestroyDevice(Device, null);
        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
    }
}
