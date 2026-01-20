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
- **Status**: Partially Working - Iteration 1 Prepare & RGBA Work, FFT Broken (2026-01-20 Evening)
- **Goal**: Implement FFT/DFT based alignment and merging with 4-iteration artifact reduction.

#### Session Summary (2026-01-20 Evening):

**Goal**: Fix Forward FFT zero output issue
**Result**: FFT issue persists despite extensive debugging. Prepare/RGBA remain working for iteration 1.

#### Critical Fixes Applied (2026-01-20):

| Fix | Description | Status |
|-----|-------------|--------|
| **HLSL Vulkan Bindings** | Added explicit `[[vk::binding(N, 0)]]` attributes to ALL shaders to fix register-to-binding mapping | ✅ Fixed |
| **ExecutePrepare Bindings** | Fixed descriptor bindings: input at Binding 2 (t1), weight at Binding 4 (t3), black levels at Binding 6 (t5) | ✅ Fixed |
| **ExecutePrepare Layout** | Fixed descriptor layout: Binding 4 as SampledImage (not StorageBuffer), added Binding 6 for BlackLevels | ✅ Fixed |
| **ExecutePrepare Dispatch** | Changed from output dimensions to input dimensions (matching Metal's dispatch pattern) | ✅ Fixed |
| **prepare_texture_bayer Shader** | Completely rewrote to match Metal: read from gid, write to gid+padding (VulkanComputePipeline.cs:2330, TextureOps.hlsl:370-414) | ✅ Fixed |
| **FFT Array Sizes** | Fixed tmp_data[64→16], tmp_tile[80] to match Metal (MergeFrequency.hlsl:73-74) | ✅ Fixed |
| **FFT Bounds Checking** | Added bounds checks to forward_fft, backward_fft, deconvolute_frequency_domain, merge_frequency_domain | ✅ Added (Note: bounds check temporarily removed during debugging) |
| **Frequency Layout Comments** | Clarified descriptor layout comments to match actual shader bindings (t1→Binding1, etc.) | ✅ Fixed |

#### Files Modified (2026-01-20):
- `TextureOps.hlsl`: Added `[[vk::binding]]` attributes, rewrote `prepare_texture_bayer`
- `Align.hlsl`: Added `[[vk::binding]]` attributes
- `MergeSpatial.hlsl`: Added `[[vk::binding]]` attributes
- `MergeFrequency.hlsl`: Added `[[vk::binding]]` attributes, fixed FFT array sizes, added bounds checks (later removed for debugging)
- `Exposure.hlsl`: Added `[[vk::binding]]` attributes
- `VulkanComputePipeline.cs`: Fixed ExecutePrepare bindings/layout/dispatch; clarified frequency layout comments

#### Test Results (2026-01-20 Evening):
```
Iteration 1:
✅ Raw input data: sum=133507466, mean=13350.75
✅ After prepare: sum=5158260, mean=5158.26
✅ After convert_to_rgba: sum=7250654, mean=725.07
❌ After forward_fft: sum=0.00, mean=0.00

Iterations 2-4:
❌ After prepare: sum=0.00, mean=0.00
```

**Progress**: Prepare and RGBA conversion confirmed working for Iteration 1!

#### Remaining Issues (BLOCKERS):

1. **Forward FFT Producing Zeros** (HIGH PRIORITY - BLOCKER):
   - **Input**: Valid RGBA data (sum=7,250,654)
   - **Output**: All zeros (sum=0.00)
   - **Debugging Attempted**:
     - ✅ Verified descriptor bindings correct (RefTexture at Binding 1, OutputTexture at Binding 10)
     - ✅ Verified shader compiles without errors
     - ✅ Verified dispatch dimensions correct (17×13 groups = 258×194 threads)
     - ✅ Verified layout transitions correct (both textures in ImageLayout.General)
     - ✅ Verified synchronization (QueueWaitIdle between commands)
     - ❌ Even simple pixel copy from input→output produces zeros
     - ❌ Test pattern writes (float4(999, 888, 777, 666)) produce zeros
   - **Suspected Causes**:
     - Shader not executing properly (despite no Vulkan errors)
     - Descriptor binding mismatch not yet identified
     - Pipeline state issue
     - Unknown Vulkan issue
   - **Location**: `forward_fft` entry point → `forward_fft_impl` in MergeFrequency.hlsl:61-150
   - **Note**: ExecuteForwardFft bindings look correct per code inspection

2. **Iterations 2-4 Prepare Failure** (HIGH PRIORITY - BLOCKER):
   - Iteration 1 prepare works, iterations 2-4 produce zeros
   - Likely cause: Texture re-upload or padding parameter issues
   - Different spatial shifts per iteration may expose bugs
   - Location: VulkanComputePipeline.cs:357-358

#### Historical Work:

**Fixes Applied (2026-01-18)**:
- Texture dimensions for RMS, mismatch, highlights (nTilesX × nTilesY)
- Hardcoded `tile_size_merge = 8` for FFT
- HLSL registers changed to `register(t1-t5, u10)`
- Robustness formula ported from Swift
- FFT tile sizes fixed

**4-Iteration Framework (2026-01-20)**:
- ✅ Framework complete (4 iterations × 6 comparisons = 24 total)
- ✅ No crashes, descriptor exhaustion, or Vulkan errors
- ✅ Processing time: ~14 seconds
- ✅ Descriptor pool increased to 500 sets

**Debug Infrastructure**:
- ✅ `--debug-dump` CLI flag implemented
- ✅ Intermediate DNG saving (step_1_prepare, step_6_exposure, etc.)
- ✅ Granular debug logging after each pipeline stage

#### Next Steps:
1. **DEBUG: Fix Forward FFT zero output** - BLOCKER
   - Investigate why even simple writes to OutputTexture produce zeros
   - Consider using Vulkan validation layers to catch errors
   - Check if there's a pipeline state or shader module issue
   - Verify SPIR-V bytecode is valid
   - Try using RenderDoc or similar to inspect actual Vulkan state
2. **DEBUG: Fix prepare stage for iterations 2-4** - BLOCKER
   - Debug texture re-upload logic
   - Verify padding parameters for different spatial shifts
3. Verify backward FFT implementation
4. Verify deconvolution implementation
5. Compare shader implementations line-by-line against Swift reference

**📖 See [TROUBLESHOOTING_FREQUENCY_DOMAIN.md](TROUBLESHOOTING_FREQUENCY_DOMAIN.md) for detailed debugging strategy.**

### 2. Full Exposure Control (Tone Mapping)
- **Status**: ✅ Implemented (2026-01-17)
- **Goal**: Implement linear and non-linear tone mapping shaders.
- **Details**: Ported `correct_exposure` and `correct_exposure_linear` kernels, fully integrated.

### 3. Temporal Averaging (Noise Reduction Max)
- **Status**: Pending
- **Goal**: Implement `calculate_temporal_average` for static scenes (Noise Reduction = 23)
- **Details**: Simple averaging without alignment

### 4. GPU-Based Noise Estimation
- **Status**: Wired / Bugged
- **Goal**: Port Swift's full GPU blur → color_difference → texture_mean pipeline
- **Details**:
  - Fully wired in `VulkanComputePipeline.cs` (`ExecuteNoiseEstimationGPU`)
  - **Known Bug**: `blur_mosaic_texture` shader writes all zeros despite correct params
  - Currently using CPU fallback (stable)

## Known Issues

### Resolved
| Issue | Resolution |
|-------|------------|
| DNG Write Overflow | Fixed by switching to Adobe DNG SDK native wrapper |
| CFA Pattern Order | Fixed via granular try-catch in `ExtractExifMetadata` |
| GCHandle Dispose | Fixed via try-catch/Recycle loop in `LibRawLoader` |
| **Black Output Bug** | Fixed incorrect robustness calculation in `MergeSpatial.hlsl` |
| **HDR Merge** | Implemented exposure-bracketed merge kernels |
| **Tone Mapping** | Implemented `correct_exposure` kernels |
| **HLSL-Vulkan Binding Mismatch** | Fixed by adding explicit `[[vk::binding]]` attributes to all shaders (2026-01-20) |
| **Prepare Stage Iteration 1** | Fixed dispatch dimensions, descriptor bindings, and shader logic (2026-01-20) |
| **RGBA Conversion Iteration 1** | Fixed by correcting prepare stage (2026-01-20) |

### Active / To Watch
- **NuGet Warnings**: `NU1701` for C++/CLI wrapper on .NET Core (safe to ignore, should suppress in csproj)
- **Forward FFT Zero Output (2026-01-20)**:
  - **Symptom**: FFT shader produces all-zero output despite valid RGBA input. Even simple test writes produce zeros.
  - **Impact**: Blocks entire frequency domain merge pipeline
  - **Status**: Extensive debugging performed, root cause not yet identified
  - **Priority**: CRITICAL BLOCKER
  - **Debugging Notes**: Descriptor bindings verified, shader compiles, dispatch dimensions correct, layouts correct, synchronization in place. Issue appears fundamental to shader execution or texture access.
- **Iterations 2-4 Prepare Failure (2026-01-20)**:
  - **Symptom**: Prepare stage works for iteration 1, fails for 2-4
  - **Impact**: Only 1 of 4 iterations produces valid data
  - **Status**: Under investigation (blocked by FFT issue)
  - **Priority**: HIGH

## Items Needing Verification

| Item | Status | Notes |
|------|--------|-------|
| **Tile grid calculation** | ⚠️ Unverified | Formula implemented, needs comparison against Swift output |
| **X-Trans Support** | ⚠️ Unverified | Code implemented, no Fujifilm test files available |
| **Memory Usage** | ⚠️ Unknown | Not tested with large bursts (>10 images) |
| **Output Quality** | ⚠️ Partial | Visual check okay, need numerical comparison with Swift |
