# Implementation Details

## 1. DNG Writing Strategy (Adobe DNG SDK)

### Motivation
We switched from `BitMiracle.LibTiff.NET` to a native **Adobe DNG SDK** wrapper because the managed library could not consistently handle complex DNG tags (specifically `WhiteLevel` and `BlackLevel` arrays) without internal serialization errors. It also required manual reconstruction of proprietary `MakerNotes` and color matrices, which was error-prone.

### The "Clone and Patch" Approach
We replicated the strategy used in the reference Swift project:
1.  **Open Source DNG**: Read the original input file.
2.  **Load into `dng_negative`**: This object preserves all original tags, `MakerNotes`, and metadata.
3.  **Patch Pixel Data**: `memcpy` our processed image data into the negative's buffer.
4.  **Update Tags**: Modify only specific tags that changed (e.g., `WhiteLevel`, `BlackLevel`).
5.  **Write**: Save to a new DNG file.

### Native Wrapper (`BurstPhoto.Native`)
- **Project Type**: C++ Dynamic Link Library (DLL).
- **Core Dependency**: Adobe DNG SDK 1.7.1.
- **Key Files**:
    - `dng_sdk_wrapper.cpp`: The bridge exposing C-compatible functions to C#.
    - `dng_jxl_stubs.cpp`: Stubs to disable JPEG-XL and XMP dependencies to keep the build lightweight (~800KB).

### C# Integration
- **`DngSdkWriter.cs`**: Uses P/Invoke to call the native DLL.
- **Workflow**:
  ```csharp
  // C#
  DngSdkWriter.WriteDng(inputPath, outputPath, pixelData, width, height, ...);
  ```

---

## 2. Raw Bayer Access (HurlbertVisionLab.LibRawWrapper)

### Overview
To perform high-quality denoising, we need access to the raw (undemosaiced) Bayer CFA data. We replaced `Sdcb.LibRaw` (which defaulted to demosaiced RGB) with `HurlbertVisionLab.LibRawWrapper`.

### Key Features
- **Direct Buffer Access**: Access unprocessed pixel data via `LibRawProcessor.RawData.Buffer` (IntPtr).
- **Metadata**:
    - `Color.Maximum` (White Level)
    - `Color.CameraWhiteBalance`
    - `Other.IsoSpeed` / `Other.Shutter`

### Code Example
```csharp
using var processor = new LibRawProcessor();
processor.Open(path);
processor.Unpack(); // Decodes raw data to memory

// Access raw pointer
IntPtr rawPtr = processor.RawData.Buffer;
int width = processor.Sizes.RawWidth;
int height = processor.Sizes.RawHeight;
```

---

## 3. Shader Implementations

We have ported the reference Metal shaders to HLSL.

### 3.1 Alignment (`Align.hlsl`)
- **2.5D Optimization**: Implemented a coarse-to-fine search strategy. If `search_dist` is 2, creating a 5x5 search window, we use optimized kernels (`compute_tile_differences25`). For larger searches, we fall back to the generic `compute_tile_differences`.
- **Warping**: `warp_texture_bayer` handles the alignment warping.

### 3.2 Texture Operations (`TextureOps.hlsl`)
- **Hot Pixel Correction**: `find_hotpixels_bayer` detects defective pixels and writes a weight mask. `prepare_texture_bayer` uses this mask to interpolate replacements.
- **X-Trans Support**: Specific kernels (`find_hotpixels_xtrans`, `prepare_texture_xtrans`) handle the 6x6 Fuji X-Trans pattern usage.

### 3.3 Merging & Exposure (`MergeSpatial.hlsl`)
- **Weighted Accumulation**: `ExecuteMerge()` now uses GPU compute shaders (`add_texture_weighted`, `add_weight_only`) instead of CPU readbacks, significantly improving performance.
- **Robustness Calculation**: Uses Swift's exact formula:
  ```
  robustness_rev = 0.5 * (36.0 - round(noise_reduction))
  robustness = 0.12 * pow(1.3, robustness_rev) - 0.4529822
  ```
- **Noise Estimation**: `EstimateColorNoise()` computes noise standard deviation from the reference texture by sampling same-color neighbors.
- **Merge Weight Formula** (from Swift `spatial.metal`):
  ```hlsl
  float max_diff = NoiseSd / Robustness;
  weight = clamp(1.0 - diff / max_diff, 0.0, 1.0);
  ```

### 3.4 Exposure-Bracketed Merge (HDR)
- **Goal**: Merge frames with different exposure values (e.g., -1, 0, +1 EV).
- **Strategy**: 
    - **Overexposed Frames (Alt Brighter)**: Scaled down (`val * ScaleFactor`) to match reference. Used to reduce noise in shadows. Kernel: `add_texture_exposure`.
    - **Underexposed Frames (Alt Darker)**: Used to recover clipped highlights in the reference. Only pixels that are highlights in the reference are blended. Kernel: `add_texture_highlights`.
- **Implementation**:
    - `ExecuteMerge` calculates `exposureDiff = Ref.ExposureBias - Alt.ExposureBias`.
    - Branches dispatch specific kernels based on `exposureDiff`.

### 3.5 Tone Mapping (`Exposure.hlsl`)
- **Goal**: Apply final brightness/contrast adjustment to the merged linear data.
- **Kernels**:
    - `correct_exposure_linear`: Applies a linear gain (`ScaleFactor`) and clamps to black/white levels.
    - `correct_exposure`: Applies a tone curve (currently equivalent to linear in the port, but extensible).
    - `max_x` / `max_y`: Reduction kernels to find the maximum pixel value for auto-exposure calculations.
