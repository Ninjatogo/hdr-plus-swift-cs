# Agent Handoff: RGBA Dimensions Fix

**Date**: 2026-01-22
**Status**: Fix implemented, ready for testing
**Branch**: HigherQualityFrequencyDomainMerging

## Executive Summary

Fixed a critical bug in the frequency domain merge pipeline where RGBA output dimensions were incorrectly calculated, causing only a small top-left region of the image to contain valid data (the "green rectangle" issue). The rest of the output was zeros, resulting in severely degraded image quality.

## Problem Analysis

### Symptoms (from test_log.txt)
```
[CONVERT_RGBA] POST-SHADER: first1000=1392961.92, mid1000=0.00, total=12813312
[WARP DEBUG] alignedTextureRgba AFTER convert: first1000 sum=1392961.92, mid1000 sum=0.00
```

**Pattern observed:**
- `first1000` (top-left corner) had data ✓
- `mid1000` (middle/right regions) was ALWAYS ZERO ❌
- User reported seeing a "green rectangle in the top-left corner"

This pattern repeated across all 4 iterations, indicating a systematic dimension mismatch.

### Root Cause

**Location**: `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs`, line 385-386

**Incorrect calculation** (BEFORE fix):
```csharp
int rgbaWidth = (iterOutWidth - 2 * cropMergeX) / 2;
int rgbaHeight = (iterOutHeight - 2 * cropMergeY) / 2;
```

With values from iteration 1:
- `iterOutWidth = 4352` (includes padding)
- `iterOutHeight = 3328` (includes padding)
- `cropMergeX = 12, cropMergeY = 12`
- **Calculated RGBA**: `(4352-24)/2 x (3328-24)/2 = 2164x1652`

But the actual texture created was `2064x1552` (100 pixels smaller in each dimension).

**Why this caused the bug:**

1. The `preparedRef` texture is `4352x3328` with data region:
   - X: `[padLeft=124, padLeft+width=124+4096] = [124, 4220)`
   - Y: `[padTop=132, padTop+height=132+3072] = [132, 3204)`

2. The `convert_to_rgba` shader reads from:
   ```hlsl
   inPos = uint2(gid.x * 2 + PadLeft, gid.y * 2 + PadTop)
   ```

3. With RGBA dimensions `2064x1552`, threads with high `gid` values read beyond the valid data:
   - Thread `gid.x=2000, gid.y=1500` reads from `inPos=(4124, 3132)` ✓ (within bounds)
   - Thread `gid.x=2063, gid.y=1551` reads from `inPos=(4250, 3234)` ❌ (beyond data region!)

4. Reads beyond the data region hit either:
   - Clamped edge pixels (from the warp clamping fix)
   - Or zeros from padding
   - Result: Invalid/zero output

**The correct calculation:**

The RGBA dimensions should match the **actual data region size**, which is simply:
```csharp
int rgbaWidth = width / 2;   // = 4096/2 = 2048
int rgbaHeight = height / 2;  // = 3072/2 = 1536
```

Where `width=4096, height=3072` are the original image dimensions (without padding).

## The Fix

**File**: `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs`
**Lines**: 383-390

**BEFORE:**
```csharp
// RGBA dimensions: Swift uses (BayerWidth - 2*cropMergeX) / 2
// This crops out the padding border before superpixel packing
int rgbaWidth = (iterOutWidth - 2 * cropMergeX) / 2;
int rgbaHeight = (iterOutHeight - 2 * cropMergeY) / 2;
int ftWidth = rgbaWidth * 2;
int ftHeight = rgbaHeight;
```

**AFTER:**
```csharp
// RGBA dimensions: Output should match the actual data region (excluding padding)
// The preparedRef texture has data in region [padLeft:padLeft+width, padTop:padTop+height]
// RGBA packs 2x2 Bayer pixels into 1 RGBA pixel, so dimensions are halved
int rgbaWidth = width / 2;
int rgbaHeight = height / 2;
Console.WriteLine($"[DEBUG] Iteration {iteration}: RGBA dimensions: {rgbaWidth}x{rgbaHeight} (from data region {width}x{height})");
int ftWidth = rgbaWidth * 2;
int ftHeight = rgbaHeight;
```

## Impact

This fix ensures:
1. ✅ RGBA texture dimensions match the actual data region
2. ✅ All dispatched shader threads correspond to valid input data
3. ✅ No reads beyond the valid data boundary
4. ✅ Full image coverage (not just top-left corner)
5. ✅ All 4 iterations will process the complete image

**Expected improvement:**
- RGBA conversion should now show non-zero values for BOTH `first1000` AND `mid1000`
- The "green rectangle" should expand to fill the entire image
- Image quality should improve dramatically (from ~25% coverage to 100% coverage)

