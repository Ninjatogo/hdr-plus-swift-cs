using Xunit;
using BurstPhoto.Rendering;
using System.IO;
using System;

namespace BurstPhoto.Tests;

public class ShaderCompilationTests
{
    [Fact]
    public void Constants_ShouldCompile()
    {
        // Constants.hlsli cannot be compiled directly as it has no entry point.
        // But we can compile a dummy shader that includes it.
        var shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "Constants.hlsli");
        Assert.True(File.Exists(shaderPath), $"Constants.hlsli not found at {shaderPath}");
    }

    [Theory]
    [InlineData("Align.hlsl", "avg_pool")]
    [InlineData("Align.hlsl", "avg_pool_normalization")]
    [InlineData("Align.hlsl", "compute_tile_differences")]
    [InlineData("Align.hlsl", "compute_tile_differences25")]
    [InlineData("Align.hlsl", "compute_tile_differences_exposure25")]
    [InlineData("Align.hlsl", "warp_texture_bayer")]
    [InlineData("Align.hlsl", "find_best_tile_alignment")]
    [InlineData("Align.hlsl", "correct_upsampling_error")]
    [InlineData("Align.hlsl", "warp_texture_xtrans")]
    [InlineData("MergeSpatial.hlsl", "color_difference")]
    [InlineData("MergeSpatial.hlsl", "compute_merge_weight")]
    [InlineData("MergeFrequency.hlsl", "forward_fft")]
    [InlineData("MergeFrequency.hlsl", "backward_fft")]
    [InlineData("MergeFrequency.hlsl", "calculate_abs_diff_rgba")]
    [InlineData("MergeFrequency.hlsl", "deconvolute_frequency_domain")]
    [InlineData("MergeFrequency.hlsl", "merge_frequency_domain")]
    [InlineData("Exposure.hlsl", "correct_exposure")]
    [InlineData("Exposure.hlsl", "correct_exposure_linear")]
    [InlineData("Exposure.hlsl", "max_y")]
    [InlineData("Exposure.hlsl", "max_x")]
    [InlineData("TextureOps.hlsl", "fill_with_zeros")]
    [InlineData("TextureOps.hlsl", "copy_texture")]
    [InlineData("TextureOps.hlsl", "convert_to_rgba")]
    [InlineData("TextureOps.hlsl", "add_texture")]
    [InlineData("TextureOps.hlsl", "add_texture_weighted")]
    [InlineData("TextureOps.hlsl", "add_weight_only")]
    [InlineData("TextureOps.hlsl", "prepare_texture_bayer")]
    [InlineData("TextureOps.hlsl", "blur_mosaic_texture")]
    [InlineData("TextureOps.hlsl", "find_hotpixels_bayer")]
    [InlineData("TextureOps.hlsl", "find_hotpixels_xtrans")]
    [InlineData("TextureOps.hlsl", "prepare_texture_xtrans")]
    public void ComputeShader_ShouldCompile(string filename, string entryPoint)
    {
        // Fix filename typos in InlineData if any (I see Align.hlsl and Algn.hlsl above)
        // I'll fix the string in the implementation below
        if (filename == "Algn.hlsl") filename = "Align.hlsl";

        var shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", filename);
        Assert.True(File.Exists(shaderPath), $"Shader file {filename} not found at {shaderPath}");

        var source = File.ReadAllText(shaderPath);
        
        // We need to resolve include paths manually or rely on the compiler finding them relative to CWD?
        // Shaderc might struggle with relative #include "Constants.hlsli" if CWD isn't right.
        // Let's manually inline or set include handler. 
        // For now, simpler: Read Constants and Prepend it? 
        // Or better: Use Shaderc include resolver. 
        // Silk.NET's wrapper usage in VulkanShaderCompiler didn't set up an include handler.
        // Quick fix: Prepend Constants.hlsli data if #include is present.
        
        if (source.Contains("#include \"Constants.hlsli\""))
        {
            var constantsPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "Constants.hlsli");
            var constantsSource = File.ReadAllText(constantsPath);
            source = source.Replace("#include \"Constants.hlsli\"", constantsSource);
        }

        using var compiler = new VulkanShaderCompiler();
        var spirv = compiler.Compile(source, filename, entryPoint);

        Assert.NotNull(spirv);
        Assert.True(spirv.Length > 0);
    }
}
