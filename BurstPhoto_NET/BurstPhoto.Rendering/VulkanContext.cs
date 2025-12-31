using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System;
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

    public VulkanContext()
    {
        Console.WriteLine("Initializing Vulkan...");
        Vk = Vk.GetApi();
        CreateInstance();
        PickPhysicalDevice();
        CreateLogicalDevice();
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

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = 0,
            PpEnabledExtensionNames = null,
            EnabledLayerCount = 0,
            PpEnabledLayerNames = null
        };

        if (Vk.CreateInstance(in createInfo, null, out var instance) != Result.Success)
        {
            throw new Exception("Failed to create Vulkan instance");
        }
        Instance = instance;

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
    }

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        Vk.EnumeratePhysicalDevices(Instance, &deviceCount, null);
        if (deviceCount == 0) throw new Exception("No Vulkan devices found");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = devices)
        {
            Vk.EnumeratePhysicalDevices(Instance, &deviceCount, pDevices);
        }

        // Pick first discrete, or first available
        PhysicalDevice = devices[0];
        for (int i = 0; i < deviceCount; i++)
        {
            Vk.GetPhysicalDeviceProperties(devices[i], out var props);
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                PhysicalDevice = devices[i];
                break;
            }
        }
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

        int i = 0;
        bool found = false;
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

        float queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = ComputeQueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        var deviceCreateInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PQueueCreateInfos = &queueCreateInfo,
            QueueCreateInfoCount = 1,
            EnabledExtensionCount = 0,
            EnabledLayerCount = 0
        };

        if (Vk.CreateDevice(PhysicalDevice, in deviceCreateInfo, null, out var device) != Result.Success)
        {
            throw new Exception("Failed to create logical device");
        }
        Device = device;

        Vk.GetDeviceQueue(Device, ComputeQueueFamilyIndex, 0, out var queue);
        ComputeQueue = queue;
    }

    public void Dispose()
    {
        Vk.DestroyDevice(Device, null);
        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
    }
}
