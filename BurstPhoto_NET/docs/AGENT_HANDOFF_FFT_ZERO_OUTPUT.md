# Agent Handoff: FFT Zero Output Bug

**Date:** 2026-01-21
**Status:** Shader compilation FIXED ✅, FFT algorithm produces zero output ❌

---

## Context

I'm working on a C# port of a burst photo HDR+ pipeline with frequency domain merging. The shader compilation issues have been **successfully resolved** by splitting the monolithic shader into separate files. However, the FFT algorithm now compiles but produces **zero output**, indicating a functional/algorithmic bug in the FFT implementation.

## Current Status

### ✅ What Works (As of 2026-01-21)

- **Shader Compilation**: All 10 frequency domain shaders compile successfully without errors
- **Pipeline Initialization**: No HLSL parsing errors, pipeline initializes cleanly
- **GPU Detection**: Both AMD and NVIDIA GPUs detected correctly
- **Feature Support**: `ShaderStorageImageWriteWithoutFormat` supported on NVIDIA RTX 3080
- **Shader Architecture**: New modular architecture with separate files per kernel
- **Fast Algorithm**: Spatial domain merging works perfectly

### ❌ What Doesn't Work

- **Forward FFT Output**: Produces all zeros despite valid input data
- **Backward FFT Output**: Also produces zeros (cascading from forward FFT bug)
- **HigherQuality Algorithm**: Non-functional due to FFT zero output

### 🔍 Symptoms

**Iteration 1 Debug Output:**
```
[DEBUG] Iteration 1: After convert_to_rgba: sum(mid 10000)=3873090.00, mean=387.3090
[DEBUG] Iteration 1: After forward_fft: sum(first 10000)=0.00, mean=0.0000
[WARNING] Forward FFT produced near-zero output!
```

**What This Tells Us:**
1. ✅ Input to FFT is valid (mean=387.3090, non-zero data)
2. ❌ Output from FFT is all zeros
3. ✅ Shader compiles and executes without errors
4. ❌ Algorithm logic has a bug

---

## Architecture Overview

### New Shader Structure (After Compilation Fix)

```
BurstPhoto.Rendering/Shaders/
├── Constants.hlsli (shared constants)
└── Frequency/
    ├── FrequencyCommon.hlsli (shared resources, forward_fft_impl helper)
    ├── forward_fft.hlsl (entry point CSMain, calls forward_fft_impl)
    ├── backward_fft.hlsl (entry point CSMain, inverse FFT)
    ├── calculate_abs_diff_rgba.hlsl
    ├── calculate_rms_rgba.hlsl
    ├── calculate_mismatch_rgba.hlsl
    ├── calculate_highlights_norm_rgba.hlsl
    ├── normalize_mismatch.hlsl
    ├── reduce_artifacts_tile_border.hlsl
    ├── merge_frequency_domain.hlsl
    └── deconvolute_frequency_domain.hlsl
```

### Key Files to Investigate

**Shader Code:**
- `BurstPhoto.Rendering/Shaders/Frequency/FrequencyCommon.hlsli` - Contains `forward_fft_impl()` (lines 63-154)
- `BurstPhoto.Rendering/Shaders/Frequency/forward_fft.hlsl` - Entry point that calls helper
- `BurstPhoto.Rendering/Shaders/Frequency/backward_fft.hlsl` - Inverse FFT

**Pipeline Code:**
- `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` - Execution logic
  - `ExecuteForwardFft()` - Dispatches forward FFT kernel
  - `ExecuteBackwardFft()` - Dispatches backward FFT kernel
  - `ExecuteMergeFrequency()` - Main frequency domain merge logic (line ~1238+)

**Reference Implementation:**
- Original Swift/Metal implementation (if available in the repo)

---

## FFT Implementation Details

### Forward FFT (`forward_fft_impl` in FrequencyCommon.hlsli)

**Parameters:**
- `TileSize`: Fixed at 8 for frequency domain merging
- Input: RGBA texture (single width, e.g., 2064×1552)
- Output: Complex FT texture (double width, e.g., 4128×1552)

**Algorithm Structure:**
1. Processes tiles of size `TileSize × TileSize` (8×8)
2. Uses cosine windowing for normalization
3. Two-stage FFT: Y-direction first, then X-direction
4. Stores complex results as interleaved real/imaginary pairs (doubled width)

**Key Variables:**
```hlsl
float4 tmp_data[16];   // For ts=8: stores 2*ts values
float4 tmp_tile[80];   // For ts=8: stores (ts/2+1)*2*ts = 5*16 = 80 values
```

