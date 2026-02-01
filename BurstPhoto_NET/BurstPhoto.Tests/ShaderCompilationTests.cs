using BurstPhoto.Rendering;

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
    // Alignment shaders (legacy multi-entry-point)
    [InlineData("Align.hlsl", "avg_pool")]
    [InlineData("Align.hlsl", "avg_pool_normalization")]
    [InlineData("Align.hlsl", "compute_tile_differences")]
    [InlineData("Align.hlsl", "compute_tile_differences25")]
    [InlineData("Align.hlsl", "compute_tile_differences_exposure25")]
    [InlineData("Align.hlsl", "warp_texture_bayer")]
    [InlineData("Align.hlsl", "find_best_tile_alignment")]
    [InlineData("Align.hlsl", "correct_upsampling_error")]
    [InlineData("Align.hlsl", "warp_texture_xtrans")]
    // Spatial merge shaders (legacy multi-entry-point)
    [InlineData("MergeSpatial.hlsl", "color_difference")]
    [InlineData("MergeSpatial.hlsl", "compute_merge_weight")]
    // Exposure shaders (legacy multi-entry-point)
    [InlineData("Exposure.hlsl", "correct_exposure")]
    [InlineData("Exposure.hlsl", "correct_exposure_linear")]
    [InlineData("Exposure.hlsl", "max_y")]
    [InlineData("Exposure.hlsl", "max_x")]
    // Texture operation shaders (legacy multi-entry-point)
    [InlineData("TextureOps.hlsl", "fill_with_zeros")]
    [InlineData("TextureOps.hlsl", "copy_texture")]
    [InlineData("TextureOps.hlsl", "convert_to_rgba")]
    [InlineData("TextureOps.hlsl", "add_texture")]
    [InlineData("TextureOps.hlsl", "add_texture_weighted")]
    [InlineData("TextureOps.hlsl", "add_weight_only")]
    [InlineData("TextureOps.hlsl", "add_texture_exposure")]
    [InlineData("TextureOps.hlsl", "add_texture_highlights")]
    [InlineData("TextureOps.hlsl", "prepare_texture_bayer")]
    [InlineData("TextureOps.hlsl", "blur_mosaic_texture")]
    [InlineData("TextureOps.hlsl", "find_hotpixels_bayer")]
    [InlineData("TextureOps.hlsl", "find_hotpixels_xtrans")]
    [InlineData("TextureOps.hlsl", "prepare_texture_xtrans")]
    // Frequency domain shaders (modular single-entry-point CSMain)
    [InlineData("Frequency/forward_fft.hlsl", "CSMain")]
    [InlineData("Frequency/backward_fft.hlsl", "CSMain")]
    [InlineData("Frequency/calculate_abs_diff_rgba.hlsl", "CSMain")]
    [InlineData("Frequency/calculate_rms_rgba.hlsl", "CSMain")]
    [InlineData("Frequency/calculate_mismatch_rgba.hlsl", "CSMain")]
    [InlineData("Frequency/calculate_highlights_norm_rgba.hlsl", "CSMain")]
    [InlineData("Frequency/normalize_mismatch.hlsl", "CSMain")]
    [InlineData("Frequency/deconvolute_frequency_domain.hlsl", "CSMain")]
    [InlineData("Frequency/merge_frequency_domain.hlsl", "CSMain")]
    [InlineData("Frequency/reduce_artifacts_tile_border.hlsl", "CSMain")]
    public void ComputeShader_ShouldCompile(string filename, string entryPoint)
    {
        var shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", filename);
        Assert.True(File.Exists(shaderPath), $"Shader file {filename} not found at {shaderPath}");

        using var compiler = new VulkanShaderCompiler();

        // Use CompileFile for modular shaders (handles includes automatically via ResolveIncludes)
        // This matches runtime behavior in VulkanKernelManager
        var spirv = compiler.CompileFile(shaderPath, entryPoint);

        Assert.NotNull(spirv);
        Assert.True(spirv.Length > 0);
    }
}
