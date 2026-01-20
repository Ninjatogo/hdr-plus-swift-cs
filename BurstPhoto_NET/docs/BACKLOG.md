# Backlog & Known Issues

## Future Work

### High Priority
- [ ] **Cross-Platform Verification**: Verify behavior on Linux (may need different LibRaw binaries).
- [ ] **Test Coverage**: specific unit tests for `LibRawLoader` raw Bayer extraction.

### Lower Priority
- [ ] **Performance Profiling**: Analyze the cost of current dispatch sizes and memory usage.
- [ ] **GUI**: Implement a graphical interface using Avalonia UI.
- [ ] **Technical Debt**: Remove `Sdcb.LibRaw` dependency once strictly unused (currently kept for potential fallback).
- [ ] Optimize memory usage for high-resolution bursts.
- [ ] Add support for more raw formats.


## Missing Features

### 1. Frequency Domain Merging (Higher Quality)
- **Status**: Black Output Confirmed (2026-01-19).
- **Goal**: Implement FFT/DFT based alignment and merging.

#### Work Done (2026-01-17/18):
- Ported `forward_fft`, `backward_fft`, `merge_frequency_domain`, `deconvolute_frequency_domain`, `reduce_artifacts_tile_border` kernels to `MergeFrequency.hlsl`.
- Implemented `EnsureMergeFrequencyPipeline`, `ExecuteMergeFrequency`, `ExecuteForwardFft`, `ExecuteBackwardFft`, `ExecuteCopyImage`, `ExecuteDeconvoluteFrequency`, `ExecuteReduceArtifacts` in `VulkanComputePipeline.cs`.

#### Fixes Applied (2026-01-18):
| Fix | Description |
|-----|-------------|
| Texture Dimensions | `texRms`, `texMismatch`, `texHighlights` now allocated at tile-grid size (nTilesX × nTilesY) |
| Tile Size | Hardcoded `tile_size_merge = 8` for FFT (matching Swift) |
| HLSL Registers | Changed to `register(t1-t5, u10)` to match C# Vulkan bindings |
| Robustness Formula | Ported Swift's formula: `robustness_rev = 0.5 * (26.5 or 28.5 - noise_reduction)` |
| FFT Tile Sizes | Fixed forward/backward FFT to use `tile_size_merge` instead of alignment `tileSize` |
| Deconvolution | Added `ExecuteDeconvoluteFrequency()` dispatch (was compiled but never called) |
| Tile Border Reduction | Added `ExecuteReduceArtifacts()` dispatch (was compiled but never called) |

#### Confirmed via Debug Testing (2026-01-19):
- **Debug Dump Feature**: Implemented `--debug-dump` CLI flag to save intermediate DNGs ✅
- **Observation**: `step_1_prepare.dng` is 16MB (valid), but `step_5_back_fft.dng` is only 1.6MB (black/zeros)
- **Conclusion**: Data is lost during FFT processing, not before or after

#### Confirmed Root Cause:
**Missing RGBA Conversion** - Swift's `convert_to_rgba()` converts single-channel Bayer to RGBA superpixels before FFT. Our code passes R32Sfloat textures, meaning the FFT shader reads only 1/4 of the expected data.

#### Next Steps:
1. ~~Implement texture dump debugging~~ ✅ Done (2026-01-19)
2. **Port `convert_to_rgba` and `convert_to_bayer` shaders** - HIGH PRIORITY
3. Verify FFT output matches Swift reference

### 1b. RGBA Conversion for Frequency Domain (Deferred)
- **Status**: Pending (Deferred to future task).
- **Goal**: Port `convert_to_rgba` and `convert_to_bayer` shaders from Swift.
- **Priority**: High - likely root cause of current 11% output size issue.
- **Details**:
    - Swift converts single-channel Bayer image to RGBA format before FFT processing.
    - The FFT shaders expect `float4` data (RGBA channels processed in parallel via SIMD).
    - Currently, we pass `R32Sfloat` (single-channel) textures (though allocated as `R32G32B32A32Sfloat`).
    - This causes data loss: only 1/4 of pixels are being processed correctly.
- **Swift Reference**: `texture/texture.swift` → `convert_to_rgba()`, `convert_to_bayer()`.


### 2. Full Exposure Control (Tone Mapping)
### 2. Full Exposure Control (Tone Mapping)
- **Status**: Implemented (2026-01-17).
- **Goal**: Implement linear and non-linear tone mapping shaders.
- **Details**:
    - Ported `correct_exposure` and `correct_exposure_linear` kernels.
    - Fully integrated into `VulkanComputePipeline` and verified via CLI.

### 3. Temporal Averaging (Noise Reduction Max)
- **Status**: Pending.
- **Goal**: Implement `calculate_temporal_average` for static scenes (Noise Reduction = 23).
- **Details**: Simple averaging without alignment.

### 4. GPU-Based Noise Estimation
### 4. GPU-Based Noise Estimation
- **Status**: Wired / Bugged.
- **Goal**: Port Swift's full GPU blur → color_difference → texture_mean pipeline.
- **Details**:
    - Fully wired in `VulkanComputePipeline.cs` (`ExecuteNoiseEstimationGPU`).
    - **Known Bug**: `blur_mosaic_texture` shader writes all zeros despite correct params.
    - Currently using CPU fallback (stable).

## Known Issues

### Resolved
| Issue | Resolution |
|-------|------------|
| DNG Write Overflow | Fixed by switching to Adobe DNG SDK native wrapper. |
| CFA Pattern Order | Fixed via granular try-catch in `ExtractExifMetadata`. |
| GCHandle Dispose | Fixed via try-catch/Recycle loop in `LibRawLoader`. |
| **Black Output Bug** | Fixed incorrect robustness calculation in `MergeSpatial.hlsl`. |
| **Black Output Bug** | Fixed incorrect robustness calculation in `MergeSpatial.hlsl`. |
| **HDR Merge** | Implemented exposure-bracketed merge kernels (`add_texture_exposure`, `highlights`). |
| **Tone Mapping** | Implemented `correct_exposure` and `correct_exposure_linear` kernels. |


### Active / To Watch
- **NuGet Warnings**: We see `NU1701` for the C++/CLI wrapper on .NET Core. This is safe to ignore but should be suppressed in csproj.
- **Frequency Domain Black Output**: Fully wired but produces black images. Needs intermediate texture debugging.

## Items Needing Verification

| Item | Status | Notes |
|------|--------|-------|
| **Tile grid calculation** | ⚠️ Unverified | Formula implemented, needs comparison against Swift output. |
| **X-Trans Support** | ⚠️ Unverified | Code implemented, but no Fujifilm test files available. |
| **Memory Usage** | ⚠️ Unknown | Not tested with large bursts (>10 images). |
| **Output Quality** | ⚠️ Partial | Visual check is okay, need distinct numerical comparison with Swift ref. |