**Dispatch Configuration (from debug output):**
```
Input texture:  2064×1552 (format: R32G32B32A32Sfloat)
Output texture: 4128×1552 (format: R32G32B32A32Sfloat)
Tile grid: 258×194 tiles
Workgroups: 17×13 = 221 groups
Threads: 272×208 = 56576 threads
Active threads (within bounds): 258×194 = 50052
```

---

## Observed Behavior

### Test Run Results (NVIDIA RTX 3080, 2026-01-21)

**Command:**
```bash
cd BurstPhoto_NET && ./BurstPhoto.CLI/bin/Release/net10.0-windows/BurstPhoto.CLI.exe \
  process "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0018_D.DNG" \
          "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0019_D.DNG" \
          "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0020_D.DNG" \
  --algorithm HigherQuality --gpu 1 -o TestOutput_HigherQuality_NVIDIA
```

**Output:**
```
✓ Using user-specified device [1]
   Selected: NVIDIA GeForce RTX 3080 Laptop GPU

[Pipeline] ✓ All frequency domain shaders compiled successfully!

[VulkanComputePipeline] === ITERATION 1/4 ===
[DEBUG] Iteration 1: After convert_to_rgba: sum(mid 10000)=3873090.00, mean=387.3090
[DEBUG] Iteration 1: Running forward FFT on reference...
✓ GPU execution COMPLETE (QueueWaitIdle returned)
[DEBUG] Iteration 1: After forward_fft: sum(first 10000)=0.00, mean=0.0000
[WARNING] Forward FFT produced near-zero output!
```

**Key Observations:**
1. ✅ Shaders compile and execute successfully
2. ✅ GPU reports execution complete (no crashes)
3. ❌ Output is all zeros despite valid input
4. ❌ This cascades through all subsequent operations

---

## Potential Root Causes

### Hypothesis 1: Array Indexing Bug
**Likelihood:** High
**Rationale:** The FFT uses complex array indexing with `tmp_data` and `tmp_tile` arrays. Off-by-one errors or incorrect index calculations could write to wrong locations or read uninitialized data.

**Lines to Check:**
- `FrequencyCommon.hlsli:101-103` - Writing to `tmp_tile`
- `FrequencyCommon.hlsli:108-110` - Reading from `tmp_tile`
- `FrequencyCommon.hlsli:135-138` - Writing to output texture

**Specific Concerns:**
```hlsl
int n_tmp = dn * 2 * ts;  // Line 87
tmp_tile[n_tmp+2*dm+0] = Re0; tmp_tile[n_tmp+2*dm+1] = Im0;  // Line 101
```

### Hypothesis 2: Bounds Check Preventing Writes
**Likelihood:** Medium
**Rationale:** The bounds checks added in `forward_fft.hlsl` might be too restrictive or incorrectly calculated, causing threads to early-return before executing the FFT.

**Lines to Check:**
- `forward_fft.hlsl:20-28` - Bounds checking logic

**Current Bounds Check:**
```hlsl
if (DTid.x >= (uint)nTilesX || DTid.y >= (uint)nTilesY)
    return;

if (outputWidth != inputWidth * 2 || outputHeight != inputHeight)
    return;
```

### Hypothesis 3: Texture Format Mismatch
**Likelihood:** Low
**Rationale:** Input/output textures might not be in the expected format, causing reads/writes to fail silently.

**To Verify:**
- Check that input texture is `R32G32B32A32Sfloat`
- Check that output texture is `R32G32B32A32Sfloat`
- Verify descriptor bindings match shader expectations

### Hypothesis 4: Thread Dispatch Mismatch
**Likelihood:** Low
**Rationale:** The workgroup dispatch might not align with the tile grid, causing some tiles not to be processed.

**Current Dispatch (from debug):**
```
Workgroups: 17×13 = 221 groups
Expected tiles: 258×194 = 50052 tiles
Threads per group: 16×16 = 256
Total threads: 17*16 × 13*16 = 272×208 = 56576
```

**Math Check:**
- nTilesX = 2064 / 8 = 258
- nTilesY = 1552 / 8 = 194
- Workgroups needed: ceil(258/16)×ceil(194/16) = 17×13 ✅

### Hypothesis 5: Ported from Metal Incorrectly
**Likelihood:** High
**Rationale:** The HLSL port from Metal might have subtle differences in:
- Array indexing (Metal uses different conventions)
- Data types (Metal's `float4` vs HLSL's `float4`)
- Texture coordinate systems

