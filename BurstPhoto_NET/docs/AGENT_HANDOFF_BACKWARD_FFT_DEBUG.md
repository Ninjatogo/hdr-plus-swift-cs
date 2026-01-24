# Agent Handoff: Backward FFT Dot Matrix Pattern Investigation

**Date:** 2026-01-23
**Branch:** HigherQualityFrequencyDomainMerging
**Issue:** HQ frequency domain merge produces dot matrix pattern instead of full resolution image

---

## Executive Summary

The backward FFT shader is producing a **sparse dot matrix pattern** instead of a full image. Through systematic debugging, we've confirmed:

1. **All tiles ARE being dispatched and written correctly** (gradient test proves this)
2. **Input frequency domain data EXISTS and is valid** (input FT test shows ~1B sum)
3. **The DC component is correct** (DC bin test produces expected ~2668 mean)
4. **The actual FFT computation produces only ~16% of expected values** (~441 mean vs ~2668 expected)

The root cause is in the **FFT butterfly computation itself**, not in dispatch, texture bindings, or tile coordinates.

---

## Visual Symptom

- **Dot matrix pattern** throughout the entire image
- **Top-left quadrant** (~1/4 of image) has slightly higher density/thicker dots
- Pattern resembles the actual scene content (not random noise)
- Consistent across all 4 merge iterations

---

## Key Files

| File | Purpose |
|------|---------|
| `BurstPhoto.Rendering/Shaders/Frequency/backward_fft.hlsl` | Backward FFT shader (THE PROBLEM IS HERE) |
| `BurstPhoto.Rendering/Shaders/Frequency/forward_fft.hlsl` | Forward FFT shader (appears correct) |
| `BurstPhoto.Rendering/Shaders/Frequency/FrequencyCommon.hlsli` | Shared FFT utilities and forward_fft_impl |
| `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` | C# orchestration code |
| `burstphoto/merge/frequency.metal` | Swift/Metal reference implementation |

---

## Test Results Summary

| Debug Mode | What It Does | Sum (first 10K) | Mean | Interpretation |
|------------|--------------|-----------------|------|----------------|
| Mode 1 (Gradient) | Writes position-based gradient to all pixels | ~34M | ~3400 | **All tiles write correctly** |
| Mode 2 (Input FT) | Copies input frequency data directly | ~1B | ~100K | **Frequency data exists** |
| Mode 3 (DC Sum) | Sums all input FT values, divides by N | ~317K | ~32 | DC computed manually |
| Mode 4 (DC Bin) | Reads DC component from FT, divides by N | ~26.7M | ~2668 | **Forward FFT DC is correct** |
| Mode 0 (Actual FFT) | Full backward FFT computation | ~4.4M | ~441 | **Only 16.5% of expected!** |

**Key Insight:** Mode 4 (DC bin) produces ~2668 mean, which is the expected average pixel value. Mode 0 (actual FFT) produces only ~441 mean, which is ~16.5% (roughly 1/6) of expected. This 6x reduction suggests the FFT butterfly operations are losing ~5/6 of the signal.

---

## Debug Modes Added to backward_fft.hlsl

```hlsl
// DEBUG MODE:
// 0 = Normal FFT operation
// 1 = Output gradient test pattern (bypasses FFT)
// 2 = Output input FT data directly (tests if input data exists)
// 3 = Output DC component only (sum of all input values / normalization)
// 4 = Copy DC bin (freq 0,0) directly to all output pixels
#define DEBUG_GRADIENT_MODE 0
```

To use: Change the `#define DEBUG_GRADIENT_MODE` value and rebuild.

---

## What We've Verified Works

### 1. Dispatch Mechanism
- Dispatching 256x192 threads (one per tile) for 2048x1536 RGBA output
- WorkGroupSize: 16x16
- All threads execute (proven by gradient test)

### 2. Texture Bindings
- `RefTexture` (binding 1): Input frequency domain data (4096x1536, double-width for complex)
- `OutputTexture` (binding 10): Output spatial domain (2048x1536)
- Bindings verified correct in VulkanComputePipeline.cs

### 3. Tile Coordinates
- `m0 = gid.x * TileSize` (correct)
- `n0 = gid.y * TileSize` (correct)
- Output writes to `(m0+dm, n0+dn+offset)` match Swift exactly

### 4. Forward FFT
- DC component is correct (~2668 mean after normalization)
- Frequency domain data has expected magnitude (~1B total sum)

### 5. Normalization Factor
- `norm_factor = NumTextures * TileSize * TileSize = 3 * 8 * 8 = 192`
- Matches Swift implementation
- NumTextures correctly passed in FrequencyParams buffer

---

## Comparison: Swift vs HLSL Backward FFT

### Array Sizes
| Array | Swift (ts=8) | HLSL | Issue? |
|-------|--------------|------|--------|
| tmp_data | 16 | 64 | Oversized but harmless |
| tmp_tile | 128 | 512 | Oversized but harmless |