- **Integration**: Called at the end of `ProcessAsync`, uploading the merged buffer back to the GPU for this final pass.

---

## 4. Noise Estimation

### CPU Implementation (Active)
The CPU-based `EstimateColorNoise()` method samples adjacent same-color pixels from the reference texture to compute noise standard deviation:
```csharp
float EstimateColorNoise(float[] data, int width, int height, int mosaicPatternWidth)
{
    // Sum |pixel[x,y] - pixel[x+mosaic, y+mosaic]| for same-color neighbors
    // Return meanDiff * mosaicPatternWidth^2
}
```
- **Performance**: <100ms for 12MP images
- **Output**: ~876 noiseSd for test DJI images

### GPU Implementation (WIP - Has Bug)
`ExecuteNoiseEstimationGPU()` attempts to replicate Swift's full pipeline:
1. **Blur X**: Separable Gaussian blur via `blur_mosaic_texture` (Direction=0)
2. **Blur Y**: Separable Gaussian blur via `blur_mosaic_texture` (Direction=1)
3. **Diff**: `color_difference_superpixel` computes |original - blurred|
4. **Reduce**: Sum via `sum_rect_columns_float` + `sum_row_to_buffer`

**Known Issue**: The blur shader outputs all zeros despite correct parameters. 
**Current State**: The GPU pipeline is fully wired in `VulkanComputePipeline.cs` (`ExecuteNoiseEstimationGPU`), but we currently rely on the CPU fallback until the shader bug is fixed.

---

## 5. Debug Dump Feature

### Overview
A debugging feature was added (2026-01-19) to save intermediate DNG files at key pipeline stages. This helps diagnose black output and other processing issues.

### Implementation
- **`VulkanComputePipeline.EnableDebugDump`**: Boolean property to enable/disable dumping.
- **`DebugDump()`**: Helper method that downloads GPU texture data and writes to DNG via `DngSdkWriter`.
- **CLI Flag**: `--debug-dump` enables the feature from the command line.

### Pipeline Stages Captured
1. `step_1_prepare` - After Prepare Pass (hot pixel correction, padding)
2. `step_2_fft_ref` - After Forward FFT (HigherQuality mode)
3. `step_3_merge_accum_*` - After merge loop completes
4. `step_4_deconv_ft` - After Deconvolution (HigherQuality mode)
5. `step_5_back_fft` - After Backward FFT (HigherQuality mode)
6. `step_6_exposure` - After Exposure Correction

### Known Limitations
- FFT textures (step_2, step_3, step_4) have 2x width for complex number storage. The debug dump extracts only the first channel, which may not represent the data correctly.
- Files are overwritten on subsequent runs.

---

## 6. Known Limitations

### Frequency Domain Merge (Confirmed Black Output)
The "Higher Quality" merge algorithm (Frequency Domain) produces **black output**. This was confirmed via debug dump testing (2026-01-19).

**Evidence from Debug Testing:**
| Stage | File Size | Status |
|-------|-----------|--------|
| step_1_prepare.dng | 16 MB | ✅ Valid data |
| step_5_back_fft.dng | 1.6 MB | ❌ Mostly zeros/black |
| step_6_exposure.dng | 1.6 MB | ❌ Mostly zeros/black |

The dramatic size reduction (16MB → 1.6MB) confirms the FFT pipeline outputs mostly zeros.

**Confirmed Root Cause:**
**Incorrect RGBA Conversion Logic** - The `convert_to_rgba` and `convert_to_bayer` shaders were demosaicing (averaging green channels and using CFA patterns) instead of directly packing raw Bayer values.

**Shader Logic Fix (2026-01-19):**
- **Problem**: Original shaders averaged green channels (e.g., `g = (p1+p2)*0.5f` for RGGB)
- **Solution**: Changed to direct packing without demosaicing:
  ```hlsl
  // convert_to_rgba: Direct pack 2x2 Bayer → RGBA
  OutTextureRGBA[gid] = float4(p0, p1, p2, p3);

  // convert_to_bayer: Simple positional unpack RGBA → 2x2 Bayer
  if (x == 0 && y == 0) val = rgba.r;      // Top-left
  else if (x == 1 && y == 0) val = rgba.g; // Top-right
  else if (x == 0 && y == 1) val = rgba.b; // Bottom-left
  else if (x == 1 && y == 1) val = rgba.a; // Bottom-right
  ```
- **Files**: `BurstPhoto.Rendering/Shaders/TextureOps.hlsl` (lines 89-127)
- **Status**: Logic matches Swift exactly, but **runtime execution produces zeros**

**Active Debugging (2026-01-19):**
Despite correct shader logic, `ExecuteConvertToRgba()` produces all-zero output:
- Input (`preparedTexture`): Valid 16MB data ✅
- RGBA output: All zeros after shader execution ❌
- Shader compilation: No errors ✅
- Possible causes: Vulkan descriptor binding, memory barriers, or image layout issues
- Location: `VulkanComputePipeline.cs:1246-1293`

### GPU Noise Estimation
The GPU blur shader has an issue outputting zeros. CPU estimation is used as a reliable fallback.

### Spatial Mode Bug (Fixed 2026-01-19)
A bug was discovered where `outHeight` was never assigned in spatial mode, causing buffer allocation failures. This was fixed by adding `outHeight = height + tileSize` in the spatial padding calculation.


