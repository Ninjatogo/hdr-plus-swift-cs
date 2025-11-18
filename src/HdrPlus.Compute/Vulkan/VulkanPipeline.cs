using Silk.NET.Vulkan;
using System.Reflection;

namespace HdrPlus.Compute.Vulkan;

/// <summary>
/// Vulkan implementation of compute pipeline.
/// Loads SPIR-V shaders and creates compute pipeline state.
/// </summary>
public unsafe class VulkanPipeline : IComputePipeline
{
    private readonly VulkanComputeDevice _computeDevice;
    private readonly Vk _vk;
    private readonly Device _device;
    private ShaderModule _shaderModule;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _descriptorSetLayout;
    private bool _disposed;

    public string Name { get; }
    public string EntryPoint { get; }

    internal Pipeline GetPipeline() => _pipeline;
    internal PipelineLayout GetPipelineLayout() => _pipelineLayout;
    internal DescriptorSetLayout GetDescriptorSetLayout() => _descriptorSetLayout;

    public VulkanPipeline(
        VulkanComputeDevice computeDevice,
        Vk vk,
        Device device,
        string shaderName,
        string entryPoint = "main")
    {
        _computeDevice = computeDevice;
        _vk = vk;
        _device = device;
        Name = shaderName;
        EntryPoint = entryPoint;

        LoadShader();
        CreateDescriptorSetLayout();
        CreatePipelineLayout();
        CreateComputePipeline();
    }

    private void LoadShader()
    {
        // Load SPIR-V shader from embedded resources
        // Look for .spv files (SPIR-V compiled shaders)
        string resourceName = $"HdrPlus.Compute.shaders.{Name}.spv";
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Shader resource not found: {resourceName}. Make sure SPIR-V shaders are embedded.");
        }

        // Read SPIR-V bytecode
        byte[] spirvCode = new byte[stream.Length];
        stream.Read(spirvCode, 0, (int)stream.Length);

        // Create shader module
        fixed (byte* codePtr = spirvCode)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirvCode.Length,
                PCode = (uint*)codePtr
            };

            fixed (ShaderModule* modulePtr = &_shaderModule)
            {
                if (_vk.CreateShaderModule(_device, &createInfo, null, modulePtr) != Result.Success)
                {
                    throw new Exception($"Failed to create shader module for {Name}");
                }
            }
        }
    }

    private void CreateDescriptorSetLayout()
    {
        // Create descriptor set layout with common bindings
        // This is a simplified version - real implementation should reflect shader bindings
        var bindings = stackalloc DescriptorSetLayoutBinding[16];

        for (uint i = 0; i < 16; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 16,
            PBindings = bindings
        };

        fixed (DescriptorSetLayout* layoutPtr = &_descriptorSetLayout)
        {
            if (_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, layoutPtr) != Result.Success)
            {
                throw new Exception("Failed to create descriptor set layout");
            }
        }
    }

    private void CreatePipelineLayout()
    {
        fixed (DescriptorSetLayout* layoutPtr = &_descriptorSetLayout)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = layoutPtr,
                PushConstantRangeCount = 0
            };

            fixed (PipelineLayout* pipelineLayoutPtr = &_pipelineLayout)
            {
                if (_vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, pipelineLayoutPtr) != Result.Success)
                {
                    throw new Exception("Failed to create pipeline layout");
                }
            }
        }
    }

    private void CreateComputePipeline()
    {
        fixed (byte* entryPointPtr = System.Text.Encoding.UTF8.GetBytes(EntryPoint + "\0"))
        {
            var stageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = _shaderModule,
                PName = entryPointPtr
            };

            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stageInfo,
                Layout = _pipelineLayout
            };

            fixed (Pipeline* pipelinePtr = &_pipeline)
            {
                if (_vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, pipelinePtr) != Result.Success)
                {
                    throw new Exception($"Failed to create compute pipeline for {Name}");
                }
            }
        }
    }

    public (int X, int Y, int Z) GetThreadGroupSize()
    {
        // Default thread group size for compute shaders
        // This should ideally be extracted from SPIR-V reflection
        return (8, 8, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _pipeline, null);
        }

        if (_pipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        }

        if (_descriptorSetLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
        }

        if (_shaderModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _shaderModule, null);
        }

        _disposed = true;
    }
}
