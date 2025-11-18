@echo off
REM Build HLSL shaders to compiled shader objects (.cso) for DirectX 12
REM Requires: Windows SDK with dxc.exe (DirectX Shader Compiler)

echo ========================================
echo Building HDR+ Phase 3 Compute Shaders
echo ========================================
echo.

REM Find dxc.exe (usually in Windows SDK)
set DXC="dxc.exe"

REM Check if dxc is available
where %DXC% >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: dxc.exe not found. Please install Windows SDK.
    echo Download from: https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/
    exit /b 1
)

REM Create output directory
if not exist compiled mkdir compiled

REM ============================================================================
REM Alignment Shaders (9 kernels)
REM ============================================================================
echo.
echo [Alignment Shaders - 9 kernels]
echo ----------------------------------------

echo [1/46] Compiling avg_pool...
%DXC% -T cs_6_0 -E avg_pool Alignment.hlsl -Fo compiled\avg_pool.cso

echo [2/46] Compiling avg_pool_normalization...
%DXC% -T cs_6_0 -E avg_pool_normalization Alignment.hlsl -Fo compiled\avg_pool_normalization.cso

echo [3/46] Compiling compute_tile_differences...
%DXC% -T cs_6_0 -E compute_tile_differences Alignment.hlsl -Fo compiled\compute_tile_differences.cso

echo [4/46] Compiling compute_tile_differences25...
%DXC% -T cs_6_0 -E compute_tile_differences25 Alignment.hlsl -Fo compiled\compute_tile_differences25.cso

echo [5/46] Compiling compute_tile_differences_exposure25...
%DXC% -T cs_6_0 -E compute_tile_differences_exposure25 Alignment.hlsl -Fo compiled\compute_tile_differences_exposure25.cso

echo [6/46] Compiling find_best_tile_alignment...
%DXC% -T cs_6_0 -E find_best_tile_alignment Alignment.hlsl -Fo compiled\find_best_tile_alignment.cso

echo [7/46] Compiling warp_texture_bayer...
%DXC% -T cs_6_0 -E warp_texture_bayer Alignment.hlsl -Fo compiled\warp_texture_bayer.cso

echo [8/46] Compiling warp_texture_xtrans...
%DXC% -T cs_6_0 -E warp_texture_xtrans Alignment.hlsl -Fo compiled\warp_texture_xtrans.cso

echo [9/46] Compiling correct_upsampling_error...
%DXC% -T cs_6_0 -E correct_upsampling_error Alignment.hlsl -Fo compiled\correct_upsampling_error.cso

REM ============================================================================
REM Spatial Merge Shaders (2 kernels)
REM ============================================================================
echo.
echo [Spatial Merge Shaders - 2 kernels]
echo ----------------------------------------

echo [10/46] Compiling color_difference...
%DXC% -T cs_6_0 -E color_difference SpatialMerge.hlsl -Fo compiled\color_difference.cso

echo [11/46] Compiling compute_merge_weight...
%DXC% -T cs_6_0 -E compute_merge_weight SpatialMerge.hlsl -Fo compiled\compute_merge_weight.cso

REM ============================================================================
REM Texture Utilities (25 kernels)
REM ============================================================================
echo.
echo [Texture Utilities - 25 kernels]
echo ----------------------------------------

echo [12/46] Compiling add_texture...
%DXC% -T cs_6_0 -E add_texture TextureUtilities.hlsl -Fo compiled\add_texture.cso

echo [13/46] Compiling add_texture_exposure...
%DXC% -T cs_6_0 -E add_texture_exposure TextureUtilities.hlsl -Fo compiled\add_texture_exposure.cso

echo [14/46] Compiling add_texture_highlights...
%DXC% -T cs_6_0 -E add_texture_highlights TextureUtilities.hlsl -Fo compiled\add_texture_highlights.cso

