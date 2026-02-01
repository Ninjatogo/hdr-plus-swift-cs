using BurstPhoto.Rendering;
using BurstPhoto.Rendering.Pipelines;

namespace BurstPhoto.Tests.TestHelpers;

/// <summary>
/// Base class for GPU-based tests. Manages Vulkan context lifecycle and provides
/// common infrastructure for GPU pipeline testing.
///
/// Usage:
/// 1. Inherit from this class
/// 2. Use the provided Context, Descriptors, KernelManager, and Factory
/// 3. Create pipeline instances as needed using the shared infrastructure
/// 4. Call Skip.If(!IsGpuAvailable, "No GPU available") at start of tests
/// </summary>
public class GpuTestFixture : IDisposable
{
    /// <summary>
    /// Test output helper for logging during tests.
    /// </summary>
    protected readonly ITestOutputHelper? Output;

    /// <summary>
    /// Vulkan context (instance, device, queues).
    /// </summary>
    public VulkanContext? Context { get; private set; }

    /// <summary>
    /// Descriptor set manager for allocating descriptor sets.
    /// </summary>
    public VulkanDescriptorManager? Descriptors { get; private set; }

    /// <summary>
    /// Kernel manager for creating and caching compute kernels.
    /// </summary>
    public VulkanKernelManager? KernelManager { get; private set; }

    /// <summary>
    /// Test texture factory for creating GPU textures from patterns.
    /// </summary>
    public TestTextureFactory? Factory { get; private set; }

    /// <summary>
    /// Whether GPU is available for testing.
    /// </summary>
    public bool IsGpuAvailable { get; private set; }

    /// <summary>
    /// Whether the GPU supports frequency domain operations (StorageImageWriteWithoutFormat).
    /// </summary>
    public bool SupportsFrequencyDomain { get; private set; }

    /// <summary>
    /// Error message if GPU initialization failed.
    /// </summary>
    public string? GpuError { get; private set; }

    /// <summary>
    /// Creates a new GPU test fixture with optional test output.
    /// </summary>
    public GpuTestFixture(ITestOutputHelper? output = null)
    {
        Output = output;
        InitializeGpu();
    }

    private void InitializeGpu()
    {
        try
        {
            Context = new VulkanContext();
            Descriptors = new VulkanDescriptorManager(Context);
            KernelManager = new VulkanKernelManager(Context, Descriptors);
            Factory = new TestTextureFactory(Context);

            IsGpuAvailable = true;
            SupportsFrequencyDomain = Context.SupportsStorageImageWriteWithoutFormat;

            Output?.WriteLine($"GPU initialized: {GetGpuName()}");
            Output?.WriteLine($"Frequency domain support: {SupportsFrequencyDomain}");
        }
        catch (Exception ex)
        {
            IsGpuAvailable = false;
            SupportsFrequencyDomain = false;
            GpuError = ex.Message;
            Output?.WriteLine($"GPU initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the name of the GPU device being used.
    /// </summary>
    public string GetGpuName()
    {
        if (Context == null) return "No GPU";

        // Return a simple identifier since we can't easily get the device name
        // without unsafe code in the test assembly
        return $"GPU (PhysicalDevice={Context.PhysicalDevice.Handle:X})";
    }

    /// <summary>
    /// Creates a FrequencyMergePipeline for FFT testing.
    /// Requires SupportsFrequencyDomain to be true.
    /// </summary>
    public FrequencyMergePipeline CreateFrequencyPipeline()
    {
        EnsureGpuAvailable();
        if (!SupportsFrequencyDomain)
            throw new NotSupportedException("GPU does not support frequency domain operations");

        return new FrequencyMergePipeline(Context!, Descriptors!, KernelManager!);
    }

    /// <summary>
    /// Creates an AlignmentPipeline for alignment testing.
    /// </summary>
    public AlignmentPipeline CreateAlignmentPipeline()
    {
        EnsureGpuAvailable();
        return new AlignmentPipeline(Context!, Descriptors!, KernelManager!);
    }

    /// <summary>
    /// Creates a SpatialMergePipeline for spatial merge testing.
    /// </summary>
    public SpatialMergePipeline CreateSpatialMergePipeline()
    {
        EnsureGpuAvailable();
        return new SpatialMergePipeline(Context!, Descriptors!, KernelManager!);
    }

    /// <summary>
    /// Creates an ExposurePipeline for exposure/blur testing.
    /// </summary>
    public ExposurePipeline CreateExposurePipeline()
    {
        EnsureGpuAvailable();
        return new ExposurePipeline(Context!, Descriptors!, KernelManager!);
    }

    /// <summary>
    /// Ensures GPU is available, throwing if not.
    /// </summary>
    protected void EnsureGpuAvailable()
    {
        if (!IsGpuAvailable)
            throw new InvalidOperationException($"GPU not available: {GpuError}");
    }

    /// <summary>
    /// Logs a message to test output if available.
    /// </summary>
    protected void Log(string message)
    {
        Output?.WriteLine(message);
    }

    public void Dispose()
    {
        Factory?.Dispose();
        KernelManager?.Dispose();
        Descriptors?.Dispose();
        Context?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// xUnit collection fixture for sharing GPU context across multiple test classes.
/// Use [Collection("GPU")] attribute on test classes that need GPU access.
/// </summary>
public class GpuCollectionFixture : GpuTestFixture
{
    public GpuCollectionFixture() : base(null) { }
}

/// <summary>
/// Collection definition for GPU tests.
/// </summary>
[CollectionDefinition("GPU")]
public class GpuCollection : ICollectionFixture<GpuCollectionFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
