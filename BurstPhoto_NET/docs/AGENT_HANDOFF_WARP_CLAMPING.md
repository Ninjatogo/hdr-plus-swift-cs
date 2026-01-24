# Agent Handoff Document: Warp Shader Clamping Fix

**Date**: 2026-01-22
**Status**: Fix implemented, awaiting user testing
**Branch**: HigherQualityFrequencyDomainMerging

## Executive Summary

This document details the debugging and fix of a critical issue where the `convert_to_rgba` shader was producing all zeros for warped/aligned comparison images in the BurstPhoto_NET HDR+ image processing pipeline. The root cause was identified as alignment vectors at edge tiles being too large, causing the warp shader to read from zero-padding regions instead of actual image data.

## Project Context

BurstPhoto_NET is a C#/.NET port of a Swift HDR+ burst photography processing pipeline. It uses Vulkan compute shaders (HLSL) for GPU-accelerated image alignment and merging.

### Key Pipeline Flow (Frequency Domain / HigherQuality mode)
1. **Prepare**: Raw Bayer image → padded float texture with zero-padding borders
2. **Pyramid**: Create multi-level downsampled pyramids for coarse-to-fine alignment
3. **Align**: Find tile-based alignment vectors between reference and comparison images
4. **Warp**: Apply alignment vectors to remap comparison image pixels
5. **Convert to RGBA**: Convert warped Bayer to RGBA superpixels for FFT merge
6. **FFT Merge**: Frequency domain merging with robustness weighting
7. **Convert to Bayer**: Back to Bayer pattern for output

## The Problem

### Symptoms
- `convert_to_rgba` shader produced all zeros for warped comparison images
- Reference image conversion worked correctly (sum=1861264)
- Warped image conversion failed (sum=0)

### Debug Output Pattern (from test_log.txt)
```
[WARP DEBUG] preparedAlt BEFORE warp (at data region): sum=11810287.00, mean=11810.2870
[WARP DEBUG] warpedAlt AFTER warp (at data region): sum=35080.34, mean=35.0803  ← 99.7% loss!
[CONVERT_RGBA] POST-SHADER: first1000=0.00, mid1000=0.00, total=12813312
[CONVERT_RGBA] ? SHADER PRODUCED ALL ZEROS!
```

### Root Cause Analysis

1. **Alignment vectors at edge tiles were too large and negative**:
   ```
   Alignment at tile (6,6): (-14, -6, 0, 0)
   Alignment at tile (7,7): (-14, -14, 0, 0)
   Alignment at tile (8,8): (-30, -14, 0, 0)
   ```

2. **These alignments pushed warp reads into zero-padding**:
   - Iteration padding: `padLeft=124, padTop=132` (full resolution)
   - For output pixel at `(124, 132)` with alignment `(-14, -14)`:
   - DownscaleFactor=2, so alignment becomes `(-28, -28)` at full res
   - Warp reads from `(124-28, 132-28) = (96, 104)`
   - But padding region is `[0, 124)` for X, so `96 < 124` = inside padding!
   - Padding is filled with zeros → warp reads zeros → output is zero

3. **Why edge tiles had bad alignments**:
   - Alignment search happens on downscaled pyramid levels
   - Edge tiles partially overlap with padding regions
   - At coarse levels, padding region has low contrast (all zeros)
   - Alignment search finds spurious "best" matches in uniform regions
   - Errors compound through pyramid refinement (search_dist=2 at each of 4 levels = up to ±30 accumulated)

## The Fix Implemented

### Approach
Clamp read coordinates in the warp shader to prevent reads from extending into the zero-padding region.

### Files Modified

#### 1. `BurstPhoto.Rendering/Shaders/Align.hlsl`

**Added padding parameters to cbuffer** (lines ~28-39):
```hlsl
// Warp clamping params (to prevent reads into zero-padding region)
int PadLeft;
int PadTop;
int ImageWidth;   // Total image width (including padding)
int ImageHeight;  // Total image height (including padding)
```

**Added helper function** (before warp_texture_bayer):
```hlsl
int2 clamp_read_coords(int readX, int readY)
{
    int minX = PadLeft;
    int maxX = ImageWidth - PadLeft - 1;
    int minY = PadTop;
    int maxY = ImageHeight - PadTop - 1;
    return int2(clamp(readX, minX, maxX), clamp(readY, minY, maxY));
}
```

**Modified warp_texture_bayer kernel** to use clamped reads:
```hlsl
// OLD:
float val0 = InTexture.Load(int3(x + prev_align0.x, y + prev_align0.y, 0)).r;

// NEW:
int2 read0 = clamp_read_coords(x + prev_align0.x, y + prev_align0.y);
float val0 = InTexture.Load(int3(read0.x, read0.y, 0)).r;
```

