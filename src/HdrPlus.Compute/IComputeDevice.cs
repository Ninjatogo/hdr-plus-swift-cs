namespace HdrPlus.Compute;

/// <summary>
/// Cross-platform GPU compute device abstraction.
/// Implementations: DirectX 12 (Windows), Vulkan (Linux/Windows), Metal (macOS via MoltenVK)
/// </summary>
public interface IComputeDevice : IDisposable
{
    /// <summary>
    /// Creates a GPU buffer from CPU data.
    /// </summary>
    IComputeBuffer CreateBuffer<T>(ReadOnlySpan<T> data, BufferUsage usage = BufferUsage.Default) where T : unmanaged;

    /// <summary>
    /// Creates an empty GPU buffer.
    /// </summary>
    IComputeBuffer CreateBuffer(int sizeInBytes, BufferUsage usage = BufferUsage.Default);

    /// <summary>
    /// Creates a 2D texture on the GPU.
    /// </summary>
    IComputeTexture CreateTexture2D(int width, int height, TextureFormat format, TextureUsage usage = TextureUsage.Default);

    /// <summary>
    /// Creates a 3D texture on the GPU.
    /// </summary>
    IComputeTexture CreateTexture3D(int width, int height, int depth, TextureFormat format, TextureUsage usage = TextureUsage.Default);

    /// <summary>
    /// Creates a compute pipeline (compiled shader + entry point).
    /// </summary>
    IComputePipeline CreatePipeline(string shaderName, string entryPoint = "main");

    /// <summary>
    /// Creates a command buffer for recording GPU commands.
    /// </summary>
    IComputeCommandBuffer CreateCommandBuffer();

    /// <summary>
    /// Executes a command buffer on the GPU.
    /// </summary>
    void Submit(IComputeCommandBuffer commandBuffer);

    /// <summary>
    /// Waits for all GPU work to complete.
    /// </summary>
    void WaitIdle();

    /// <summary>
    /// Gets the name of the GPU device.
    /// </summary>
    string DeviceName { get; }

    /// <summary>
    /// Gets the backend type (DirectX12, Vulkan, Metal).
    /// </summary>
    ComputeBackend Backend { get; }
}

public enum ComputeBackend
{
    DirectX12,
    Vulkan,
    Metal
}

public enum BufferUsage
{
    Default,
    Upload,     // CPU -> GPU
    Readback    // GPU -> CPU
}

public enum TextureUsage
{
    Default,
    ShaderRead,
    ShaderWrite,
    ShaderReadWrite
}

public enum TextureFormat
{
    R16_Float,      // Metal: .r16Float
    R32_Float,      // Metal: .r32Float
    RG16_SInt,      // Metal: .rg16Sint (for alignment vectors)
    RGBA16_Float,   // Metal: .rgba16Float
    RGBA32_Float    // Metal: .rgba32Float
}
