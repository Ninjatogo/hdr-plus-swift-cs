# Burst Photo .NET

A cross-platform C#/.NET port of **Burst Photo**, implementing a simplified version of HDR+, the computational photography pipeline used in Google Pixel phones. This application processes a burst of RAW images to increase dynamic range and reduce noise.

## Credits

This project is a port of the original **[Burst Photo](https://github.com/martin-marek/hdr-plus-swift)** by **[Martin Marek](https://github.com/martin-marek)**. The original application was written in Swift/SwiftUI/Metal for macOS. This port brings the same algorithms to Windows (and eventually Linux/macOS) using C#, Vulkan, and the .NET runtime.

If you're on macOS and prefer native performance, check out the original:
- **Original Repository**: [github.com/martin-marek/hdr-plus-swift](https://github.com/martin-marek/hdr-plus-swift)
- **Website**: [burst.photo](https://burst.photo/)
- **Mac App Store**: [Download](https://burst.photo/download/)

For researchers or those who prefer Python/PyTorch, Martin also maintains [hdr-plus-pytorch](https://github.com/martin-marek/hdr-plus-pytorch).

## Background

You can read more about HDR+ in Google's paper: [Burst photography for high dynamic range and low-light imaging on mobile cameras](http://static.googleusercontent.com/media/www.hdrplusdata.org/en//hdrplus.pdf).

## Example

In the example below, a burst of 51 images was taken at ISO 51,200 on a Sony A7S III camera. Exposure was adjusted to taste with equal settings for both images. The comparison shows a single image from the burst versus a merge of all images.

![](docs/assets/images/home/monika_stars.jpg)

For more examples, visit [burst.photo/gallery/](https://burst.photo/gallery/).

## Technology Stack

| Component | Technology |
|-----------|------------|
| **Runtime** | .NET 10 (cross-platform) |
| **GPU Compute** | Vulkan via Silk.NET |
| **Shaders** | HLSL compiled to SPIR-V |
| **RAW Input** | LibRaw (HurlbertVisionLab.LibRawWrapper) |
| **DNG Output** | Adobe DNG SDK (native C++ wrapper) |
| **CLI** | Spectre.Console |

## Project Structure

```
BurstPhoto_NET/
├── BurstPhoto.Core/       # Domain logic and interfaces
├── BurstPhoto.Rendering/  # Vulkan backend and shaders
├── BurstPhoto.CLI/        # Command-line application
├── BurstPhoto.GUI/        # Avalonia desktop application
├── BurstPhoto.Native/     # C++ wrapper for Adobe DNG SDK
└── BurstPhoto.Tests/      # Unit tests
```

## Prerequisites

- **.NET 10 SDK** (or .NET 8/9)
- **Vulkan Drivers**:
  - **Windows**: Standard GPU drivers (NVIDIA, AMD, Intel)
  - **Linux**: `vulkan-tools`, `mesa-vulkan-drivers`
  - **macOS**: MoltenVK

## Building

```bash
cd BurstPhoto_NET
dotnet build
```

## Usage

### GUI Application

Launch the graphical interface:

```bash
dotnet run --project BurstPhoto.GUI
```

### CLI - Process a Burst

```bash
dotnet run --project BurstPhoto.CLI -- process <INPUT_DNG_1> <INPUT_DNG_2> ... --output <OUTPUT_DNG>
```

**Example:**
```bash
dotnet run --project BurstPhoto.CLI -- process "Burst Samples\image_001.DNG" "Burst Samples\image_002.DNG" "Burst Samples\image_003.DNG" -o merged.dng
```

The application will automatically select a reference frame and align/merge the burst.

### Debug Mode

Save intermediate DNG files at various pipeline stages for troubleshooting:

```bash
dotnet run --project BurstPhoto.CLI -- process <INPUT_DNGs> -o <OUTPUT> --debug-dump
```

### Running Tests

```bash
dotnet test BurstPhoto_NET/BurstPhoto.Tests/BurstPhoto.Tests.csproj
```

## Features

### Ported from Original
- [x] DNG input/output (via Adobe DNG SDK)
- [x] Simple temporal averaging
- [x] Motion-robust merge in spatial domain
- [x] Motion-robust merge in frequency domain (higher quality)
- [x] Bayer sensor support
- [x] Support for bursts with bracketed exposure (HDR)
- [x] Optional exposure correction
- [x] Hot pixel suppression

### .NET Port Additions
- [x] Cross-platform Vulkan compute backend
- [x] HLSL shaders compiled to SPIR-V
- [x] CLI with progress indicators
- [x] Debug dump for intermediate outputs
- [x] Avalonia GUI (desktop application)

### Planned
- [ ] Linux verification
- [ ] macOS verification (via MoltenVK)
- [ ] Non-Bayer sensor support (X-Trans)

## Current Status

**Version:** 1.0 Beta

| Component | Status |
|-----------|--------|
| Core Logic | Stable |
| GPU Backend (Vulkan) | Stable |
| RAW Input | Stable |
| DNG Output | Stable |
| Spatial Merge | Stable |
| Frequency Merge | Stable |
| HDR/Bracketed Merge | Stable |
| Tone Mapping | Stable |
| GUI (Avalonia) | Stable |

For detailed status and recent changes, see [MIGRATION_STATUS.md](BurstPhoto_NET/docs/MIGRATION_STATUS.md).

## Documentation

- [Architecture](BurstPhoto_NET/docs/ARCHITECTURE.md) - System design and dependencies
- [Usage Guide](BurstPhoto_NET/docs/USAGE.md) - Detailed CLI instructions
- [Implementation Details](BurstPhoto_NET/docs/IMPLEMENTATION_DETAILS.md) - Technical deep dives

## Acknowledgements

- **[Martin Marek](https://github.com/martin-marek)** - Original Burst Photo application
- **Google Research** - HDR+ algorithm and paper
- **Adobe** - DNG SDK (this product includes DNG technology under license by Adobe)

## License

This project maintains compatibility with the original Burst Photo licensing. Please refer to the original repository for license details.