echo [15/46] Compiling add_texture_uint16...
%DXC% -T cs_6_0 -E add_texture_uint16 TextureUtilities.hlsl -Fo compiled\add_texture_uint16.cso

echo [16/46] Compiling add_texture_weighted...
%DXC% -T cs_6_0 -E add_texture_weighted TextureUtilities.hlsl -Fo compiled\add_texture_weighted.cso

echo [17/46] Compiling blur_mosaic_texture...
%DXC% -T cs_6_0 -E blur_mosaic_texture TextureUtilities.hlsl -Fo compiled\blur_mosaic_texture.cso

echo [18/46] Compiling calculate_weight_highlights...
%DXC% -T cs_6_0 -E calculate_weight_highlights TextureUtilities.hlsl -Fo compiled\calculate_weight_highlights.cso

echo [19/46] Compiling convert_float_to_uint16...
%DXC% -T cs_6_0 -E convert_float_to_uint16 TextureUtilities.hlsl -Fo compiled\convert_float_to_uint16.cso

echo [20/46] Compiling convert_to_bayer...
%DXC% -T cs_6_0 -E convert_to_bayer TextureUtilities.hlsl -Fo compiled\convert_to_bayer.cso

echo [21/46] Compiling convert_to_rgba...
%DXC% -T cs_6_0 -E convert_to_rgba TextureUtilities.hlsl -Fo compiled\convert_to_rgba.cso

echo [22/46] Compiling copy_texture...
%DXC% -T cs_6_0 -E copy_texture TextureUtilities.hlsl -Fo compiled\copy_texture.cso

echo [23/46] Compiling crop_texture...
%DXC% -T cs_6_0 -E crop_texture TextureUtilities.hlsl -Fo compiled\crop_texture.cso

echo [24/46] Compiling fill_with_zeros...
%DXC% -T cs_6_0 -E fill_with_zeros TextureUtilities.hlsl -Fo compiled\fill_with_zeros.cso

echo [25/46] Compiling find_hotpixels_bayer...
%DXC% -T cs_6_0 -E find_hotpixels_bayer TextureUtilities.hlsl -Fo compiled\find_hotpixels_bayer.cso

echo [26/46] Compiling find_hotpixels_xtrans...
%DXC% -T cs_6_0 -E find_hotpixels_xtrans TextureUtilities.hlsl -Fo compiled\find_hotpixels_xtrans.cso

echo [27/46] Compiling prepare_texture_bayer...
%DXC% -T cs_6_0 -E prepare_texture_bayer TextureUtilities.hlsl -Fo compiled\prepare_texture_bayer.cso

echo [28/46] Compiling prepare_texture_xtrans...
%DXC% -T cs_6_0 -E prepare_texture_xtrans TextureUtilities.hlsl -Fo compiled\prepare_texture_xtrans.cso

echo [29/46] Compiling divide_buffer...
%DXC% -T cs_6_0 -E divide_buffer TextureUtilities.hlsl -Fo compiled\divide_buffer.cso

echo [30/46] Compiling sum_divide_buffer...
%DXC% -T cs_6_0 -E sum_divide_buffer TextureUtilities.hlsl -Fo compiled\sum_divide_buffer.cso

echo [31/46] Compiling normalize_texture...
%DXC% -T cs_6_0 -E normalize_texture TextureUtilities.hlsl -Fo compiled\normalize_texture.cso

echo [32/46] Compiling sum_rect_columns_float...
%DXC% -T cs_6_0 -E sum_rect_columns_float TextureUtilities.hlsl -Fo compiled\sum_rect_columns_float.cso

echo [33/46] Compiling sum_rect_columns_uint...
%DXC% -T cs_6_0 -E sum_rect_columns_uint TextureUtilities.hlsl -Fo compiled\sum_rect_columns_uint.cso

echo [34/46] Compiling sum_row...
%DXC% -T cs_6_0 -E sum_row TextureUtilities.hlsl -Fo compiled\sum_row.cso

