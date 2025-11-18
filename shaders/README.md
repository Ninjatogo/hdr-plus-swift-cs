# HDR+ Compute Shaders

This directory contains GPU compute shaders for the HDR+ image processing pipeline.

## Shader Files

- **Alignment.hlsl** - Image alignment compute kernels (Metal → HLSL conversion)
  - avg_pool - Average pooling/downsampling
  - avg_pool_normalization - Pooling with color channel normalization
  - compute_tile_differences - Tile-based motion estimation
  - find_best_tile_alignment - Find optimal alignment from candidates
  - warp_texture_bayer - Apply alignment to warp images (Bayer pattern)
  - correct_upsampling_error - Refine alignment at pyramid boundaries

## Building Shaders

### Windows (DirectX 12)

```batch
cd shaders
build_shaders.bat
```

Requires: Windows SDK with DXC (DirectX Shader Compiler)

Output: `compiled/*.cso` files

### Linux/macOS (Vulkan - Coming Soon)

```bash
# Using glslc or spirv-cross to compile HLSL → SPIR-V
glslc -fshader-stage=compute Alignment.hlsl -o Alignment.spv
```

## Integration

Compiled shaders are embedded as resources in the `HdrPlus.Compute` assembly.
The DirectX 12 backend loads them at runtime from embedded resources.

## Shader Model

- **Target:** Shader Model 6.0 (cs_6_0)
- **Type:** Compute shaders only
- **Thread Group Size:** Varies by shader (typically 8×8×1 or 4×4×4)

## Conversion Notes

These shaders are converted from Metal Shading Language (MSL) to HLSL.

Key differences handled:
- `texture2d<T, access::read>` (Metal) → `RWTexture2D<T>` (HLSL)
- `kernel void` (Metal) → `[numthreads(x,y,z)] void` (HLSL)
- `[[texture(n)]]` (Metal) → `: register(u<n>)` (HLSL)
- `[[buffer(n)]]` (Metal) → `cbuffer : register(b<n>)` (HLSL)
- `thread_position_in_grid` (Metal) → `SV_DispatchThreadID` (HLSL)

## Future Work

- [ ] Spatial merge shaders
- [ ] Frequency domain merge shaders (FFT-based)
- [ ] Exposure correction shaders
- [ ] Texture utility shaders (blur, upsample, etc.)
- [ ] X-Trans sensor support (6×6 mosaic pattern)
