# Architecture

## Project Structure
- `BurstPhoto.Core`: Domain logic, interfaces (`IRawImageLoader`, `IComputePipeline`).
- `BurstPhoto.Rendering`: Vulkan backend and shader management.
- `BurstPhoto.CLI`: Console application entry point.
- `BurstPhoto.Native`: C++ DLL wrapper for Adobe DNG SDK.

## Technology Stack

| Objective | Stack |
|-----------|-------|
| **Cross-Platform** | Windows, Linux, macOS (Target) |
| **Graphics** | Vulkan via Silk.NET |
| **Shaders** | HLSL → SPIR-V (via Silk.NET.Shaderc) |
| **Raw I/O** | HurlbertVisionLab.LibRawWrapper (Raw Bayer) + Sdcb.LibRaw (Fallback) |
| **DNG Writing** | Adobe DNG SDK (Native C++ Wrapper) |
| **CLI** | Spectre.Console |

## Key Dependencies

| Package | Purpose |
|---------|---------|
| **HurlbertVisionLab.LibRawWrapper** | Direct access to raw Bayer/CFA data via `RawData.Buffer`. |
| **Adobe DNG SDK** | Industry-standard DNG writing and metadata handling (via `BurstPhoto.Native` wrapper). |
| **Sdcb.LibRaw** | Secondary loader used for comparative analysis or fallback. |
| **MetadataExtractor** | Efficient reading of EXIF/DNG tags without full image decoding. |
| **Silk.NET** | Low-level bindings for Vulkan and Shaderc. |
| **Spectre.Console** | Rich terminal UI for the CLI. |
| **xunit** | Unit testing framework. |
