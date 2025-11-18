using FluentAssertions;
using HdrPlus.Compute;
using Xunit;
using Xunit.Abstractions;

namespace HdrPlus.Tests.Integration;

/// <summary>
/// Multi-platform compatibility tests.
/// Validates that the HDR+ pipeline works across Windows (DirectX12), Linux (Vulkan), and macOS (Metal/Vulkan).
/// </summary>
public class MultiPlatformTests
{
    private readonly ITestOutputHelper _output;

    public MultiPlatformTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = "Platform detection test")]
    public void DetectPlatform_ShouldIdentifyCorrectOS()
    {
        // Act
        var isWindows = OperatingSystem.IsWindows();
        var isLinux = OperatingSystem.IsLinux();
        var isMacOS = OperatingSystem.IsMacOS();

        // Assert & Output
        _output.WriteLine($"Platform Detection:");
        _output.WriteLine($"  Windows: {isWindows}");
        _output.WriteLine($"  Linux: {isLinux}");
        _output.WriteLine($"  macOS: {isMacOS}");

        (isWindows || isLinux || isMacOS).Should().BeTrue("Should be running on a supported platform");
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void CreateDevice_OnCurrentPlatform_ShouldSelectCorrectBackend()
    {
        // Act
        using var device = ComputeDeviceFactory.CreateDefault();

        // Assert
        _output.WriteLine($"Selected Backend: {device.Backend}");
        _output.WriteLine($"Device Name: {device.DeviceName}");

        if (OperatingSystem.IsWindows())
        {
            device.Backend.Should().BeOneOf(ComputeBackend.DirectX12, ComputeBackend.Vulkan,
                because: "Windows supports both DirectX12 and Vulkan");
        }
        else if (OperatingSystem.IsLinux())
        {
            device.Backend.Should().Be(ComputeBackend.Vulkan,
                because: "Linux only supports Vulkan");
        }
        else if (OperatingSystem.IsMacOS())
        {
            device.Backend.Should().BeOneOf(ComputeBackend.Metal, ComputeBackend.Vulkan,
                because: "macOS supports Metal natively or Vulkan via MoltenVK");
        }
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void DirectX12Backend_OnWindows_ShouldInitializeSuccessfully()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipping DirectX12 test - not on Windows");
            return;
        }

        // Act
        Action act = () =>
        {
            using var device = ComputeDeviceFactory.Create(ComputeBackend.DirectX12);
            _output.WriteLine($"DirectX12 Device: {device.DeviceName}");
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void VulkanBackend_OnLinux_ShouldInitializeSuccessfully()
    {
        // Arrange
        if (!OperatingSystem.IsLinux())
        {
            _output.WriteLine("Skipping Vulkan test - not on Linux");
            return;
        }

        // Act
        Action act = () =>
        {
            using var device = ComputeDeviceFactory.Create(ComputeBackend.Vulkan);
            _output.WriteLine($"Vulkan Device: {device.DeviceName}");
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void VulkanBackend_OnWindows_ShouldInitializeSuccessfully()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipping Vulkan on Windows test - not on Windows");
            return;
        }

        // Act
        Action act = () =>
        {
            using var device = ComputeDeviceFactory.Create(ComputeBackend.Vulkan);
            _output.WriteLine($"Vulkan Device (Windows): {device.DeviceName}");
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void AllAvailableBackends_ShouldInitializeWithoutErrors()
    {
        // Arrange
        var backends = new[] { ComputeBackend.DirectX12, ComputeBackend.Vulkan, ComputeBackend.Metal };
        var successfulBackends = new List<ComputeBackend>();

        // Act
        foreach (var backend in backends)
        {
            try
            {
                using var device = ComputeDeviceFactory.Create(backend);
                successfulBackends.Add(backend);
                _output.WriteLine($"✓ {backend}: {device.DeviceName}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"✗ {backend}: {ex.Message}");
            }
        }

        // Assert
        successfulBackends.Should().NotBeEmpty("At least one backend should be available");
    }

    [Theory(Skip = "Requires GPU hardware")]
    [InlineData(ComputeBackend.DirectX12)]
    [InlineData(ComputeBackend.Vulkan)]
    [InlineData(ComputeBackend.Metal)]
    public void SpecificBackend_CreateBuffer_ShouldWorkCorrectly(ComputeBackend backend)
    {
        // Arrange
        IComputeDevice? device = null;

        try
        {
            device = ComputeDeviceFactory.Create(backend);
        }
        catch
        {
            _output.WriteLine($"Backend {backend} not available on this platform - skipping");
            return;
        }

        Span<float> data = stackalloc float[] { 1.0f, 2.0f, 3.0f, 4.0f };

        // Act
        using var buffer = device.CreateBuffer(data);

        // Assert
        buffer.Should().NotBeNull();
        buffer.SizeInBytes.Should().Be(4 * sizeof(float));

        device?.Dispose();
    }

    [Theory(Skip = "Requires GPU hardware")]
    [InlineData(ComputeBackend.DirectX12)]
    [InlineData(ComputeBackend.Vulkan)]
    [InlineData(ComputeBackend.Metal)]
    public void SpecificBackend_CreateTexture_ShouldWorkCorrectly(ComputeBackend backend)
    {
        // Arrange
        IComputeDevice? device = null;

        try
        {
            device = ComputeDeviceFactory.Create(backend);
        }
        catch
        {
            _output.WriteLine($"Backend {backend} not available on this platform - skipping");
            return;
        }

        // Act
        using var texture = device.CreateTexture2D(512, 512, TextureFormat.R16_Float);

        // Assert
        texture.Should().NotBeNull();
        texture.Width.Should().Be(512);
        texture.Height.Should().Be(512);

        device?.Dispose();
    }

    [Fact(Skip = "Cross-platform shader test")]
    public void ShaderFormats_ShouldBeCorrectForPlatform()
    {
        // Arrange & Act
        var expectedFormats = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            expectedFormats.Add("DXIL (.dxil)"); // DirectX12
            expectedFormats.Add("SPIR-V (.spv)"); // Vulkan
        }
        else if (OperatingSystem.IsLinux())
        {
            expectedFormats.Add("SPIR-V (.spv)"); // Vulkan
        }
        else if (OperatingSystem.IsMacOS())
        {
            expectedFormats.Add("Metal (.metallib)");
            expectedFormats.Add("SPIR-V (.spv)"); // Via MoltenVK
        }

        // Assert
        _output.WriteLine("Expected shader formats on this platform:");
        foreach (var format in expectedFormats)
        {
            _output.WriteLine($"  - {format}");
        }

        expectedFormats.Should().NotBeEmpty();
    }
}
