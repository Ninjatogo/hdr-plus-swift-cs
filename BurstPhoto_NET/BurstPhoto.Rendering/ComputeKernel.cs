using System;
using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering;

public unsafe class ComputeKernel : IDisposable
{
    private readonly VulkanContext _ctx;
    public Pipeline Pipeline { get; private set; }
    public PipelineLayout PipelineLayout { get; private set; }
    public DescriptorSetLayout DescriptorSetLayout { get; private set; }
    
    // Default group size, typically hardcoded in shader [numthreads(x, y, z)]
    // We store it here to calculate dispatch groups
    public (uint x, uint y, uint z) WorkGroupSize { get; }

    public ComputeKernel(VulkanContext ctx, DescriptorSetLayout layout, byte[] shaderSpirv, string entryPoint = "CSMain", uint wgX = 64, uint wgY = 1, uint wgZ = 1)
    {
        _ctx = ctx;
        DescriptorSetLayout = layout;
        WorkGroupSize = (wgX, wgY, wgZ);

        CreateComputePipeline(shaderSpirv, entryPoint);
    }

    private void CreateComputePipeline(byte[] shaderSpirv, string entryPoint)
    {
        var shaderModule = CreateShaderModule(shaderSpirv);

        // Pipeline Layout
        var layouts = new[] { DescriptorSetLayout };
        fixed (DescriptorSetLayout* pLayouts = layouts)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = pLayouts
            };

            if (_ctx.Vk.CreatePipelineLayout(_ctx.Device, in pipelineLayoutInfo, null, out var layout) != Result.Success)
            {
                throw new Exception("Failed to create pipeline layout!");
            }
            PipelineLayout = layout;
        }

        // Pipeline
        // Specialize if needed, but for now typical compute
        var entryPointPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(entryPoint);

        var stageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = shaderModule,
            PName = (byte*)entryPointPtr
        };

        var pipelineInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Layout = PipelineLayout,
            Stage = stageInfo
        };

        var result = _ctx.Vk.CreateComputePipelines(_ctx.Device, default, 1, in pipelineInfo, null, out var pipeline);
        if (result != Result.Success)
        {
            throw new Exception($"Failed to create compute pipeline! Result: {result}");
        }
        Pipeline = pipeline;
        
        System.Runtime.InteropServices.Marshal.FreeHGlobal(entryPointPtr);
        _ctx.Vk.DestroyShaderModule(_ctx.Device, shaderModule, null);
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        if (code.Length % 4 != 0) throw new ArgumentException("Shader code size must be multiple of 4");
        
        uint[] words = new uint[code.Length / 4];
        System.Buffer.BlockCopy(code, 0, words, 0, code.Length);
        
        fixed (uint* pCode = words)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = pCode
            };

            if (_ctx.Vk.CreateShaderModule(_ctx.Device, in createInfo, null, out var  module) != Result.Success)
            {
                throw new Exception("Failed to create shader module!");
            }
            return module;
        }
    }

    public void Dispatch(CommandBuffer cmd, uint width, uint height = 1, uint depth = 1)
    {
        _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, Pipeline);

        uint groupX = (uint)Math.Ceiling(width / (double)WorkGroupSize.x);
        uint groupY = (uint)Math.Ceiling(height / (double)WorkGroupSize.y);
        uint groupZ = (uint)Math.Ceiling(depth / (double)WorkGroupSize.z);

        _ctx.Vk.CmdDispatch(cmd, groupX, groupY, groupZ);
    }
    
    // Overload that binds descriptor sets automatically if provided? 
    // Usually kept separate for flexibility, but binding the pipeline layout is needed to bind sets.
    public void BindPipeline(CommandBuffer cmd)
    {
        _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, Pipeline);
    }

    public void Dispose()
    {
        if (Pipeline.Handle != 0)
        {
            _ctx.Vk.DestroyPipeline(_ctx.Device, Pipeline, null);
            Pipeline = default;
        }
        if (PipelineLayout.Handle != 0)
        {
            _ctx.Vk.DestroyPipelineLayout(_ctx.Device, PipelineLayout, null);
            PipelineLayout = default;
        }
        // DescriptorSetLayout ownership is often shared, but if this kernel created it, it might own it.
        // For this design, we passed it in, so we assume the caller manages it unless we change ownership model.
        // We'll leave it to the caller (likely VulkanDescriptorManager or Pipeline class) to destroy layouts.
    }
}