## Testing

### Build Status
```bash
cd "C:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET"
dotnet build BurstPhoto.CLI -c Debug
# Build succeeded with only warnings (NU1701, nullable refs)
```

### How to Test

1. **Run the same test that produced the green rectangle:**
   ```bash
   dotnet run --project BurstPhoto.CLI -- process -i <images> -m higherquality -o output.dng
   ```

2. **Expected improvements in logs:**
   - `[DEBUG] Iteration X: RGBA dimensions:` should show `2048x1536` (not `2064x1552`)
   - `[CONVERT_RGBA] POST-SHADER:` should show non-zero `mid1000` values
   - No more "SHADER PRODUCED ALL ZEROS" warnings

3. **Expected visual improvements:**
   - Output image should be fully processed (not just top-left corner)
   - Green/color data should fill the entire frame
   - Overall image quality should match expectations for HDR+ processing

## Technical Deep Dive

### Why was the old calculation wrong?

The comment said "Swift uses (BayerWidth - 2*cropMergeX) / 2", but this is **incorrect** for the .NET port. Here's why:

**In Swift (frequency.swift):**
- The `cropMergeX/Y` padding is used to **crop the final output**
- RGBA conversion happens on the **full padded texture**
- Then the result is cropped before FFT processing

**In .NET (current implementation):**
- RGBA conversion receives `padLeft/padTop` parameters to skip padding
- The shader reads from `gid*2 + padLeft/padTop`, effectively **cropping during conversion**
- Therefore, RGBA dimensions must match the **data region after padding is excluded**

The fix aligns the .NET implementation with the correct behavior: RGBA dimensions = data region dimensions / 2.

### Relationship to Previous Fixes

This bug was **exposed by** the warp clamping fix (AGENT_HANDOFF_WARP_CLAMPING.md):

1. **Before warp clamping**: Reads beyond data boundaries would fetch random GPU memory → unpredictable output
2. **After warp clamping**: Reads beyond data boundaries are clamped to valid region → exposes dimension mismatch
3. **After this fix**: RGBA dimensions match data region → no out-of-bounds reads

So the warp clamping fix was **necessary** but **revealed** this deeper dimensional bug.

## Key Files Modified

| File | Lines | Change |
|------|-------|--------|
| `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` | 383-390 | Changed RGBA dimension calculation from `(iterOutWidth - 2*cropMergeX)/2` to `width/2` |

## Follow-up Items

After testing confirms this fix:

1. ✅ Remove old comment about Swift using `(BayerWidth - 2*cropMergeX) / 2` - it was misleading
2. ✅ Consider adding validation: `assert(rgbaWidth * 2 + 2*padLeft <= iterOutWidth)`
3. ⚠️ Check if cropMergeX/Y variables are still needed - they may now be unused
4. 📋 Update BACKLOG.md to mark frequency domain merge as fully working

## Debugging Commands

```bash
# Build
cd "C:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET"
dotnet build BurstPhoto.CLI -c Debug

# Test with same inputs
dotnet run --project BurstPhoto.CLI -- process \
  -i "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0018_D.DNG" \
  -i "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0020_D.DNG" \
  -i "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0019_D.DNG" \
  -m higherquality \
  -o output.dng

# Check logs for key indicators
grep "RGBA dimensions:" docs/test_log.txt
grep "mid1000=" docs/test_log.txt
```

## Expected Log Output (After Fix)

```
[DEBUG] Iteration 1: RGBA dimensions: 2048x1536 (from data region 4096x3072)
[CONVERT_RGBA] Input: 4352x3328, Output: 2048x1536
[DEBUG] ExecuteConvertToRgba: Input=4352x3328, Output=2048x1536, CropX=124, CropY=132, Dispatch=128x96
[CONVERT_RGBA] POST-SHADER: first1000=<non-zero>, mid1000=<non-zero>, total=...
```

Key changes:
- RGBA output is now `2048x1536` (not `2064x1552`)
- Dispatch is `128x96` workgroups (not `129x97`)
- Both `first1000` AND `mid1000` should have non-zero values

---

**End of Handoff Document**

## Context for Next Agent

If the fix works:
- The frequency domain merge should now produce correct output for all 4 iterations
- The "green rectangle" issue should be resolved
- Next step: Verify output quality matches expectations and compare with Swift reference

If issues persist:
- Check if there are other dimension mismatches in the FFT or merge stages
- Verify that the pyramid dimensions align with RGBA dimensions
- Consider if TileInfo calculations need adjustment based on new RGBA dimensions