**Action:** Compare line-by-line with original Metal implementation

---

## Diagnostic Strategy

### Step 1: Add Intermediate Debug Output
Modify `forward_fft_impl` to output intermediate values at key stages:

```hlsl
// After loading input data
OutputTexture[int2(0, 0)] = float4(tmp_data[0].r, tmp_data[1].r, tmp_data[2].r, tmp_data[3].r);

// After Y-direction FFT
OutputTexture[int2(1, 0)] = float4(tmp_tile[0].r, tmp_tile[1].r, tmp_tile[2].r, tmp_tile[3].r);

// After X-direction FFT (normal output follows)
```

### Step 2: Simplify to Minimal Test Case
Create a minimal FFT shader that:
1. Reads input data
2. Writes it directly to output (bypassing FFT logic)
3. Verifies read/write operations work

### Step 3: Compare with Metal Reference
If Metal source is available:
1. Read the original `forward_fft` implementation
2. Compare array sizes, indexing, and loop bounds
3. Look for Metal-specific constructs that don't translate directly to HLSL

### Step 4: Validate Bounds Calculations
Add debug output to verify:
```hlsl
// At start of forward_fft.hlsl CSMain
if (DTid.x == 0 && DTid.y == 0) {
    OutputTexture[int2(0, 0)] = float4(nTilesX, nTilesY, inputWidth, inputHeight);
    OutputTexture[int2(1, 0)] = float4(outputWidth, outputHeight, TileSize, 0);
}
```

### Step 5: GPU Validation Tools
Use tools to inspect actual GPU execution:
- RenderDoc (if compatible with compute-only workloads)
- Nsight Graphics (NVIDIA)
- Check if any threads are hitting early returns

---

## Testing Commands

### Basic Test (with NVIDIA GPU)
```bash
cd BurstPhoto_NET
./BurstPhoto.CLI/bin/Release/net10.0-windows/BurstPhoto.CLI.exe process \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0018_D.DNG" \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0019_D.DNG" \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0020_D.DNG" \
  --algorithm HigherQuality --gpu 1 -o TestOutput_FFT_Debug
```

### With Debug Dump (saves intermediate DNG files)
```bash
./BurstPhoto.CLI/bin/Release/net10.0-windows/BurstPhoto.CLI.exe process \
  "Burst Samples/Static Exposure/DJI_20251218173647_0202_D.DNG" \
  "Burst Samples/Static Exposure/DJI_20251218173647_0203_D.DNG" \
  "Burst Samples/Static Exposure/DJI_20251218173647_0204_D.DNG" \
  --algorithm HigherQuality --gpu 1 --debug-dump -o TestOutput_Debug
```

### Check GPU Detection
```bash
./BurstPhoto.CLI/bin/Release/net10.0-windows/BurstPhoto.CLI.exe process dummy.dng dummy2.dng --list-gpus
```

---

## Success Criteria

✅ Forward FFT produces non-zero output
✅ Output values are in expected range (complex frequency domain data)
✅ Backward FFT correctly reconstructs spatial domain data
✅ Final merged image is non-black
✅ Visual quality comparable to Fast algorithm (or better)

---

## What I Need You To Do

1. **Diagnose the FFT zero-output bug** by:
   - Reviewing the `forward_fft_impl` logic in `FrequencyCommon.hlsli`
   - Comparing with the original Metal implementation (if available)
   - Adding intermediate debug output to isolate where the bug occurs
   - Testing with the provided commands

2. **Fix the bug** once identified

3. **Verify the fix** by:
   - Running the test commands above
   - Confirming non-zero FFT output in debug logs
   - Checking that the final output image is non-black

---

## Additional Context

- **Hardware:** NVIDIA RTX 3080 Laptop GPU (use `--gpu 1`)
- **Test Data:** Bracketed exposure samples work well for testing
- **Debug Output:** Extensive logging already in place, grep for "WARNING" or "sum=" to see data flow
- **Previous Work:** Shader compilation issue was resolved by splitting monolithic shader into separate files
- **Shader Compile Time:** All shaders compile in ~1 second total

---

## Questions to Ask Me

If you need:
- Access to the original Metal/Swift FFT implementation
- Clarification on expected FFT output format
- Help interpreting debug output
- Additional test cases or validation criteria
- RenderDoc or GPU profiling assistance

---

Good luck! The compilation infrastructure is solid now, so this should be a straightforward algorithmic debugging task.