### Angle Sign
Both use `angle = -2*PI/tile_size` (negative) - this is correct for the conjugate form of inverse DFT.

### Complex Multiplication (Inverse DFT Form)
**Swift:**
```metal
Re0 += (coefRe*dataRe + coefIm*dataIm);
Im0 += (coefIm*dataRe - coefRe*dataIm);
```

**HLSL:**
```hlsl
Re0 += (coefRe*dataRe + coefIm*dataIm);
Im0 += (coefIm*dataRe - coefRe*dataIm);
```
These match (note: different from forward FFT which uses opposite signs).

### tmp_tile Write (After First Pass)
**Swift:**
```metal
tmp_tile[n_tmp+2*dm+0]                =  Re0;
tmp_tile[n_tmp+2*dm+1]                = -Im0;  // Negative imaginary
tmp_tile[n_tmp+2*dm+tile_size_24+0]   =  Re1;
tmp_tile[n_tmp+2*dm+tile_size_24+1]   = -Im1;
tmp_tile[n_tmp+2*dm+tile_size+0]      =  Re2;
tmp_tile[n_tmp+2*dm+tile_size+1]      = -Im2;
tmp_tile[n_tmp+2*dm+tile_size_24*3+0] =  Re3;
tmp_tile[n_tmp+2*dm+tile_size_24*3+1] = -Im3;
```

**HLSL:**
```hlsl
tmp_tile[n_tmp+2*dm+0] = Re0; tmp_tile[n_tmp+2*dm+1] = -Im0;
tmp_tile[n_tmp+2*dm+sz24+0] = Re1; tmp_tile[n_tmp+2*dm+sz24+1] = -Im1;
tmp_tile[n_tmp+2*dm+ts+0] = Re2; tmp_tile[n_tmp+2*dm+ts+1] = -Im2;
tmp_tile[n_tmp+2*dm+sz24*3+0] = Re3; tmp_tile[n_tmp+2*dm+sz24*3+1] = -Im3;
```
These match (sz24=4, ts=8, sz24*3=12 for tile_size=8).

### Output Coordinates
**Swift:**
```metal
out_texture.write(Re0/norm_factor, uint2(m, n));              // m = m0+dm, n = n0+dn
out_texture.write(Re1/norm_factor, uint2(m, n+tile_size_14)); // n + 2
out_texture.write(Re2/norm_factor, uint2(m, n+tile_size_24)); // n + 4
out_texture.write(Re3/norm_factor, uint2(m, n+tile_size_34)); // n + 6
```

**HLSL:**
```hlsl
OutputTexture[int2(m0+dm, n0+dn)] = Re0 / norm_factor;
OutputTexture[int2(m0+dm, n0+dn+sz14)] = Re1 / norm_factor;  // +2
OutputTexture[int2(m0+dm, n0+dn+sz24)] = Re2 / norm_factor;  // +4
OutputTexture[int2(m0+dm, n0+dn+sz34)] = Re3 / norm_factor;  // +6
```
These match.

---

## Loop Structure Comparison

### First Pass (Row-wise FFT)

**Swift:**
```metal
for (int dn = 0; dn < tile_size; dn++) {           // Outer: each row
    int const n_tmp = dn*2*tile_size;
    for (int dm = 0; dm < tile_size; dm++) {       // Copy row data
        tmp_data[2*dm+0] = in_texture_ft.read(uint2(2*(m0+dm)+0, n0+dn));
        tmp_data[2*dm+1] = in_texture_ft.read(uint2(2*(m0+dm)+1, n0+dn));
    }
    for (int dm = 0; dm < tile_size/4; dm++) {     // DFT on row
        ...butterfly operations...
    }
}
```

**HLSL:**
```hlsl
for (int dn = 0; dn < ts; dn++) {                  // Outer: each row
    int n_tmp = dn * 2 * ts;
    for (int dm = 0; dm < ts; dm++) {              // Copy row data
        tmp_data[2*dm+0] = RefTexture.Load(int3(2*(m0+dm)+0, n0+dn, 0));
        tmp_data[2*dm+1] = RefTexture.Load(int3(2*(m0+dm)+1, n0+dn, 0));
    }
    for (int dm = 0; dm < ts/4; dm++) {            // DFT on row
        ...butterfly operations...
    }
}
```
Structure matches.

### Second Pass (Column-wise FFT)

**Swift:**
```metal
for (int dm = 0; dm < tile_size; dm++) {           // Outer: each column
    int const m = m0 + dm;
    for (int dn = 0; dn < tile_size; dn++) {       // Copy column data
        tmp_data[2*dn+0] = tmp_tile[dn*2*tile_size+2*dm+0];
        tmp_data[2*dn+1] = tmp_tile[dn*2*tile_size+2*dm+1];
    }
    for (int dn = 0; dn < tile_size/4; dn++) {     // DFT on column
        ...butterfly operations...
        // Output writes here
    }
}
```

