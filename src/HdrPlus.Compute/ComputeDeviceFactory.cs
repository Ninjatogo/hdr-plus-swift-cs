namespace HdrPlus.Compute;

/// <summary>
/// Factory for creating platform-appropriate compute devices.
/// </summary>
public static class ComputeDeviceFactory
{
    /// <summary>
    /// Creates the best available compute device for the current platform.
    /// Windows: DirectX 12 (native) or Vulkan
    /// Linux: Vulkan
    /// macOS: Vulkan (via MoltenVK) or Metal
    /// </summary>
    public static IComputeDevice CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            // Prefer DirectX 12 on Windows for best performance
            try
            {
                return CreateDirectX12();
            }
            catch
            {
                // Fall back to Vulkan if DirectX 12 is not available
                return CreateVulkan();
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            return CreateVulkan();
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Use Vulkan via MoltenVK on macOS
            return CreateVulkan();
            // Future: CreateMetal() for native Metal support
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported platform for GPU compute.");
        }
    }

    /// <summary>
    /// Creates a DirectX 12 compute device (Windows only).
    /// </summary>
    public static IComputeDevice CreateDirectX12()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DirectX 12 is only supported on Windows.");
        }

        return new DirectX12.DX12ComputeDevice();
    }

    /// <summary>
    /// Creates a Vulkan compute device (cross-platform).
    /// Supports Windows, Linux, and macOS (via MoltenVK).
    /// </summary>
    public static IComputeDevice CreateVulkan()
    {
        return new Vulkan.VulkanComputeDevice();
    }

    // Future: CreateMetal() for native macOS Metal support
}
