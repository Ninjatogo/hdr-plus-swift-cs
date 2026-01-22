# Agent Handoff: Multi-Level Pyramid Alignment Implementation

**Created**: 2026-01-22
**Issue**: Frequency Domain Merge produces zero output for comparison images
**Root Cause**: Alignment search is broken - uses single-level instead of multi-level pyramid alignment

---

## Executive Summary

The Frequency Domain Merge algorithm in the `.NET` port is failing because the `ExecuteAlignmentSearch` method implements a simplified single-level alignment, not the multi-level hierarchical pyramid alignment that Swift uses. This causes:
1. Alignment vectors to be wrong or uninformative (values like `(62, -63)` that don't represent actual image motion)
2. After warp, valid image data is in the wrong location
3. The RGBA conversion reads from expected locations that contain zeros

**The sample images are tripod-shot, meaning there should be minimal/zero alignment needed.** The alignment values should be close to `(0, 0)` for static scenes.

---

## Current State Analysis

### What Works ✅
- Padding calculation (`padAlignX=252, padAlignY=252`) matches Swift
- `prepare_texture` shader correctly places data at `(padLeft, padTop)` offset
- `warp_texture_bayer` shader executes and produces non-zero output at mid-image
- `convert_to_rgba` and FFT shaders work when given valid input
- Reference image pipeline produces valid FFT output (`sum=8,642,301`)

### What's Broken ❌
- **Alignment search produces garbage values** like `(62, -63)` consistently across all iterations
- **Warp output at expected location is zeros** because alignment shifts data to wrong region
- **Comparison images produce zero FFT output** leading to zero accumulator output

---

## Technical Details

### Swift's Multi-Level Pyramid Alignment
Location: [frequency.swift](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/burstphoto/merge/frequency.swift) and [align.swift](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/burstphoto/align/align.swift)

Swift builds a pyramid with dynamic number of levels based on image size:

```swift
// frequency.swift lines 67-78
var downscale_factor_array = [mosaic_pattern_width]  // [2] for Bayer
var search_dist_array = [2]
var tile_size_array = [tile_size]
var res = min_image_dim / downscale_factor_array[0]

while (res > search_distance) {
    downscale_factor_array.append(2)
    search_dist_array.append(2)
    tile_size_array.append(max(tile_size_array.last!/2, 8))
    res /= 2
}
```

For a 4096x3072 image with `tile_size=64` and `search_distance=64`:
- Creates ~6 pyramid levels
- Each level uses `search_dist=2` (testing ±2 pixel offsets = 25 positions)
- Tile size halves at each level (minimum 8): [64, 32, 16, 8, 8, 8]

The alignment loop in `align_texture()` (align.swift lines 30-75):
1. Starts at COARSEST level (smallest image)
2. Initial alignment = zeros
3. At each level:
   - Upsample previous alignment × 2 (nearest neighbor)
   - Run `correct_upsampling_error` to pick best of 3 candidate alignments
   - Run `compute_tile_differences` to compute costs for 25 positions
   - Run `find_best_tile_alignment` to select best offset and accumulate
4. Final alignment is at finest pyramid level

### .NET's Current Implementation (BROKEN)
Location: [VulkanComputePipeline.cs](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET/BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs) lines 2062-2193

Current issues:
1. **Only uses level 0**: Lines 2112-2113 reference `refPyramid[0]` and `compPyramid[0]`
2. **Wrong TileInfo dimensions**: `TileInfo.Calculate(width, height, ...)` uses original dimensions, not padded pyramid dimensions
3. **Wrong search distance**: Uses user's `search_distance` (e.g., 64) instead of fixed `search_dist=2`
4. **Always uses zero prev_alignment**: Line 2093-2096 creates `dummyPrev` with zeros, never upsamples

---

## Required Changes

### 1. Implement Multi-Level Alignment Loop

Refactor `ExecuteAlignmentSearch` to iterate through pyramid levels from coarsest to finest:

```csharp
private void ExecuteAlignmentSearch(
    List<VulkanImage> refPyramid, 
    List<VulkanImage> compPyramid, 
    VulkanImage alignmentOut, 
    int[] tileSizeArray,      // Different tile size per level
    int[] searchDistArray,    // [2, 2, 2, ...] for each level  
    int[] downscaleFactors)   // [2, 2, 2, ...] for each level (or 0 for coarsest)
{
    VulkanImage prevAlignment = null; // Starts as null (zeros)
    VulkanImage currentAlignment;
    
    // Iterate from coarsest to finest (reverse order)
    for (int level = refPyramid.Count - 1; level >= 0; level--)
    {
        var refLayer = refPyramid[level];
        var compLayer = compPyramid[level];
        int tileSize = tileSizeArray[level];
        int searchDist = 2;  // ALWAYS 2 at each level!
        
        int nTilesX = refLayer.Width / (tileSize / 2) - 1;
        int nTilesY = refLayer.Height / (tileSize / 2) - 1;
        
        // 1. Upsample previous alignment (if not first level)
        if (prevAlignment != null)
        {
            prevAlignment = ExecuteUpsampleAlignment(prevAlignment, nTilesX, nTilesY);
        }
        else
        {
            // Create zero-initialized alignment for coarsest level
            prevAlignment = CreateZeroAlignment(nTilesX, nTilesY);
        }
        
        // 2. Correct upsampling error (test 3 candidates)
        prevAlignment = ExecuteCorrectUpsamplingError(
            refLayer, compLayer, prevAlignment, 
            downscaleFactors[level], level != 0, tileSize, nTilesX, nTilesY);
        
        // 3. Compute tile differences (25 positions)
        var tileDiff = ExecuteComputeTileDiff(
            refLayer, compLayer, prevAlignment,
            downscaleFactors[level], tileSize, searchDist);
        
        // 4. Find best alignment
        currentAlignment = ExecuteFindBestAlignment(
            tileDiff, prevAlignment, 
            downscaleFactors[level], searchDist, nTilesX, nTilesY);
        
        prevAlignment?.Dispose();
        prevAlignment = currentAlignment;
    }
    
    // Copy final alignment to output
    CopyAlignment(prevAlignment, alignmentOut);
}
```

### 2. Fix TileInfo to Use Pyramid Dimensions

The tile count must be calculated from the PYRAMID level dimensions, not original image dimensions:

```csharp
// At each pyramid level:
int nTilesX = pyramidLevel.Width / (tileSize / 2) - 1;
int nTilesY = pyramidLevel.Height / (tileSize / 2) - 1;
```

### 3. Add Missing Shaders/Kernels

Ensure these alignment shaders are properly compiled and dispatched:
- `upsample_nearest_int` - for upsampling alignment vectors
- `correct_upsampling_error` - tests 3 candidate alignments  
- `compute_tile_differences25` - optimized for search_dist=2 (25 positions)
- `compute_tile_differences_exposure25` - for non-uniform exposure
- `find_best_tile_alignment` - selects best offset

### 4. Fix Warp DownscaleFactor

The warp shader uses `DownscaleFactor` to scale alignment when applying to full-resolution image:
- Current: `DownscaleFactor = 2` (set in earlier fix)
- This should match `downscale_factor_array[0]` from Swift (the first downscale = mosaic_pattern_width = 2 for Bayer)

---

## Key Files to Modify

| File | Changes Needed |
|------|----------------|
| [VulkanComputePipeline.cs](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET/BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs) | Refactor `ExecuteAlignmentSearch` to multi-level loop |
| [TileInfo.cs](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET/BurstPhoto.Core/Models/TileInfo.cs) | May need per-level tile info calculation |
| [Align.hlsl](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/BurstPhoto_NET/BurstPhoto.Rendering/Shaders/Align.hlsl) | Verify all shaders match Swift implementation |

---

## Reference Implementation

### Swift align_texture() Flow
```
align.swift lines 14-81:

1. Initialize 1x1 alignment texture (zeros)
2. Build comparison pyramid with avg_pool
3. For level = (coarsest) to (finest):
   a. Load ref_layer and comp_layer from pyramids
   b. Calculate n_tiles_x, n_tiles_y from layer dimensions
   c. Get downscale_factor from PREVIOUS level (0 for coarsest)
   d. Upsample prev_alignment to current tile grid
   e. correct_upsampling_error() - pick best of 3 candidates
   f. compute_tile_diff() - compute 25-position cost grid
   g. find_best_tile_alignment() - select best offset
4. warp_texture() with final alignment
```

### Swift Shader Reference Files
- [align.metal](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/burstphoto/align/align.metal) - All alignment shaders
- [texture.metal](file:///c:/Users/maxwe/RiderProjects/hdr-plus-swift-cs/burstphoto/texture/texture.metal) - Upsample shaders

---

## Testing Strategy

1. **Zero alignment test**: For tripod images, alignment should be near `(0, 0)`. Add logging to verify.

2. **Warp identity test**: If alignment is `(0, 0)`, warp output should equal input at same locations.

3. **RGBA conversion test**: After successful warp, RGBA conversion at `(padLeft, padTop)` should have data.

4. **FFT output test**: Non-zero RGBA input should produce non-zero FFT output.

5. **Full pipeline test**: FinalAccumulator should have non-zero sum after 4 iterations.

---

## Debug Logging Currently in Place

The codebase has extensive debug logging already added during this investigation:
- Input validation in `ExecuteWarp` (lines 2206-2245)
- Alignment data inspection (lines 2224-2245)
- Warp output validation (lines 2295-2345)
- RGBA conversion validation (lines 543-550)
- FFT input/output validation (in ExecuteForwardFft)

These can be used to verify the fix is working.

---

## Expected Outcome

After implementing multi-level alignment:
1. Alignment values for tripod images should be near `(0, 0)`
2. Warp output at `(padLeft, padTop)` should have valid data
3. RGBA conversion should produce non-zero output
4. FFT of comparison images should produce non-zero output
5. FinalAccumulator should accumulate properly across 4 iterations
6. Output DNG should contain valid merged image data

---

## Questions for New Agent

Before starting implementation:
1. Should we implement the full multi-level alignment or a simplified version?
2. Are all required shaders (upsample_nearest_int, correct_upsampling_error, etc.) already in Align.hlsl?
3. Should TileInfo be refactored to support per-level calculations, or calculate inline?
