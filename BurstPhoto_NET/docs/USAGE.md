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

---

## Debugging Tools

### Debug Dump (Intermediate Output)
When troubleshooting black output or other processing issues, you can enable debug dumps to save intermediate DNG files at various pipeline stages.

**Usage:**
```bash
dotnet run --project BurstPhoto.CLI -- process <INPUT_DNGs> -o <OUTPUT_DIR> --debug-dump
```

**Output Files (saved to `DebugOutput/` folder):**

| File | Stage | Description |
|------|-------|-------------|
| `step_1_prepare.dng` | After Prepare | Reference frame after hot pixel correction and padding |
| `step_2_fft_ref.dng` | After Forward FFT | (HigherQuality mode only) - May fail for complex textures |
| `step_3_merge_accum_*.dng` | After Merge | Accumulated result (spatial or frequency domain) |
| `step_4_deconv_ft.dng` | After Deconvolution | (HigherQuality mode only) |
| `step_5_back_fft.dng` | After Backward FFT | (HigherQuality mode only) |
| `step_6_exposure.dng` | After Exposure Correction | Final tone-mapped result |

**Notes:**
- Debug files are **overwritten** on each run. Move or rename `DebugOutput/` between runs to preserve files.
- FFT textures (step_2, step_3, step_4) may fail to dump due to 2x width for complex numbers - this is expected.

### Log File Output
Save all console output to a timestamped log file for easier review and debugging.

**Usage:**
```bash
# Auto-generate timestamped log in logs/ folder
dotnet run --project BurstPhoto.CLI -- process <INPUT_DNGs> -o <OUTPUT_DIR> --log

# Specify custom log file path
dotnet run --project BurstPhoto.CLI -- process <INPUT_DNGs> -o <OUTPUT_DIR> --log-file my_log.txt
```

**Default log path:** `logs/process_YYYYMMDD_HHMMSS.log`

**Tip:** When automating tests or debugging issues, always use `--log` to capture full output for later analysis.


