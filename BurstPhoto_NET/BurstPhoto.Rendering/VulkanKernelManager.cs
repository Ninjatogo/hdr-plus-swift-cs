using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering;

/// <summary>
/// Specification for creating a compute kernel.
/// </summary>
public record KernelSpec(
    string Name,
    string ShaderPath,
    string EntryPoint,
    uint WorkgroupX = 16,
    uint WorkgroupY = 16,
    uint WorkgroupZ = 1,
    bool UseModularShader = false // true = use CompileFile (no entry point rename), false = use string replace
);

/// <summary>
/// Specification for a descriptor set layout.
/// </summary>
public record LayoutSpec(
    string Name,
    DescriptorSetLayoutBinding[] Bindings
);

/// <summary>
/// Manages compute kernel creation, caching, and disposal.
/// Centralizes the pattern of lazy kernel initialization used throughout VulkanComputePipeline.
/// </summary>
public class VulkanKernelManager : IDisposable
{
    private readonly VulkanContext _ctx;
    private readonly VulkanShaderCompiler _compiler;
    private readonly VulkanDescriptorManager _descriptors;

    private readonly Dictionary<string, ComputeKernel> _kernelCache = new();
    private readonly Dictionary<string, DescriptorSetLayout> _layoutCache = new();
    private readonly Dictionary<string, string> _shaderSourceCache = new();

    private bool _disposed;

    public VulkanKernelManager(VulkanContext ctx, VulkanDescriptorManager descriptors)
    {
        _ctx = ctx;
        _compiler = new VulkanShaderCompiler();
        _descriptors = descriptors;
    }

    /// <summary>
    /// Gets or creates a descriptor set layout.
    /// </summary>
    public DescriptorSetLayout GetOrCreateLayout(LayoutSpec spec)
    {
        if (_layoutCache.TryGetValue(spec.Name, out var cached))
            return cached;

        var layout = _descriptors.CreateLayout(spec.Bindings);
        _layoutCache[spec.Name] = layout;
        return layout;
    }

    /// <summary>
    /// Gets or creates a compute kernel.
    /// </summary>
    public ComputeKernel GetOrCreateKernel(KernelSpec spec, DescriptorSetLayout layout)
    {
        if (_kernelCache.TryGetValue(spec.Name, out var cached))
            return cached;

        byte[] spirv;

        if (spec.UseModularShader)
        {
            // Modular shader: compile file directly (entry point already named CSMain)
            spirv = _compiler.CompileFile(spec.ShaderPath);
        }
        else
        {
            // Legacy shader: load source, resolve includes, rename entry point
            var source = GetShaderSource(spec.ShaderPath);
            var modified = source.Replace($"void {spec.EntryPoint}(", "void CSMain(");
            spirv = _compiler.Compile(modified, "CSMain");
        }

        var kernel = new ComputeKernel(_ctx, layout, spirv, "CSMain",
            spec.WorkgroupX, spec.WorkgroupY, spec.WorkgroupZ);

        _kernelCache[spec.Name] = kernel;
        return kernel;
    }

    /// <summary>
    /// Checks if a kernel has already been created.
    /// </summary>
    public bool HasKernel(string name) => _kernelCache.ContainsKey(name);

    /// <summary>
    /// Checks if a layout has already been created.
    /// </summary>
    public bool HasLayout(string name) => _layoutCache.ContainsKey(name);

    /// <summary>
    /// Gets a cached kernel by name, or null if not found.
    /// </summary>
    public ComputeKernel? GetKernel(string name) =>
        _kernelCache.TryGetValue(name, out var kernel) ? kernel : null;

    /// <summary>
    /// Gets a cached layout by name, or null if not found.
    /// </summary>
    public DescriptorSetLayout? GetLayout(string name) =>
        _layoutCache.TryGetValue(name, out var layout) ? layout : null;

    /// <summary>
    /// Loads and caches shader source with include resolution.
    /// </summary>
    private string GetShaderSource(string shaderPath)
    {
        if (_shaderSourceCache.TryGetValue(shaderPath, out var cached))
            return cached;

        var source = File.ReadAllText(shaderPath);

        // Resolve #include "Constants.hlsli"
        var constantsPath = Path.Combine(Path.GetDirectoryName(shaderPath) ?? "", "Constants.hlsli");
        if (File.Exists(constantsPath) && source.Contains("#include \"Constants.hlsli\""))
        {
            var constants = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constants);
        }

        _shaderSourceCache[shaderPath] = source;
        return source;
    }

    /// <summary>
    /// Clears the shader source cache (useful if shaders are modified at runtime).
    /// </summary>
    public void ClearSourceCache() => _shaderSourceCache.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kernel in _kernelCache.Values)
        {
            kernel.Dispose();
        }
        _kernelCache.Clear();
        _layoutCache.Clear();
        _shaderSourceCache.Clear();

        GC.SuppressFinalize(this);
    }
}
