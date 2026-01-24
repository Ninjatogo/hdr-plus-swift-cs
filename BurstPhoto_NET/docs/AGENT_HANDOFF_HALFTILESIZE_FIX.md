# Agent Handoff: HalfTileSize Fix and Alignment Investigation

## Date: 2026-01-22

## Summary

Fixed one bug and identified the root cause of the "green rectangle in top-left corner" issue in HQ (frequency domain) merging mode.

## Bug Fixed: HalfTileSize Parameter for Warp Shader

### Location
`BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs`, lines 2412-2426

### Issue
The warp shader (`warp_texture_bayer`) expects a `half_tile_size` parameter that determines how pixels map to the tile grid.

**Swift** (align.swift line 253):
```swift
command_encoder.setBytes([Int32((downscale_factor==2 ? 1 : downscale_factor)*tile_info.tile_size)], ...)
```
For Bayer images (downscale_factor=2): passes `1 * tile_size` = `tile_size` (e.g., 16)

**.NET (BEFORE fix)**:
```csharp
HalfTileSize = tileInfo.TileSize / 2,  // e.g., 16/2 = 8 - WRONG!
```

### Fix Applied
```csharp
// For Bayer images (mosaic_pattern_width=2), DownscaleFactor = 2
// Swift warp_texture passes: (downscale_factor==2 ? 1 : downscale_factor) * tile_size
// So for Bayer: half_tile_size = 1 * tile_size = tile_size (NOT tile_size/2!)
int downscaleFactor = 2; // Bayer
int halfTileSizeForWarp = (downscaleFactor == 2 ? 1 : downscaleFactor) * tileInfo.TileSize;

var alignParams = new AlignParams
{
    // ...
    HalfTileSize = halfTileSizeForWarp,
    // ...
};
```

### Impact
This fix corrects the pixel-to-tile-grid mapping in the warp shader. With wrong HalfTileSize, pixels were looking up alignment vectors from wrong tile positions, causing data to be shifted incorrectly.

---

## Root Cause Identified: Alignment Produces Wrong Values

### Symptom from test_log.txt
```
First alignment vector: (24, -25, 0, 0)
Mid alignment vector:   (-32, -32, 0, 0)
```

For **tripod-shot images** (stationary camera), alignment values should be near `(0, 0)`. Values like `(24, -25)` are way too large.

### Effect
With `DownscaleFactor=2`, the warp shader multiplies alignment by 2:
- `(24, -25)` becomes `(48, -50)` pixel shift
- This shifts data ~50 pixels from where it should be
- The `convert_to_rgba` shader reads from `(cropX, cropY)` offset expecting data there
- Result: no data at expected location = black/zero output

### Why Warp Shows Data at "Mid" but Not at "Data Region"
The warp produces output, but shifted. Debug validation shows:
- `Output data (mid 1000): sum=1,210,153` → HAS DATA (but wrong location)
- `Output data (at data region): sum=0` → NO DATA where expected

---

## Remaining Issue: TileInfo Calculation Mismatch

### Swift's Approach
In `align.swift`, TileInfo is calculated **per pyramid level**:
```swift
for i in (0 ... downscale_factor_array.count-1).reversed() {
    let tile_size = tile_size_array[i]
    let ref_layer = ref_pyramid[i]

    // Calculate tile grid for THIS level
    let n_tiles_x = ref_layer.width / (tile_size / 2) - 1
    let n_tiles_y = ref_layer.height / (tile_size / 2) - 1

    tile_info = TileInfo(tile_size: tile_size, ..., n_tiles_x: n_tiles_x, ...)
}
// Final tile_info is from level 0 (finest resolution)
// Warp uses this tile_info
```

### .NET's Approach (Current)
TileInfo is calculated **once** outside the alignment function:
```csharp
// Line 280
var tileInfo = TileInfo.Calculate(width / 2, height / 2, tileSize, searchDist);

// Line 517 - uses same tileInfo for warp
ExecuteWarp(preparedAlt, warpedAlt, alignment, tileInfo);
```

### Problem
The `TileInfo.Calculate` uses `width/2, height/2` (half of original image), but the pyramid level 0 might have different dimensions due to:
1. Padding adjustments
2. Rounding to even numbers

The alignment texture dimensions must match what the warp shader expects.

---

## Next Steps

1. **Test the HalfTileSize fix** - Run HQ mode and check if data now appears at correct location
2. **Investigate alignment values** - If still producing garbage values, the pyramid alignment search needs debugging
3. **Consider per-level TileInfo** - Ensure alignment output dimensions match warp expectations

---

## Files Modified
- `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` - HalfTileSize fix

## Key Log Lines to Watch
```
║       First alignment vector: (x, y, 0, 0)  // Should be near (0,0) for tripod
║       TileInfo: TileSize=X, NTilesX=Y, NTilesY=Z
[WARP DEBUG] warpedAlt AFTER warp (at data region): sum=X  // Should be > 0
```

## Test Command
```bash
dotnet run --project BurstPhoto.CLI -- process -i <images> -o output.dng -m higherquality
```

---

## Session 2 Update (2026-01-22 continued)

### HalfTileSize Fix Verified Working
After running the test with the HalfTileSize fix:
- Alignment values are now correct: `(0, 0)` and `(-2, -2)` instead of `(24, -25)` and `(-32, -32)`
- Warp output has data at the expected location
- TileInfo dimensions are now correct: `255x191` instead of the previous `511x383`

### New Issue Identified: convert_to_rgba Producing Zeros

**Symptom:**
```
[WARP DEBUG] warpedAlt AFTER warp (at data region): sum=35080.34  // ✓ Has data
[CONVERT_RGBA] Input data (at offset cropY*W+cropX): sum=35080.34  // ✓ Has data
[WARP DEBUG] alignedTextureRgba AFTER convert: sum=0.00  // ❌ Zero!
```

**Analysis:**
1. The warp shader correctly produces output at the expected location
2. The CPU validation confirms data exists at `(cropX, cropY)` before the convert shader
3. But the convert_to_rgba shader produces all zeros

**Shader Logic (TextureOps.hlsl:114-132):**
```hlsl
void convert_to_rgba(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint2 inPos = uint2(gid.x * 2 + PadLeft, gid.y * 2 + PadTop);

    float p0 = InTextureFloat.Load(int3(inPos.x,   inPos.y,   0));
    float p1 = InTextureFloat.Load(int3(inPos.x+1, inPos.y,   0));
    float p2 = InTextureFloat.Load(int3(inPos.x,   inPos.y+1, 0));
    float p3 = InTextureFloat.Load(int3(inPos.x+1, inPos.y+1, 0));

    OutTextureRGBA[gid] = float4(p0, p1, p2, p3);
}
```

**Verified Correct:**
- Descriptor bindings match shader expectations
- Parameters (`PadLeft=124, PadTop=132`) are passed correctly
- Input coordinates are within bounds

**Possible Causes to Investigate:**
1. GPU memory coherency issue between warp and convert
2. Image layout transition issue
3. Shader compilation/linking issue
4. Descriptor set allocation conflict

**Added Debug Output:**
Enhanced validation to sample from both first 1000 AND mid 1000 floats to determine if output is zeros everywhere or just at the start.