echo [35/46] Compiling upsample_bilinear_float...
%DXC% -T cs_6_0 -E upsample_bilinear_float TextureUtilities.hlsl -Fo compiled\upsample_bilinear_float.cso

echo [36/46] Compiling upsample_nearest_int...
%DXC% -T cs_6_0 -E upsample_nearest_int TextureUtilities.hlsl -Fo compiled\upsample_nearest_int.cso

REM ============================================================================
REM Frequency Merge Shaders (8 kernels implemented)
REM ============================================================================
echo.
echo [Frequency Merge Shaders - 8 kernels]
echo ----------------------------------------

echo [37/46] Compiling merge_frequency_domain...
%DXC% -T cs_6_0 -E merge_frequency_domain FrequencyMerge.hlsl -Fo compiled\merge_frequency_domain.cso

echo [38/46] Compiling calculate_abs_diff_rgba...
%DXC% -T cs_6_0 -E calculate_abs_diff_rgba FrequencyMerge.hlsl -Fo compiled\calculate_abs_diff_rgba.cso

echo [39/46] Compiling calculate_highlights_norm_rgba...
%DXC% -T cs_6_0 -E calculate_highlights_norm_rgba FrequencyMerge.hlsl -Fo compiled\calculate_highlights_norm_rgba.cso

echo [40/46] Compiling calculate_mismatch_rgba...
%DXC% -T cs_6_0 -E calculate_mismatch_rgba FrequencyMerge.hlsl -Fo compiled\calculate_mismatch_rgba.cso

echo [41/46] Compiling calculate_rms_rgba...
%DXC% -T cs_6_0 -E calculate_rms_rgba FrequencyMerge.hlsl -Fo compiled\calculate_rms_rgba.cso

echo [42/46] Compiling deconvolute_frequency_domain...
%DXC% -T cs_6_0 -E deconvolute_frequency_domain FrequencyMerge.hlsl -Fo compiled\deconvolute_frequency_domain.cso

echo [43/46] Compiling normalize_mismatch...
%DXC% -T cs_6_0 -E normalize_mismatch FrequencyMerge.hlsl -Fo compiled\normalize_mismatch.cso

echo [44/46] Compiling reduce_artifacts_tile_border...
%DXC% -T cs_6_0 -E reduce_artifacts_tile_border FrequencyMerge.hlsl -Fo compiled\reduce_artifacts_tile_border.cso

REM Note: FFT implementations (forward_fft, backward_fft, forward_dft, backward_dft)
REM require additional implementation work. See FrequencyMerge.hlsl for details.

REM ============================================================================
REM Exposure Correction Shaders (4 kernels)
REM ============================================================================
echo.
echo [Exposure Correction Shaders - 4 kernels]
echo ----------------------------------------

echo [45/46] Compiling correct_exposure...
%DXC% -T cs_6_0 -E correct_exposure ExposureCorrection.hlsl -Fo compiled\correct_exposure.cso

echo [46/46] Compiling correct_exposure_linear...
%DXC% -T cs_6_0 -E correct_exposure_linear ExposureCorrection.hlsl -Fo compiled\correct_exposure_linear.cso

REM Note: max_x and max_y require proper texture binding setup

REM ============================================================================
REM Summary
REM ============================================================================
echo.
echo ========================================
echo Shader compilation complete!
echo ========================================
echo.
echo Total shaders compiled: 46 kernels
echo   - Alignment: 9 kernels
echo   - Spatial Merge: 2 kernels
echo   - Texture Utilities: 25 kernels
echo   - Frequency Merge: 8 kernels
echo   - Exposure Correction: 2 kernels
echo.
echo Output directory: shaders/compiled/*.cso
echo.
echo IMPORTANT NOTES:
echo - FFT shaders (forward/backward) need additional implementation
echo - Copy .cso files to your build output directory
echo - Or embed them as resources in your application
echo.
echo ========================================

pause
