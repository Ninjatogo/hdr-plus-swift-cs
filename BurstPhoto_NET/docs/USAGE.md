# BurstPhoto Usage Guide

## Prerequisites
- **.NET 8 SDK**
- **Vulkan Drivers**: Ensure you have Vulkan drivers installed for your GPU.
  - **Linux**: `vulkan-tools`, `mesa-vulkan-drivers` (or proprietary drivers).
  - **Windows**: Standard GPU drivers usually include Vulkan.
  - **macOS**: MoltenVK is required (Silk.NET often handles this, or install via Vulkan SDK).

## Building
Navigate to the solution directory:
```bash
cd BurstPhoto_NET
dotnet build
```

## Running the CLI
The application is a command-line tool. You can run it using `dotnet run`.

### Process an Image (Vertical Slice Verification)
Currently, this command reads a RAW image (using LibRaw), initializes the Vulkan backend, and writes a dummy output (copy of input data) to verify the architecture.

```bash
dotnet run --project BurstPhoto.CLI/BurstPhoto.CLI.csproj -- process <INPUT_RAW> <OUTPUT_PGM>
```

**Example:**
```bash
dotnet run --project BurstPhoto.CLI/BurstPhoto.CLI.csproj -- process sample.dng output.pgm
```

### Debug LibRaw
This command inspects properties of the LibRaw context, useful for verifying that `Sdcb.LibRaw` works correctly on your system.

```bash
dotnet run --project BurstPhoto.CLI/BurstPhoto.CLI.csproj -- debug-libraw
```

## Running Tests
To run the unit tests, ensure `DJI_0011.DNG` is present in the repository root (fetched from `main`).

```bash
dotnet test BurstPhoto_NET/BurstPhoto.Tests/BurstPhoto.Tests.csproj
```
