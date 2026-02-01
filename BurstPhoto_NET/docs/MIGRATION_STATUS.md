# Burst Photo - .NET Migration Status

## 1. Executive Summary
**Goal**: Migrate **Burst Photo** (Swift/Metal/macOS) to cross-platform **.NET 10**.

We have successfully achieved a "Vertical Slice" and have mostly completed the core engineering phases (Foundation, Logic, Compute, Raw I/O). The current focus is on **refinement**, **UI implementation**, and **comprehensive cross-platform verification**.

**Current Version:** 1.0 (Beta)
**Date:** 2026-01-21

---

## 2. Documentation Index

To maintain clarity, the documentation is split into focused files:

*   **[ARCHITECTURE.md](./ARCHITECTURE.md)**: System design, stack, and dependencies.
*   **[USAGE.md](./USAGE.md)**: Instructions for building, running, and testing.
*   **[IMPLEMENTATION_DETAILS.md](./IMPLEMENTATION_DETAILS.md)**: Deep dives into specific solutions (DNG SDK, Raw Bayer).
*   **[BACKLOG.md](./BACKLOG.md)**: Known issues, future tasks, and verification items.

---

## 3. Phase Roadmap

### Phase 1: Foundation & Vertical Slice ✅
*Complete.* Basic project structure, core interfaces, Vulkan initialization, and CLI scaffolding are in place.

### Phase 2: Logic Port (C#) ✅
*Complete.* All major CPU-side orchestration logic (reference frame selection, tile grid calculation, exposure analysis) is ported.

### Phase 3: Shaders & Compute (HLSL/SPIR-V) ✅
*Complete.* 100% of Metal kernels ported to HLSL.
- **Alignment**: Coarse-to-fine search 2.5D optimization.
- **Merging**: GPU-weighted accumulation.
- **Denoising**: Frequency domain (FFT/Wiener) and spatial merging.
- **Corrections**: Hot pixel removal, lens shading (via warped textures).

### Phase 4: Raw Bayer/CFA Integration ✅
*Complete.*
- [x] **Input**: Switched to `HurlbertVisionLab.LibRawWrapper` for direct raw buffer access.
- [x] **Output**: Implemented `Adobe DNG SDK` native wrapper for reliable DNG writing.
- [x] **Metadata**: Correctly handling BlackLevel, WhiteLevel, and CFA patterns.

### Phase 5: Refinement & UI (Current Focus) 🚧
- [x] **DNG Writing**: Robust implementation via native SDK.
- [x] **Exposure-Bracketed Merge**: Implemented exposure-aware kernels for HDR bursts.
- [x] **Tone Mapping**: Implemented `correct_exposure` and `correct_exposure_linear` kernels.
- [ ] **Performance Tuning**: Memory management for large bursts.
- [ ] **UI**: Replace CLI with Avalonia UI app.
- [ ] **Linux Support**: Verify Vulkan/LibRaw on Linux.

---

## 4. Current Status Snapshot

| Component | Status | Notes |
|-----------|--------|-------|
| **Core Logic** | 🟢 Stable | Logic matches Swift reference. |
| **GPU Backend** | 🟢 Stable | Vulkan compute pipelines fully operational. |
| **IO - Input** | 🟢 Stable | Reading Raw Bayer data successfully. |
| **IO - Output** | 🟢 Stable | Writing valid DNGs using Adobe SDK. |
| **Spatial Merge** | 🟢 Stable | Fixed robustness calculation bug (2026-01-03). |
| **Frequency Merge** | 🟢 Stable | Fixed padding offset bug - all 4 iterations now working (2026-01-21). |
| **HDR Merge** | 🟢 Stable | Implemented exposure scaling and highlight recovery (2026-01-16). |
| **Tone Mapping** | 🟢 Stable | Implemented Linear and Curve modes (2026-01-17). |
| **Noise Estimation** | ⚠️ Mixed | CPU estimation stable. GPU estimation wired but bugged (outputs 0). |
| **Debug Tools** | 🟢 New | `--debug-dump` flag to save intermediate DNGs (2026-01-19). |
| **UI** | 🔴 Pending | Use CLI `process` command for now. |

---

## 5. Recent Changes