#### 2. `BurstPhoto.Rendering/ShaderTypes.cs`

**Updated AlignParams struct** (lines 10-37):
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct AlignParams
{
    // ... existing fields ...

    // Warp clamping params (to prevent reads into zero-padding region)
    public int PadLeft;
    public int PadTop;
    public int ImageWidth;   // Total image width (including padding)
    public int ImageHeight;  // Total image height (including padding)
}
```
Note: Removed the old Padding0/1/2 fields as the new params serve as both functional data and alignment padding.

#### 3. `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs`

**Updated ExecuteWarp signature** (line ~2388):
```csharp
private void ExecuteWarp(VulkanImage altImage, VulkanImage output, VulkanImage alignment,
                         TileInfo tileInfo, int padLeft = 0, int padTop = 0)
```

**Updated AlignParams initialization** (lines ~2462-2478):
```csharp
var alignParams = new AlignParams
{
    // ... existing fields ...
    PadLeft = padLeft,
    PadTop = padTop,
    ImageWidth = (int)altImage.Width,
    ImageHeight = (int)altImage.Height
};
```

**Updated call sites**:
- Line ~547 (frequency domain path): `ExecuteWarp(preparedAlt, warpedAlt, alignment, iterTileInfo, padLeft, padTop);`
- Line ~823 (spatial path): `ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo, pad, pad);`

## Build Status

```bash
cd "C:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET"
dotnet build BurstPhoto.CLI -c Debug
# Build succeeded with only warnings (NU1701 package compatibility, nullable refs)
```

## Testing Required

1. **Run the test process again** and check `docs/test_log.txt`
2. **Expected improvements**:
   - `warpedAlt AFTER warp (at data region)` should have sum closer to `preparedAlt BEFORE warp`
   - `CONVERT_RGBA POST-SHADER` should show non-zero values for mid1000
   - No more "SHADER PRODUCED ALL ZEROS" warnings

3. **Verify output image quality**:
   - The merged output should now include data from comparison images
   - Previously, comparison images contributed only zeros (no noise reduction benefit)

## Potential Follow-up Issues

### If the fix doesn't fully resolve the issue:

1. **Clamping may cause edge artifacts**: When alignment is clamped, pixels at edges will duplicate nearby pixels rather than reading the "correctly aligned" data. This is better than zeros but may cause visible edge artifacts.

2. **Alternative approaches to consider**:
   - **Pre-copy input to output**: Copy `preparedAlt` to `warpedAlt` before warping so unwarped pixels keep original values
   - **Reduce alignment at edges**: Attenuate alignment vectors near image boundaries
   - **Improve alignment search**: Add special handling for tiles that overlap with padding

3. **Check if Swift has same issue**: The Swift version uses identical warp shader logic but may not encounter this due to different padding strategy or image sizes

## Key Files Reference

| File | Purpose |
|------|---------|
| `BurstPhoto.Rendering/Shaders/Align.hlsl` | Warp and alignment compute shaders |
| `BurstPhoto.Rendering/ShaderTypes.cs` | C# structs matching HLSL cbuffers |
| `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` | Main GPU pipeline orchestration |
| `BurstPhoto.Core/Models/TileInfo.cs` | Tile grid calculation |
| `docs/test_log.txt` | Test output log (user updates this) |
| `burstphoto/align/align.metal` | Swift reference implementation |
| `burstphoto/align/align.swift` | Swift pipeline orchestration |

## Previous Fixes in This Session

1. **HalfTileSize bug**: Fixed calculation for Bayer images (should be `TileSize`, not `TileSize/2`)
2. **TileInfo dimensions**: Fixed to use padded pyramid dimensions instead of original image dimensions
3. **FillWithZeros**: Added call in ExecutePrepare to match Swift behavior (ensures padding is zeros)
4. **Warp clamping**: Current fix - prevent reads into zero-padding

## Debug Logging Locations

The codebase has extensive debug logging. Key points to check in logs:

- `[WARP DEBUG]` - Before/after warp data sums
- `[CONVERT_RGBA]` - Input/output data validation
- `[FFT DEBUG]` - FFT input/output values
- Alignment values at tiles (6,6), (7,7), (8,8) - edge tiles near data boundary

## Commands for Testing

```powershell
# Build
cd "C:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET"
dotnet build BurstPhoto.CLI -c Debug

# Run (user typically runs via their own test script)
# Output goes to docs/test_log.txt
```

## Contact Points in Code

If debugging continues, key functions to examine:

1. `ExecuteWarp()` at line ~2388 - Warp dispatch and parameter setup
2. `warp_texture_bayer()` in Align.hlsl - The actual warp kernel
3. `ExecuteConvertToRgba()` - RGBA conversion that was showing zeros
4. `clamp_read_coords()` - New helper function for coordinate clamping

---

**End of Handoff Document**
