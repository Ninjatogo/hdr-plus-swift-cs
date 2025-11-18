# HDR+ Compute Shaders - Phase 3 Implementation

This directory contains GPU compute shaders for the complete HDR+ image processing pipeline, converted from Metal to HLSL for cross-platform DirectX 12 / Vulkan support.

## 📦 Shader Files

### Constants.hlsli
Common constants and definitions used across all shaders:
- Mathematical constants (PI)
- 16-bit float limits
- Integer limits

### Alignment.hlsl (9 kernels)
Image alignment and warping operations:
- `avg_pool` - Average pooling/downsampling
- `avg_pool_normalization` - Pooling with color channel normalization
- `compute_tile_differences` - Generic tile-based motion estimation
- `compute_tile_differences25` - **NEW** Optimized for 5×5 search (25 candidates)
- `compute_tile_differences_exposure25` - **NEW** With exposure correction
- `find_best_tile_alignment` - Find optimal alignment from candidates
- `warp_texture_bayer` - Apply alignment to warp Bayer pattern images
- `warp_texture_xtrans` - **NEW** Apply alignment for X-Trans sensors
- `correct_upsampling_error` - Refine alignment at pyramid boundaries

### SpatialMerge.hlsl (2 kernels) **NEW**
Spatial domain merging operations:
- `color_difference` - Compute absolute color differences between frames
- `compute_merge_weight` - Calculate robust merging weights for motion detection

### TextureUtilities.hlsl (25 kernels) **NEW**
Comprehensive texture operations and utilities:

**Addition/Merging:**
- `add_texture` - Simple texture addition with averaging
- `add_texture_exposure` - Exposure-dependent weighted addition
- `add_texture_highlights` - Highlight clipping prevention
- `add_texture_uint16` - Add unsigned 16-bit textures
- `add_texture_weighted` - Weighted blend of two textures

**Filtering:**
- `blur_mosaic_texture` - Separable binomial blur for mosaic patterns

**Conversion:**
- `convert_float_to_uint16` - Float to 16-bit unsigned integer
- `convert_to_bayer` - RGBA to Bayer pattern
- `convert_to_rgba` - Bayer pattern to RGBA

**Basic Operations:**
- `copy_texture` - Simple texture copy
- `crop_texture` - Crop with padding offset
- `fill_with_zeros` - Clear texture

**Hot Pixel Detection:**
- `find_hotpixels_bayer` - Detect hot pixels in Bayer images
- `find_hotpixels_xtrans` - Detect hot pixels in X-Trans images

**Preprocessing:**
- `prepare_texture_bayer` - Prepare Bayer texture (conversion, correction, padding)
- `prepare_texture_xtrans` - Prepare X-Trans texture

**Math Operations:**
- `divide_buffer` - Divide buffer by scalar
- `sum_divide_buffer` - Sum buffer and divide
- `normalize_texture` - Normalize by normalization texture
- `sum_rect_columns_float` - Sum columns in rectangle (float)
- `sum_rect_columns_uint` - Sum columns in rectangle (uint)
- `sum_row` - Sum texture values along rows

**Resampling:**
- `upsample_bilinear_float` - Bilinear interpolation upsampling
- `upsample_nearest_int` - Nearest neighbor upsampling (integer)

**Highlight Protection:**
- `calculate_weight_highlights` - Calculate highlight protection weights

### FrequencyMerge.hlsl (8 kernels implemented) **NEW**
Frequency domain merging with FFT and Wiener filtering:
- `merge_frequency_domain` - Main frequency-domain merging with Wiener filtering
- `calculate_abs_diff_rgba` - Calculate absolute differences for RGBA
- `calculate_highlights_norm_rgba` - Calculate highlight normalization
- `calculate_mismatch_rgba` - Calculate motion mismatch ratio
- `calculate_rms_rgba` - RMS noise estimation
- `deconvolute_frequency_domain` - Frequency domain deconvolution/sharpening
- `normalize_mismatch` - Normalize mismatch by mean value
- `reduce_artifacts_tile_border` - Reduce tile boundary artifacts

**Note:** FFT implementations (forward_fft, backward_fft, forward_dft, backward_dft) require additional complex implementation from the Metal versions.

### ExposureCorrection.hlsl (4 kernels) **NEW**
Exposure correction and tone mapping:
- `correct_exposure` - Reinhard tone mapping with exposure correction
- `correct_exposure_linear` - Simple linear exposure correction
- `max_x` - Find maximum value along x-axis
- `max_y` - Find maximum value along y-axis

## 🚀 Building Shaders

### Windows (DirectX 12)

```batch
cd shaders
build_shaders.bat
```

**Requirements:**
- Windows SDK with DXC (DirectX Shader Compiler)
- Download: https://developer.microsoft.com/windows/downloads/windows-sdk/

**Output:** `compiled/*.cso` files (46 shader kernels)

