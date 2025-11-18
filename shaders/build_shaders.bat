@echo off
REM Build HLSL shaders to compiled shader objects (.cso) for DirectX 12
REM Requires: Windows SDK with dxc.exe (DirectX Shader Compiler)

echo Building HDR+ compute shaders...

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

echo.
echo [1/7] Compiling avg_pool...
%DXC% -T cs_6_0 -E avg_pool Alignment.hlsl -Fo compiled\avg_pool.cso

echo [2/7] Compiling avg_pool_normalization...
%DXC% -T cs_6_0 -E avg_pool_normalization Alignment.hlsl -Fo compiled\avg_pool_normalization.cso

echo [3/7] Compiling compute_tile_differences...
%DXC% -T cs_6_0 -E compute_tile_differences Alignment.hlsl -Fo compiled\compute_tile_differences.cso

echo [4/7] Compiling find_best_tile_alignment...
%DXC% -T cs_6_0 -E find_best_tile_alignment Alignment.hlsl -Fo compiled\find_best_tile_alignment.cso

echo [5/7] Compiling warp_texture_bayer...
%DXC% -T cs_6_0 -E warp_texture_bayer Alignment.hlsl -Fo compiled\warp_texture_bayer.cso

echo [6/7] Compiling correct_upsampling_error...
%DXC% -T cs_6_0 -E correct_upsampling_error Alignment.hlsl -Fo compiled\correct_upsampling_error.cso

echo.
echo ========================================
echo Shader compilation complete!
echo Output: shaders/compiled/*.cso
echo ========================================
echo.
echo Copy these files to your build output directory or embed them as resources.

pause
