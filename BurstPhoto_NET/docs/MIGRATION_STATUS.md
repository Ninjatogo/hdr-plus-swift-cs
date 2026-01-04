# Burst Photo - .NET Migration Status

## 1. Executive Summary
**Goal**: Migrate **Burst Photo** (Swift/Metal/macOS) to cross-platform **.NET 10**.

We have successfully achieved a "Vertical Slice" and have mostly completed the core engineering phases (Foundation, Logic, Compute, Raw I/O). The current focus is on **refinement**, **UI implementation**, and **comprehensive cross-platform verification**.

**Current Version:** 0.8 (Alpha)
**Date:** 2026-01-03

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
| **Noise Estimation** | 🟡 Partial | CPU-based estimation implemented; GPU version prepared. |
| **Bracketed HDR Merge** | 🔴 Not Implemented | Exposure-weighted merge path needed. |
| **UI** | 🔴 Pending | Use CLI `process` command for now. |

---

## 5. Recent Changes (2026-01-03)

### Bug Fix: Black Output with Certain NR Settings
- **Issue**: Some NoiseReduction/MergingAlgorithm combinations produced corrupt (black) DNG files.
- **Root Cause**: Incorrect port of robustness calculation - C# used two-parameter smoothstep, Swift uses single parameter.
- **Fix**: Updated `MergeSpatial.hlsl` and `CalculateRobustness()` to match Swift's formula.

### New: Noise Estimation
- Added `EstimateColorNoise()` method that samples reference texture to compute noise standard deviation.
- Passes calculated `NoiseSd` to merge shader for proper weight calculation.

