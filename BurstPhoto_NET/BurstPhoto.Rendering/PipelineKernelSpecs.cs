using Silk.NET.Vulkan;

namespace BurstPhoto.Rendering;

/// <summary>
/// Centralized definitions for all pipeline layouts and kernel specifications.
/// This eliminates duplication across EnsureXxxPipeline methods.
/// </summary>
public static class PipelineKernelSpecs
{
    private static string ShadersDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Shaders");
    private static string FrequencyDir => Path.Combine(ShadersDir, "Frequency");

    #region Layout Specifications

    public static LayoutSpec PrepareLayout => new("Prepare", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // b0
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0 (unused)
        new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t1 InTextureUint
        new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t2 (unused)
        new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t3 AuxTextureFloat
        new() { Binding = 5, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t4 MeanTextureBuffer (unused)
        new() { Binding = 6, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t5 BlackLevels
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // u10 OutTextureFloat
        new() { Binding = 11, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // u11 (unused)
        new() { Binding = 12, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u12 (unused)
    ]);

    public static LayoutSpec ConversionLayout => new("Conversion", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // b0
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0
        new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t2
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // u10
        new() { Binding = 12, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u12
    ]);

    public static LayoutSpec AlignLayout => new("Align", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0
        new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t1
        new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t2
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u10
    ]);

    public static LayoutSpec MergeLayout => new("Merge", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0
        new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t1
        new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t2
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u10
    ]);

    public static LayoutSpec AccumLayout => new("Accum", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0
        new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t3
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u10
    ]);

    public static LayoutSpec AccumHighLayout => new("AccumHigh", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0
        new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t3
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // u10
        new() { Binding = 13, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u13
    ]);