### Linux/macOS (Vulkan - Planned)

```bash
# Using DXC with SPIR-V backend
dxc -spirv -T cs_6_0 -E <entry_point> <shader_file>.hlsl -Fo <output>.spv
```

## 📊 Statistics

**Total Shaders:** 46 compute kernels
- Alignment: 9 kernels (including 3 new optimized variants)
- Spatial Merge: 2 kernels
- Texture Utilities: 25 kernels
- Frequency Merge: 8 kernels (4 FFT kernels pending)
- Exposure Correction: 4 kernels

**Lines of Code:**
- Alignment.hlsl: ~600 lines
- SpatialMerge.hlsl: ~70 lines
- TextureUtilities.hlsl: ~900 lines
- FrequencyMerge.hlsl: ~400 lines
- ExposureCorrection.hlsl: ~150 lines
- **Total:** ~2,120 lines of HLSL

## 🔧 Integration

Compiled shaders are embedded as resources in the `HdrPlus.Compute` assembly.
The DirectX 12 backend loads them at runtime from embedded resources.

### Usage Example:

```csharp
var device = ComputeDeviceFactory.CreateDefault();
var pipeline = device.CreatePipeline("avg_pool"); // Load from embedded resource
var cmd = device.CreateCommandBuffer();
cmd.SetPipeline(pipeline);
cmd.DispatchThreads(width, height, 1);
device.Submit(cmd);
```

## 📐 Shader Model

- **Target:** Shader Model 6.0 (cs_6_0)
- **Type:** Compute shaders only
- **Thread Group Sizes:**
  - Most kernels: 8×8×1 (64 threads)
  - Tile differences: 4×4×4 (64 threads)
  - Buffer operations: 256×1×1 (256 threads)

## 🔄 Conversion Notes

These shaders are converted from Metal Shading Language (MSL) to HLSL.

### Key Differences Handled:

| Metal | HLSL |
|-------|------|
| `texture2d<T, access::read>` | `RWTexture2D<T>` |
| `texture3d<T, access::write>` | `RWTexture3D<T>` |
| `kernel void func(...)` | `[numthreads(x,y,z)] void func(...)` |
| `[[texture(n)]]` | `: register(u<n>)` |
| `[[buffer(n)]]` | `cbuffer : register(b<n>)` |
| `thread_position_in_grid` | `SV_DispatchThreadID` |
| `constant T&` | `cbuffer { T value; }` |
| `device T*` | `RWBuffer<T>` |

### Metal-Specific Features Adapted:

1. **Half precision** - Metal's `half` type → HLSL `float` (with constants)
2. **Texture sampling** - Metal's `.read()` → HLSL's `[]` indexing
3. **Atomic operations** - Metal's atomic functions → HLSL's Interlocked*
4. **Thread group memory** - `threadgroup` → `groupshared`

## 🎯 Phase 3 Completion Status

✅ **Completed:**
- All alignment shaders (9/9) including optimized variants
- Spatial merge shaders (2/2)
- Texture utility shaders (25/25)
- Core frequency merge shaders (8/8)
- Exposure correction shaders (4/4)
- Build scripts updated for all shaders
- Documentation updated

⚠️ **Remaining Work:**
- FFT implementations (forward_fft, backward_fft, forward_dft, backward_dft)
- These require ~1000+ lines of complex butterfly diagram FFT code
- Can be implemented following the Metal shader patterns
- Alternative: Use optimized FFT library (e.g., DirectCompute FFT)

## 📚 References

- **HDR+ Paper:** [Hasinoff et al., SIGGRAPH Asia 2016](https://graphics.stanford.edu/papers/hdrp/)
- **Night Sight Paper:** [Liba et al., SIGGRAPH Asia 2019](https://graphics.stanford.edu/papers/night-sight-sigasia19/)
- **IPOL Implementation:** [Monod et al., 2021](https://www.ipol.im/pub/art/2021/336/)
- **Reinhard Tone Mapping:** [Reinhard et al., 2002](https://www.cs.utah.edu/docs/techreports/2002/pdf/UUCS-02-001.pdf)

## 🐛 Troubleshooting

### Shader Compilation Errors

```
Error: dxc.exe not found
Solution: Install Windows SDK
```

### Runtime Errors

```
Error: Failed to create pipeline
Solution: Check shader resource bindings match C# code
```

### Performance Issues

- Ensure correct thread group sizes for your GPU
- Profile with GPU profilers (NSight, PIX, RenderDoc)
- Consider work group size optimization for your target hardware

## 🤝 Contributing

When adding new shaders:
1. Follow existing naming conventions
2. Document all kernels in this README
3. Update build_shaders.bat
4. Add entry points to the shader loader in C#
5. Include references to original Metal code

## 📝 License

Same license as the parent project (HDR+ Swift → C# migration).
