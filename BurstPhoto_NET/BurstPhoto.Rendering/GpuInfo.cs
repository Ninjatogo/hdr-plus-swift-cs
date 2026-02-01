using Silk.NET.Core;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering;

/// <summary>
/// Information about an available GPU device.
/// </summary>
public record GpuInfo(int Index, string Name, string Type, uint VendorId, uint DeviceId);

/// <summary>
/// Utility class to enumerate available Vulkan GPUs without creating a full context.
/// </summary>
public static class GpuEnumerator
{
    /// <summary>
    /// Enumerates all available Vulkan GPU devices.
    /// </summary>
    /// <returns>List of available GPUs, or empty list if Vulkan is not available.</returns>
    public static unsafe List<GpuInfo> EnumerateGpus()
    {
        var gpus = new List<GpuInfo>();

        try
        {
            using var vk = Vk.GetApi();

            // Create a minimal instance just for enumeration
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("BurstPhoto GPU Enum"),
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

            if (vk.CreateInstance(in createInfo, null, out var instance) != Result.Success)
            {
                Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
                Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
                return gpus;
            }

            try
            {
                uint deviceCount = 0;
                vk.EnumeratePhysicalDevices(instance, &deviceCount, null);

                if (deviceCount == 0)
                    return gpus;

                var devices = new PhysicalDevice[deviceCount];
                fixed (PhysicalDevice* pDevices = devices)
                {
                    vk.EnumeratePhysicalDevices(instance, &deviceCount, pDevices);
                }

                for (var i = 0; i < deviceCount; i++)
                {
                    vk.GetPhysicalDeviceProperties(devices[i], out var props);
                    var deviceName = System.Text.Encoding.UTF8.GetString(props.DeviceName, 256).TrimEnd('\0');
                    var deviceType = props.DeviceType switch
                    {
                        PhysicalDeviceType.DiscreteGpu => "Discrete GPU",
                        PhysicalDeviceType.IntegratedGpu => "Integrated GPU",
                        PhysicalDeviceType.VirtualGpu => "Virtual GPU",
                        PhysicalDeviceType.Cpu => "CPU",
                        _ => "Other"
                    };

                    gpus.Add(new GpuInfo(i, deviceName, deviceType, props.VendorID, props.DeviceID));
                }
            }
            finally
            {
                vk.DestroyInstance(instance, null);
                Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
                Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
            }
        }
        catch
        {
            // Vulkan not available - return empty list
        }

        return gpus;
    }
}
