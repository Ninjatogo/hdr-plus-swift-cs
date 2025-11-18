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
    /// macOS: Metal or Vulkan (via MoltenVK)
    /// </summary>
    public static IComputeDevice CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            // Prefer DirectX 12 on Windows for best performance
            return CreateDirectX12();
        }
        else if (OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Vulkan backend not yet implemented. Coming soon!");
            // return CreateVulkan();
        }
        else if (OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Metal backend not yet implemented. Coming soon!");
            // return CreateMetal();
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

    // Future: CreateVulkan(), CreateMetal()
}
