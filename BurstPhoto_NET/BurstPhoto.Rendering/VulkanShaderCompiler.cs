using System;
using Silk.NET.Shaderc;

namespace BurstPhoto.Rendering;

public unsafe class VulkanShaderCompiler : IDisposable
{
    private readonly Shaderc _shaderc;
    private readonly Compiler* _compiler;
    private readonly CompileOptions* _options;

    public VulkanShaderCompiler()
    {
        _shaderc = Shaderc.GetApi();
        _compiler = _shaderc.CompilerInitialize();
        _options = _shaderc.CompileOptionsInitialize();

        _shaderc.CompileOptionsSetSourceLanguage(_options, SourceLanguage.Hlsl);
        _shaderc.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
        // Optimize for performance
        _shaderc.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Performance);
    }

    public byte[] Compile(string source, string name, string entryPoint = "CSMain")
    {
        var result = _shaderc.CompileIntoSpv(
            _compiler, 
            source, 
            (nuint)source.Length, 
            ShaderKind.ComputeShader, 
            name, 
            entryPoint, 
            _options);

        if (_shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
        {
            var errorMsg = _shaderc.ResultGetErrorMessageS(result);
            _shaderc.ResultRelease(result);
            throw new Exception($"Shader compilation failed for {name}: {errorMsg}");
        }

        var length = _shaderc.ResultGetLength(result);
        var bytes = _shaderc.ResultGetBytes(result);

        var byteCode = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy((IntPtr)bytes, byteCode, 0, (int)length);

        _shaderc.ResultRelease(result);

        return byteCode;
    }

    public void Dispose()
    {
        _shaderc.CompileOptionsRelease(_options);
        _shaderc.CompilerRelease(_compiler);
        _shaderc.Dispose();
    }
}
