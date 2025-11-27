# Burst Photo - Porting Plan

## 1. Goals
The primary goal is to migrate the existing **Burst Photo** application (Swift/Metal/macOS) to a modern, cross-platform **.NET 8/9** application.

### Key Objectives
*   **Cross-Platform Support:** Target Windows, Linux, and macOS.
*   **Modern C#:** Leverage the latest .NET features.
*   **Graphics Independence:** Replace Metal with **Vulkan** (via Silk.NET) to ensure broad GPU compatibility.
*   **Shader Portability:** Migrate MSL (Metal Shading Language) shaders to **HLSL** and compile to **SPIR-V** for Vulkan consumption.
*   **Raw Image Handling:** Replace the Adobe DNG SDK with **LibRaw** for broader open-source compatibility and easier integration.
*   **CLI First:** Deliver a robust Command Line Interface (CLI) using `Spectre.Console` first, laying the groundwork for a future Avalonia UI.

## 2. Architecture

### Tech Stack
*   **Runtime:** .NET 8 or 9
*   **UI (Future):** Avalonia UI
*   **CLI:** Spectre.Console
*   **Graphics/Compute:** Silk.NET (Vulkan bindings)
*   **Shaders:** HLSL -> SPIR-V (via DXC)
*   **Image I/O:** LibRaw (via P/Invoke or bindings)

### Project Structure
*   `BurstPhoto.Core`: Contains the domain logic, algorithm implementations (orchestration), and interfaces (`IRawImageLoader`, `IComputePipeline`).
*   `BurstPhoto.Rendering`: Implementation of the graphics backend (Vulkan) and shader management.
*   `BurstPhoto.CLI`: The entry point for the console application, handling user input and progress visualization.
*   `BurstPhoto.UI` (Future): The Avalonia-based GUI.

## 3. Milestones

### Phase 1: Foundation & Logic (Current Focus)
- [ ] **Project Setup:** Initialize solution and projects.
- [ ] **Core Interfaces:** Define `IRawImageLoader`, `IRawImageWriter`, and `IComputePipeline`.
- [ ] **Logic Port:** Translate `denoise.swift` orchestration logic to C# (`DenoisePipeline`).
- [ ] **Data Structures:** Port `ProcessingProgress`, `TileInfo`, and other helpers.

### Phase 2: Input/Output
- [ ] **LibRaw Integration:** Implement DNG reading using LibRaw.
- [ ] **DNG Writing:** Implement DNG writing (either via LibRaw or custom DNG writer if needed for specific output requirements).

### Phase 3: Shaders & Compute
- [ ] **Shader Translation:** Convert all `.metal` shaders (`align`, `merge`, `exposure`) to HLSL.
- [ ] **SPIR-V Compilation:** Setup build process/tools to compile HLSL to SPIR-V.
- [ ] **Vulkan Backend:** Implement `VulkanComputePipeline` to handle resource management and compute dispatch.

### Phase 4: CLI & Integration
- [ ] **CLI UX:** Build the command-line interface with argument parsing and progress bars.
- [ ] **Integration:** Wire up Loader -> Pipeline -> Writer.
- [ ] **Verification:** Verify output against known good results (if possible) or ensure pipeline completes without errors.

### Phase 5: GUI (Future)
- [ ] **Avalonia Setup:** Create the UI project.
- [ ] **View Implementation:** Port SwiftUI views to Avalonia XAML/C#.
- [ ] **Interactivity:** Connect UI controls to the `DenoisePipeline`.