    public static LayoutSpec NoiseEstLayout => new("NoiseEst", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0
        new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t3
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u10
    ]);

    public static LayoutSpec FrequencyLayout => new("Frequency", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // b0
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t1 RefTexture
        new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t2 AlignedTexture
        new() { Binding = 3, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t3 AuxTexture0 (RMS)
        new() { Binding = 4, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t4 AuxTexture1 (Mismatch)
        new() { Binding = 5, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t5 AuxTexture2 (Highlights)
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }   // u10 OutputTexture
    ]);

    public static LayoutSpec ExposureLayout => new("Exposure", [
        new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t0
        new() { Binding = 2, DescriptorType = DescriptorType.SampledImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },   // t1
        new() { Binding = 3, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t2 BlackLevels
        new() { Binding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // t3 MaxBuffer
        new() { Binding = 10, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },  // u0
        new() { Binding = 11, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }  // u1
    ]);

    #endregion

    #region Kernel Specifications

    // Prepare Pipeline
    public static KernelSpec PrepareBayer => new(
        "prepare_texture_bayer",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "prepare_texture_bayer"
    );

    // Conversion Pipeline
    public static KernelSpec ConvertToRgba => new(
        "convert_to_rgba",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "convert_to_rgba"
    );

    public static KernelSpec ConvertToBayer => new(
        "convert_to_bayer",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "convert_to_bayer"
    );

    // Align Pipeline
    public static KernelSpec AvgPool => new(
        "avg_pool",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "avg_pool"
    );

    public static KernelSpec AvgPoolNormalization => new(
        "avg_pool_normalization",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "avg_pool_normalization"
    );

    public static KernelSpec TileDiff => new(
        "compute_tile_differences",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "compute_tile_differences",
        8, 8, 4
    );

    public static KernelSpec TileDiff25 => new(
        "compute_tile_differences25",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "compute_tile_differences25"
    );

    public static KernelSpec TileDiffExposure25 => new(
        "compute_tile_differences_exposure25",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "compute_tile_differences_exposure25"
    );

    public static KernelSpec FindBest => new(
        "find_best_tile_alignment",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "find_best_tile_alignment"
    );

    public static KernelSpec Warp => new(
        "warp_texture_bayer",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "warp_texture_bayer"
    );

    public static KernelSpec UpsampleAlignment => new(
        "upsample_alignment",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "upsample_alignment"
    );

    public static KernelSpec CorrectUpsamplingError => new(
        "correct_upsampling_error",
        Path.Combine(ShadersDir, "Align.hlsl"),
        "correct_upsampling_error"
    );

    // Merge Pipeline (Spatial)
    public static KernelSpec ColorDiff => new(
        "color_difference",
        Path.Combine(ShadersDir, "MergeSpatial.hlsl"),
        "color_difference"
    );

    public static KernelSpec MergeWeight => new(
        "compute_merge_weight",
        Path.Combine(ShadersDir, "MergeSpatial.hlsl"),
        "compute_merge_weight"
    );

    public static KernelSpec AddWeighted => new(
        "add_texture_weighted",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "add_texture_weighted"
    );

    public static KernelSpec AddWeightOnly => new(
        "add_weight_only",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "add_weight_only"
    );

    public static KernelSpec AddExposure => new(
        "add_texture_exposure",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "add_texture_exposure"
    );

    public static KernelSpec AddHighlights => new(
        "add_texture_highlights",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "add_texture_highlights"
    );

    // Noise Estimation Pipeline
    public static KernelSpec BlurMosaic => new(
        "blur_mosaic_texture",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "blur_mosaic_texture"
    );

    public static KernelSpec ColorDiffSuperpixel => new(
        "color_difference_superpixel",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "color_difference_superpixel"
    );

    public static KernelSpec SumColumns => new(
        "sum_rect_columns_float",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "sum_rect_columns_float"
    );

    public static KernelSpec SumRows => new(
        "sum_row_to_buffer",
        Path.Combine(ShadersDir, "TextureOps.hlsl"),
        "sum_row_to_buffer"
    );

    // Frequency Domain Pipeline (modular shaders)
    public static KernelSpec AbsDiff => new(
        "calculate_abs_diff_rgba",
        Path.Combine(FrequencyDir, "calculate_abs_diff_rgba.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec Rms => new(
        "calculate_rms_rgba",
        Path.Combine(FrequencyDir, "calculate_rms_rgba.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec Mismatch => new(
        "calculate_mismatch_rgba",
        Path.Combine(FrequencyDir, "calculate_mismatch_rgba.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec HighlightsNorm => new(
        "calculate_highlights_norm_rgba",
        Path.Combine(FrequencyDir, "calculate_highlights_norm_rgba.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec NormalizeMismatch => new(
        "normalize_mismatch",
        Path.Combine(FrequencyDir, "normalize_mismatch.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec ArtifactsTileBorder => new(
        "reduce_artifacts_tile_border",
        Path.Combine(FrequencyDir, "reduce_artifacts_tile_border.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec ForwardFft => new(
        "forward_fft",
        Path.Combine(FrequencyDir, "forward_fft.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec BackwardFft => new(
        "backward_fft",
        Path.Combine(FrequencyDir, "backward_fft.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec MergeFrequencyDomain => new(
        "merge_frequency_domain",
        Path.Combine(FrequencyDir, "merge_frequency_domain.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    public static KernelSpec DeconvoluteFrequency => new(
        "deconvolute_frequency_domain",
        Path.Combine(FrequencyDir, "deconvolute_frequency_domain.hlsl"),
        "CSMain",
        UseModularShader: true
    );

    // Exposure Pipeline
    public static KernelSpec CorrectExposure => new(
        "correct_exposure",
        Path.Combine(ShadersDir, "Exposure.hlsl"),
        "correct_exposure"
    );

    public static KernelSpec CorrectExposureLinear => new(
        "correct_exposure_linear",
        Path.Combine(ShadersDir, "Exposure.hlsl"),
        "correct_exposure_linear"
    );

    public static KernelSpec MaxY => new(
        "max_y",
        Path.Combine(ShadersDir, "Exposure.hlsl"),
        "max_y",
        64, 1, 1
    );

    public static KernelSpec MaxX => new(
        "max_x",
        Path.Combine(ShadersDir, "Exposure.hlsl"),
        "max_x",
        1, 1, 1
    );

    #endregion
}
