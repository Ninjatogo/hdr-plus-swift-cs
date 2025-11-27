# Burst Photo - .NET Migration Status

## 1. Goals
The primary goal is to migrate the existing **Burst Photo** application (Swift/Metal/macOS) to a modern, cross-platform **.NET 8** application.

### Key Objectives
*   **Cross-Platform Support:** Target Windows, Linux, and macOS.
*   **Modern C#:** Leverage the latest .NET features.
*   **Graphics Independence:** Replace Metal with **Vulkan** (via Silk.NET) to ensure broad GPU compatibility.
*   **Shader Portability:** Migrate MSL (Metal Shading Language) shaders to **HLSL** and compile to **SPIR-V** for Vulkan consumption.
*   **Raw Image Handling:** Replace the Adobe DNG SDK with **LibRaw** for broader open-source compatibility and easier integration.
*   **CLI First:** Deliver a robust Command Line Interface (CLI) using `Spectre.Console`.

## 2. Architecture

### Tech Stack
*   **Runtime:** .NET 8
*   **CLI:** Spectre.Console
*   **Graphics/Compute:** Silk.NET (Vulkan bindings)
*   **Shaders:** HLSL -> SPIR-V (Runtime via Silk.NET.Shaderc)
*   **Image I/O:** LibRaw (via Sdcb.LibRaw)

### Project Structure
*   `BurstPhoto.Core`: Domain logic, interfaces (`IRawImageLoader`, `IComputePipeline`).
*   `BurstPhoto.Rendering`: Graphics backend (Vulkan) and shader management.
*   `BurstPhoto.CLI`: Console application entry point.

## 3. Detailed Milestones

### Phase 1: Foundation & Vertical Slice (Completed)
- [x] **Project Setup:** Initialize solution and projects.
- [x] **Core Interfaces:** Define `IRawImageLoader`, `IRawImageWriter`, and `IComputePipeline`.
- [x] **LibRaw Integration:** Implement basic LibRaw reading (via Sdcb.LibRaw).
- [x] **Simple Output:** Implement basic PGM/PPM writer for verification.
- [x] **Vulkan Backend:** Implement `VulkanContext` and basic compute pipeline.
- [x] **Shader Compilation:** Integrated `Silk.NET.Shaderc` for runtime HLSL compilation.
- [x] **Test Shader:** Added `Passthrough.hlsl` shader.
- [x] **CLI:** Implement basic `process` command.

### Phase 2: Logic Port (Detailed Plan)
Focus: Porting the orchestration logic from Swift to C#.
- [ ] **Data Structures:**
    - Verify `TileInfo` and `ProcessingProgress` match Swift logic.
    - Implement `ImageCacheWrapper` equivalent (if needed).
- [ ] **Pipeline Logic (`DenoisePipeline.cs`):**
    - Port `perform_denoising` function from `denoise.swift`.
    - Implement `load_images` equivalent (batch loading).
    - Implement Reference Frame selection logic (Exposure/ISO analysis).
    - Implement Tile Grid calculation.
- [ ] **Integration:**
    - Connect `LibRawLoader` to the pipeline.
    - Ensure data flows correctly from Loader -> Logic -> (Mock) Compute.

### Phase 3: Shaders & Compute (Detailed Plan)
Focus: Porting Metal shaders and implementing Vulkan dispatch.
- [ ] **Shader Translation (HLSL):**
    - `Align.hlsl`: Port `avg_pool`, `compute_tile_differences`, `warp_texture`.
    - `Merge.hlsl`: Port `merge_spatial`, `merge_frequency`.
    - `Exposure.hlsl`: Port exposure correction kernels.
- [ ] **Vulkan Pipeline Implementation:**
    - Implement Buffer/Image allocation helpers (VMA-like or manual).
    - Implement DescriptorSet management (layout, pool, update).
    - Implement Command Buffer recording for each shader pass.
    - Implement Synchronization (Barriers, Fences).

### Phase 4: Refinement & UI
- [ ] **DNG Writing:** Investigate `BitMiracle.LibTiff.NET` or other libraries for robust DNG output.
- [ ] **Performance Tuning:** Optimize memory usage and dispatch sizes.
- [ ] **GUI:** Evaluate Avalonia UI for cross-platform GUI.
