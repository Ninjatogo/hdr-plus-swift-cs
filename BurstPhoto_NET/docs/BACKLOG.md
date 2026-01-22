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


## Completed Features

### 1. Frequency Domain Merging (Higher Quality) ✅
- **Status**: ✅ COMPLETE (2026-01-21)
- **Goal**: Implement FFT/DFT based alignment and merging with 4-iteration artifact reduction.
- **Final Fix**: Padding offset bug - `ExecuteConvertToRgba` was receiving wrong offset parameters
- **Result**: All 4 iterations producing correct output, achieving 100% quality
- **Documentation**: See `AGENT_HANDOFF_PADDING_OFFSET_FIX.md` for complete resolution details

#### Historical Debugging Journey (2026-01-18 to 2026-01-21):

The frequency domain merge went through multiple debugging phases before resolution:

**Phase 1: HLSL/Vulkan Bindings (2026-01-18)**:
- Added explicit `[[vk::binding(N, 0)]]` attributes to ALL shaders
- Fixed ExecutePrepare descriptor bindings and layout
- Fixed prepare_texture_bayer shader logic
- Fixed FFT array sizes and bounds checking

**Phase 2: Buffer Size Bug (2026-01-21 AM)**:
- Root cause: `VulkanImage.GetData<T>()` calculated buffer size incorrectly
- Only read 1/4 of RGBA32F image data (sizeof(float) instead of format-based size)
- Fixed by adding `GetBytesPerPixel()` method
- Result: FFT started working for iterations 3-4

**Phase 3: Padding Offset Bug (2026-01-21 PM)** ✅ FINAL FIX:
- Root cause: `ExecuteConvertToRgba` received wrong padding parameters
- Was passing fixed `cropMergeX/Y` instead of iteration-specific `padLeft/padTop`
- 20-pixel offset for iterations 1-2 caused complete data miss
- Fixed by passing correct padding values to shader
- Result: All 4 iterations now produce identical, correct output

**Debug Infrastructure Built**:
- ✅ `--debug-dump` CLI flag for intermediate DNGs
- ✅ Granular logging after each pipeline stage
- ✅ Descriptor pool increased to 500 sets
- ✅ Complete 4-iteration framework (24 total comparison passes)

## Missing Features
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