**HLSL:**
```hlsl
for (int dm = 0; dm < ts; dm++) {                  // Outer: each column
    for (int dn = 0; dn < ts; dn++) {              // Copy column data
        tmp_data[2*dn+0] = tmp_tile[dn*2*ts + 2*dm+0];
        tmp_data[2*dn+1] = tmp_tile[dn*2*ts + 2*dm+1];
    }
    for (int dn = 0; dn < ts/4; dn++) {            // DFT on column
        ...butterfly operations...
        // Output writes here
    }
}
```
Structure matches.

---

## Hypotheses for Root Cause

### 1. Butterfly Coefficient Calculation Error
The 4-point DFT butterfly uses twiddle factors that may have subtle sign or index errors. The pattern of ~1/6 signal survival suggests only certain frequency components are computed correctly.

### 2. tmp_tile Indexing Mismatch
The first pass writes to tmp_tile with specific indexing, and the second pass reads from it. If there's an indexing mismatch, the second pass would read wrong/zero values.

### 3. Variable Reuse Bug
In the butterfly operations, variables like Re0/Im0 are reused across stages. A subtle order-of-operations bug could cause intermediate values to be overwritten before use.

### 4. HLSL vs Metal Floating-Point Precision
HLSL and Metal may have different default precision for sin/cos calculations, potentially causing phase errors that result in destructive interference.

### 5. The 1/4 Image Density Difference
The top-left quarter having higher dot density suggests either:
- The first workgroup (0,0) through (7,7) behaves differently
- There's an off-by-one in the tile grid calculation affecting 3/4 of tiles
- The bounds check `if (DTid.x >= nTilesX)` has an edge case

---

## Recommended Next Steps

### 1. Simplified FFT Test
Replace the full 8x8 FFT with a trivial 2x2 or 4x4 case to isolate the butterfly logic:
```hlsl
// Test: Just output the DC component via actual FFT math
// For ts=2: DC = (x[0]+x[1]) / 2, should match input average
```

### 2. Add Per-Stage Debug Output
Modify the shader to output intermediate values at each butterfly stage:
```hlsl
// After first pass: write tmp_tile[0..15] to debug texture
// After second pass before butterflies: write tmp_data[0..15]
// After first butterfly: write Re00, Re11, etc.
```

### 3. Compare Single Tile Output
Run both Swift and HLSL on the SAME input tile data and compare outputs byte-for-byte.

### 4. Check for Workgroup Edge Effects
Add logging for tiles at workgroup boundaries (tile 15, 16, 31, 32, etc.) to see if there's a pattern.

### 5. Use Reference DFT
Replace the optimized FFT with the simple O(N^2) DFT from Swift's `backward_dft` kernel to verify the optimized version is the problem:
```metal
// Swift backward_dft (slow but simple)
for (int dm = 0; dm < tile_size; dm++) {
    for (int dn = 0; dn < tile_size; dn++) {
        Re = Im = zeros;
        for (int dy = 0; dy < tile_size; dy++) {
            coefRe = cos(angle*dn*dy);
            coefIm = sin(angle*dn*dy);
            ...
        }
    }
}
```

---

## Test Commands

```powershell
# Build
cd C:\Users\maxwe\RiderProjects\hdr-plus-swift-cs\BurstPhoto_NET
dotnet build BurstPhoto.CLI -c Release

# Run test
.\BurstPhoto.CLI\bin\Release\net10.0-windows\BurstPhoto.CLI.exe process `
  '.\Burst Samples\Bracketed Exposure\Input\DJI_20250925172104_0018_D.DNG' `
  '.\Burst Samples\Bracketed Exposure\Input\DJI_20250925172104_0019_D.DNG' `
  '.\Burst Samples\Bracketed Exposure\Input\DJI_20250925172104_0020_D.DNG' `
  --algorithm HigherQuality --tile-size Medium --noise-reduction 13 `
  --exposure-control LinearFullRange --gpu 1 `
  -o 'C:\Users\maxwe\Desktop\TestOutput\TestName'
```

---

## Related Documentation

- `AGENT_HANDOFF_FINAL_ISSUE.md` - Previous handoff about dispatch fixes
- `AGENT_HANDOFF_DISPATCH_FIXES.md` - Earlier dispatch investigation
- `MIGRATION_STATUS.md` - Overall project status
- `BACKLOG.md` - Outstanding tasks

---

## Summary

The backward FFT produces only ~16% of expected output values, resulting in a dot matrix pattern. All infrastructure (dispatch, bindings, coordinates) is verified correct through gradient testing. The issue is isolated to the FFT butterfly computation itself. The most likely cause is a subtle bug in the twiddle factor calculations or variable reuse within the butterfly stages. Next steps should focus on simplifying the FFT to isolate the exact failing operation.
