#!/bin/bash
# Build HLSL shaders to SPIR-V (.spv) for Vulkan
# Requires: DXC with SPIR-V support (DirectX Shader Compiler)
# Install: https://github.com/microsoft/DirectXShaderCompiler

echo "========================================"
echo "Building HDR+ Compute Shaders (SPIR-V)"
echo "========================================"
echo ""

# Find dxc (check common locations)
DXC=""
if command -v dxc &> /dev/null; then
    DXC="dxc"
elif [ -f "/usr/bin/dxc" ]; then
    DXC="/usr/bin/dxc"
elif [ -f "/usr/local/bin/dxc" ]; then
    DXC="/usr/local/bin/dxc"
fi

# Check if dxc is available
if [ -z "$DXC" ]; then
    echo "ERROR: dxc not found. Please install DirectXShaderCompiler."
    echo "Ubuntu/Debian: sudo apt install dxc"
    echo "Or build from source: https://github.com/microsoft/DirectXShaderCompiler"
    exit 1
fi

# Verify SPIR-V support
if ! $DXC -help 2>&1 | grep -q "spirv"; then
    echo "WARNING: Your dxc may not support SPIR-V output."
    echo "Please ensure you have a version with -spirv support."
fi

# Create output directory
mkdir -p compiled_spirv

echo ""
echo "Using DXC: $DXC"
echo "Output directory: compiled_spirv/"
echo ""

# Compilation function
compile_shader() {
    local entry=$1
    local source=$2
    local output=$3
    echo "[$4] Compiling $entry..."
    $DXC -T cs_6_0 -E $entry $source -spirv -fspv-target-env=vulkan1.2 -Fo compiled_spirv/$output
    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to compile $entry"
        exit 1
    fi
}

# ============================================================================
# Alignment Shaders (9 kernels)
# ============================================================================
echo ""
echo "[Alignment Shaders - 9 kernels]"
echo "----------------------------------------"

compile_shader "avg_pool" "Alignment.hlsl" "avg_pool.spv" "1/46"
compile_shader "avg_pool_normalization" "Alignment.hlsl" "avg_pool_normalization.spv" "2/46"
compile_shader "compute_tile_differences" "Alignment.hlsl" "compute_tile_differences.spv" "3/46"
compile_shader "compute_tile_differences25" "Alignment.hlsl" "compute_tile_differences25.spv" "4/46"
compile_shader "compute_tile_differences_exposure25" "Alignment.hlsl" "compute_tile_differences_exposure25.spv" "5/46"
compile_shader "find_best_tile_alignment" "Alignment.hlsl" "find_best_tile_alignment.spv" "6/46"
compile_shader "warp_texture_bayer" "Alignment.hlsl" "warp_texture_bayer.spv" "7/46"
compile_shader "warp_texture_xtrans" "Alignment.hlsl" "warp_texture_xtrans.spv" "8/46"
compile_shader "correct_upsampling_error" "Alignment.hlsl" "correct_upsampling_error.spv" "9/46"

# ============================================================================
# Spatial Merge Shaders (2 kernels)
# ============================================================================
echo ""
echo "[Spatial Merge Shaders - 2 kernels]"
echo "----------------------------------------"

compile_shader "color_difference" "SpatialMerge.hlsl" "color_difference.spv" "10/46"
compile_shader "compute_merge_weight" "SpatialMerge.hlsl" "compute_merge_weight.spv" "11/46"

# ============================================================================
# Texture Utilities (25 kernels)
# ============================================================================
echo ""
echo "[Texture Utilities - 25 kernels]"
echo "----------------------------------------"

