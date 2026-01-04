# BurstPhoto Usage Guide

## Prerequisites
- **.NET 10 SDK** (or .NET 8/9 if compatible)
- **Vulkan Drivers**:
  - **Windows**: Standard GPU drivers.
  - **Linux**: `vulkan-tools`, `mesa-vulkan-drivers`.
  - **macOS**: MoltenVK.

## Building
Navigate to the project root:
```bash
cd BurstPhoto_NET
dotnet build
```

## Running the CLI

### 1. Process a Burst (Main Workflow)
This command processes a sequence of DNG images, aligns them, merges them, and outputs a denoised DNG.

**Syntax:**
```bash
dotnet run --project BurstPhoto.CLI -- process <INPUT_DNG_1> <INPUT_DNG_2> ... --output <OUTPUT_DNG>
```

**Example (using included samples):**
```bash
cd BurstPhoto_NET
dotnet run --project BurstPhoto.CLI -- process "Burst Samples\DJI_20251218173647_0202_D.DNG" "Burst Samples\DJI_20251218173647_0203_D.DNG"
```
*Note: The application will select a reference frame automatically.*

### 2. Debug LibRaw
Inspect raw metadata for a specific file to verify the loader is working.
```bash
dotnet run --project BurstPhoto.CLI -- debug-libraw "Burst Samples\DJI_20251218173647_0202_D.DNG"
```

## Running Tests

### Unit Tests
```bash
dotnet test BurstPhoto_NET/BurstPhoto.Tests/BurstPhoto.Tests.csproj
```

### Reference Comparison Tests
These tests compare the output of key pipeline stages (like `LoadReference`) against known-good values.
```bash
dotnet test --filter "ReferenceComparisonTests"
```

## Test Data
The repository includes sample DNGs in `BurstPhoto_NET/Burst Samples/`.
- **Source**: DJI drone DNGs (~18MB each).
- **Resolution**: High-res Bayer raw.
