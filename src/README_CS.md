# HDR+ C#/.NET Implementation

Cross-platform implementation of the HDR+ computational photography pipeline using C# and .NET 8.

## Architecture

### Project Structure
```
src/
├── HdrPlus.Core/        # Core algorithms (pure C#, cross-platform)
│   └── Alignment/       # Image alignment logic
├── HdrPlus.Compute/     # GPU abstraction layer
│   └── DirectX12/       # DirectX 12 backend (Windows)
├── HdrPlus.IO/          # DNG/RAW image I/O
└── HdrPlus.CLI/         # Command-line interface
```

### GPU Abstraction

Platform-agnostic GPU compute interface:
```csharp
IComputeDevice device = ComputeDeviceFactory.CreateDefault();
// Windows → DirectX 12
// Linux → Vulkan (planned)
// macOS → Metal (planned)
```

### Supported Platforms

| OS | Compute API | Status |
|----|-------------|--------|
| Windows 10/11 | DirectX 12 | ✅ Working |
| Linux | Vulkan | 🚧 Planned |
| macOS | Metal | 🚧 Planned |

## Building from Source

### Prerequisites
- [.NET 8 SDK](https://dot.net) or later
- Windows SDK (for DirectX 12)
- GPU with compute shader support

### Build Commands
```bash
# Restore dependencies
dotnet restore

# Build all projects
dotnet build

# Run tests
dotnet test

# Run CLI
dotnet run --project HdrPlus.CLI
```

### Building Shaders (Windows)
```batch
cd ..\shaders
build_shaders.bat
```

This compiles HLSL shaders to `.cso` files using DXC.

## Usage

### CLI Commands

**Display GPU Info:**
```bash
dotnet run --project HdrPlus.CLI info
```

**Run Tests:**
```bash
dotnet run --project HdrPlus.CLI test
```

**Align Images:**
```bash
dotnet run --project HdrPlus.CLI align reference.dng comp1.dng comp2.dng -o merged.dng
```

### Programmatic Usage

```csharp
using HdrPlus.Compute;
using HdrPlus.Core.Alignment;
using HdrPlus.IO;

// Initialize GPU
var device = ComputeDeviceFactory.CreateDefault();

// Load images
var reader = new SimpleRawReader();
var refImage = reader.ReadDng("reference.dng");
var compImage = reader.ReadDng("comparison.dng");

// Create aligner
var aligner = new ImageAligner(device);

// Align images
var aligned = aligner.AlignTexture(
    refPyramid, compTexture,
    downscaleFactors, tileSizes, searchDistances,
    uniformExposure: true,
    blackLevelMean: refImage.BlackLevels.Average(),
    colorFactors: refImage.ColorFactors
);

// Save result
var writer = new DngWriter();
writer.WriteDng(resultImage, "output.dng");

device.Dispose();
```

## Implementation Status

### ✅ Complete
- [x] GPU abstraction layer (IComputeDevice, IComputeBuffer, IComputeTexture, IComputePipeline)
- [x] DirectX 12 compute backend
- [x] Alignment shaders (6/40 kernels)
  - [x] avg_pool
  - [x] avg_pool_normalization
  - [x] compute_tile_differences
  - [x] find_best_tile_alignment
  - [x] warp_texture_bayer
  - [x] correct_upsampling_error
- [x] DNG I/O (basic TiffLibrary-based)
- [x] Core alignment algorithm structure
- [x] CLI with Spectre.Console

### 🚧 In Progress
- [ ] Descriptor heap management (DX12)
- [ ] Texture upload/download (staging buffers)
- [ ] Root signature reflection
- [ ] Complete alignment pipeline

### 📋 Planned
- [ ] Remaining alignment shaders (34 kernels)
- [ ] Spatial domain merge shaders
- [ ] Frequency domain merge shaders (FFT-based)
- [ ] Exposure correction shaders
- [ ] Texture utilities (blur, upsample, etc.)
- [ ] Vulkan backend (Linux/macOS)
- [ ] Metal backend (macOS native)
- [ ] LibRaw integration (better RAW support)
- [ ] Avalonia UI (cross-platform GUI)
- [ ] Unit tests
- [ ] Performance benchmarks

## Technology Stack

| Component | Technology |
|-----------|------------|
| Language | C# 12 |
| Framework | .NET 8 |
| Graphics | Silk.NET (DirectX 12, Vulkan planned) |
| Shaders | HLSL → SPIR-V/DXIL |
| Image I/O | TiffLibrary, ImageSharp |
| CLI | Spectre.Console, System.CommandLine |

## Shader Conversion

Metal shaders from the Swift version are converted to HLSL:

| Metal (Original) | HLSL (C# Version) |
|------------------|-------------------|
| `kernel void` | `[numthreads(x,y,z)] void` |
| `texture2d<T, access::read>` | `RWTexture2D<T>` |
| `[[texture(n)]]` | `: register(u<n>)` |
| `[[buffer(n)]]` | `cbuffer : register(b<n>)` |
| `thread_position_in_grid` | `SV_DispatchThreadID` |

See `../shaders/Alignment.hlsl` for examples.

## Performance Considerations

- **Zero-copy operations:** Using `Span<T>` and `Memory<T>` for efficient data handling
- **Async I/O:** Non-blocking file operations
- **GPU compute:** All heavy processing on GPU
- **Future optimizations:**
  - Native AOT compilation
  - Descriptor heap pooling
  - Async GPU submission

## Contributing

This is an active migration. Priority areas:

1. **DX12 descriptor heaps** - Complete resource binding
2. **Shader porting** - Convert remaining 34 Metal kernels
3. **Vulkan backend** - Enable Linux support
4. **Testing** - Add unit and integration tests

## Differences from Swift Version

| Feature | Swift Version | C# Version |
|---------|---------------|------------|
| Platform | macOS only | Cross-platform |
| GPU API | Metal | DirectX 12 / Vulkan / Metal |
| UI | SwiftUI | CLI (Avalonia planned) |
| RAW I/O | Adobe DNG SDK | TiffLibrary + ImageSharp |
| Language | Swift 5 | C# 12 |

## Troubleshooting

**"DirectX 12 device creation failed"**
- Ensure Windows 10/11 with DirectX 12 compatible GPU
- Update graphics drivers

**"Shader not found"**
- Run `build_shaders.bat` in `shaders/` directory
- Ensure compiled `.cso` files are in output directory

**"DNG file not supported"**
- Current implementation uses ImageSharp (limited format support)
- Full LibRaw integration coming soon for 700+ RAW formats

## License

Same as original Swift implementation.

## References

- Original Swift code: `../burstphoto/`
- HDR+ paper: https://graphics.stanford.edu/papers/hdrp/
- Silk.NET: https://github.com/dotnet/Silk.NET

---

**Migration by:** Claude (Anthropic)
**Status:** 🟢 Vertical slice functional (40% complete)