compile_shader "add_texture" "TextureUtilities.hlsl" "add_texture.spv" "12/46"
compile_shader "add_texture_exposure" "TextureUtilities.hlsl" "add_texture_exposure.spv" "13/46"
compile_shader "add_texture_highlights" "TextureUtilities.hlsl" "add_texture_highlights.spv" "14/46"
compile_shader "add_texture_uint16" "TextureUtilities.hlsl" "add_texture_uint16.spv" "15/46"
compile_shader "add_texture_weighted" "TextureUtilities.hlsl" "add_texture_weighted.spv" "16/46"
compile_shader "blur_mosaic_texture" "TextureUtilities.hlsl" "blur_mosaic_texture.spv" "17/46"
compile_shader "calculate_weight_highlights" "TextureUtilities.hlsl" "calculate_weight_highlights.spv" "18/46"
compile_shader "convert_float_to_uint16" "TextureUtilities.hlsl" "convert_float_to_uint16.spv" "19/46"
compile_shader "convert_to_bayer" "TextureUtilities.hlsl" "convert_to_bayer.spv" "20/46"
compile_shader "convert_to_rgba" "TextureUtilities.hlsl" "convert_to_rgba.spv" "21/46"
compile_shader "copy_texture" "TextureUtilities.hlsl" "copy_texture.spv" "22/46"
compile_shader "crop_texture" "TextureUtilities.hlsl" "crop_texture.spv" "23/46"
compile_shader "fill_with_zeros" "TextureUtilities.hlsl" "fill_with_zeros.spv" "24/46"
compile_shader "find_hotpixels_bayer" "TextureUtilities.hlsl" "find_hotpixels_bayer.spv" "25/46"
compile_shader "find_hotpixels_xtrans" "TextureUtilities.hlsl" "find_hotpixels_xtrans.spv" "26/46"
compile_shader "prepare_texture_bayer" "TextureUtilities.hlsl" "prepare_texture_bayer.spv" "27/46"
compile_shader "prepare_texture_xtrans" "TextureUtilities.hlsl" "prepare_texture_xtrans.spv" "28/46"
compile_shader "divide_buffer" "TextureUtilities.hlsl" "divide_buffer.spv" "29/46"
compile_shader "sum_divide_buffer" "TextureUtilities.hlsl" "sum_divide_buffer.spv" "30/46"
compile_shader "normalize_texture" "TextureUtilities.hlsl" "normalize_texture.spv" "31/46"
compile_shader "sum_rect_columns_float" "TextureUtilities.hlsl" "sum_rect_columns_float.spv" "32/46"
compile_shader "sum_rect_columns_uint" "TextureUtilities.hlsl" "sum_rect_columns_uint.spv" "33/46"
compile_shader "sum_row" "TextureUtilities.hlsl" "sum_row.spv" "34/46"
compile_shader "upsample_bilinear_float" "TextureUtilities.hlsl" "upsample_bilinear_float.spv" "35/46"
compile_shader "upsample_nearest_int" "TextureUtilities.hlsl" "upsample_nearest_int.spv" "36/46"

# ============================================================================
# Frequency Merge Shaders (8 kernels)
# ============================================================================
echo ""
echo "[Frequency Merge Shaders - 8 kernels]"
echo "----------------------------------------"

compile_shader "merge_frequency_domain" "FrequencyMerge.hlsl" "merge_frequency_domain.spv" "37/46"
compile_shader "calculate_abs_diff_rgba" "FrequencyMerge.hlsl" "calculate_abs_diff_rgba.spv" "38/46"
compile_shader "calculate_highlights_norm_rgba" "FrequencyMerge.hlsl" "calculate_highlights_norm_rgba.spv" "39/46"
compile_shader "calculate_mismatch_rgba" "FrequencyMerge.hlsl" "calculate_mismatch_rgba.spv" "40/46"
compile_shader "calculate_rms_rgba" "FrequencyMerge.hlsl" "calculate_rms_rgba.spv" "41/46"
compile_shader "deconvolute_frequency_domain" "FrequencyMerge.hlsl" "deconvolute_frequency_domain.spv" "42/46"
compile_shader "normalize_mismatch" "FrequencyMerge.hlsl" "normalize_mismatch.spv" "43/46"
compile_shader "reduce_artifacts_tile_border" "FrequencyMerge.hlsl" "reduce_artifacts_tile_border.spv" "44/46"

# ============================================================================
# Exposure Correction Shaders (2 kernels)
# ============================================================================
echo ""
echo "[Exposure Correction Shaders - 2 kernels]"
echo "----------------------------------------"

compile_shader "correct_exposure" "ExposureCorrection.hlsl" "correct_exposure.spv" "45/46"
compile_shader "correct_exposure_linear" "ExposureCorrection.hlsl" "correct_exposure_linear.spv" "46/46"

# ============================================================================
# Summary
# ============================================================================
echo ""
echo "========================================"
echo "SPIR-V shader compilation complete!"
echo "========================================"
echo ""
echo "Total shaders compiled: 46 kernels"
echo "  - Alignment: 9 kernels"
echo "  - Spatial Merge: 2 kernels"
echo "  - Texture Utilities: 25 kernels"
echo "  - Frequency Merge: 8 kernels"
echo "  - Exposure Correction: 2 kernels"
echo ""
echo "Output directory: shaders/compiled_spirv/*.spv"
echo ""
echo "NOTES:"
echo "- SPIR-V shaders can be used with Vulkan backend"
echo "- Embed .spv files as resources in HdrPlus.Compute project"
echo "========================================"
echo ""
