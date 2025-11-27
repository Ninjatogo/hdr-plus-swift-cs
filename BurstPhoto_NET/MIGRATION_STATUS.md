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

## 3. Milestones

### Phase 1: Foundation & Vertical Slice (Completed)
- [x] **Project Setup:** Initialize solution and projects.
- [x] **Core Interfaces:** Define `IRawImageLoader`, `IRawImageWriter`, and `IComputePipeline`.
- [x] **LibRaw Integration:** Implement basic LibRaw reading (via Sdcb.LibRaw).
- [x] **Simple Output:** Implement basic PGM/PPM writer for verification.
- [x] **Vulkan Backend:** Implement `VulkanContext` and basic compute pipeline.
- [x] **Shader Compilation:** Integrated `Silk.NET.Shaderc` for runtime HLSL compilation.
- [x] **Test Shader:** Added `Passthrough.hlsl` shader.
- [x] **CLI:** Implement basic `process` command.

### Phase 2: Logic Port (Next Steps)
- [ ] **Logic Port:** Translate `denoise.swift` orchestration logic.
- [ ] **Data Structures:** Port `ProcessingProgress`, `TileInfo`, and helpers.

### Phase 3: Shaders & Compute
- [ ] **Shader Translation:** Convert `align.metal`, `merge.metal`, `exposure.metal` to HLSL.
- [ ] **Advanced Pipeline:** Implement complex dispatch logic in Vulkan.

### Phase 4: Refinement
- [ ] **DNG Writing:** Investigate robust DNG writing libraries.
- [ ] **GUI:** Evaluate Avalonia UI.