### (2026-01-21) Bug Fix: Padding Offset in Frequency Domain Merge ✅ CRITICAL
- **Issue**: Iterations 1-2 of the 4-iteration frequency merge produced zero FFT output, resulting in 50% quality loss.
- **Root Cause**: `ExecuteConvertToRgba` was receiving fixed `cropMergeX/Y` values instead of iteration-specific `padLeft/padTop` offsets.
  - `prepare_texture_bayer` writes data at `(gid.x + padLeft, gid.y + padTop)` where padding varies per iteration
  - `convert_to_rgba` was reading from wrong location due to incorrect offset parameters
  - Iterations 1-2: 20-pixel offset mismatch (complete miss)
  - Iterations 3-4: 12-pixel offset (worked by coincidence)
- **Fix**: Changed two `ExecuteConvertToRgba` calls (lines 387, 506) to pass `padLeft, padTop` instead of `cropMergeX, cropMergeY`
- **Result**: All 4 iterations now produce identical, correct FFT output (sum=8642301.07)
- **Impact**: Frequency domain merge now achieves 100% quality (was 50%)
- **File**: `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs`
- **Documentation**: See `AGENT_HANDOFF_PADDING_OFFSET_FIX.md` for complete analysis

### (2026-01-19) Bug Fix: RGBA Conversion Shader Logic
- **Issue**: `convert_to_rgba` and `convert_to_bayer` shaders were demosaicing instead of direct packing.
- **Root Cause**: Shaders averaged green channels and used CFA pattern logic, causing data loss.
- **Fix**:
  - `convert_to_rgba`: Changed to direct packing `float4(p0, p1, p2, p3)` without averaging
  - `convert_to_bayer`: Changed to simple positional unpacking based on (x,y) % 2
- **File**: `BurstPhoto.Rendering/Shaders/TextureOps.hlsl` (lines 89-127)
- **Status**: Logic fixed, but runtime execution produces zeros (see BACKLOG.md for active debugging)

### (2026-01-19) Feature: Debug Dump for Intermediate Outputs
- **Goal**: Enable saving intermediate DNG files at pipeline stages to diagnose black output issues.
- **Implementation**: Added `--debug-dump` CLI flag, `DebugDump()` helper in `VulkanComputePipeline.cs`.
- **Stages Captured**: After Prepare, Forward FFT, Merge, Deconvolution, Backward FFT, Exposure Correction.
- **Finding**: Confirmed `step_1_prepare.dng` is valid (16MB), but RGBA conversion produces zeros.

### (2026-01-19) Bug Fix: Spatial Mode Buffer Allocation
- **Issue**: `outHeight` was never assigned in spatial mode, causing buffer allocation failures.
- **Fix**: Added `outHeight = height + tileSize` in `VulkanComputePipeline.cs`.

### (2026-01-17) Feature: Tone Mapping Kernels
- **Goal**: Apply final exposure correction to the merged image.
- **Implementation**: Ported `correct_exposure` (Curve) and `correct_exposure_linear` (Linear) from Swift.
- **Verification**: Verified via CLI with `Linear1EV` setting.

### (2026-01-17) Feature: GPU Noise Estimation (Wired)
- **Goal**: Port full GPU blur → color_difference → texture_mean pipeline from Swift.
- **Implementation**: Created `ExecuteNoiseEstimationGPU()` with blur, diff, and reduction passes.
- **Result**: GPU blur shader outputs zeros - bug under investigation.
- **Workaround**: Using CPU-based `EstimateColorNoise()` which works correctly (~876 noiseSd for test images).
- **Impact**: Processing still works; noise estimation now properly integrated into merge.

### (2026-01-16) Feature: Exposure-Bracketed Merge (HDR)
- **Problem**: Non-uniform bursts (e.g. -1, 0, +1 EV) produced artifacts because exposure differences were treated as motion.
- **Solution**: Ported `add_texture_exposure` and `add_texture_highlights` from Swift.
- **Implementation**: `ExecuteMerge` now detects exposure difference.
    - **Overexposed (Brighter) Frames**: Scaled down to match reference (reduces shadow noise).
    - **Underexposed (Darker) Frames**: Used to recover clipped highlights in the reference.

### (2026-01-03) Bug Fix: Black Output with Certain NR Settings
- **Issue**: Some NoiseReduction/MergingAlgorithm combinations produced corrupt (black) DNG files.
- **Root Cause**: Incorrect port of robustness calculation - C# used two-parameter smoothstep, Swift uses single parameter.
- **Fix**: Updated `MergeSpatial.hlsl` and `CalculateRobustness()` to match Swift's formula.


