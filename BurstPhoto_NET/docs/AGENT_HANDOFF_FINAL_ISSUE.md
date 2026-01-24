# Agent Handoff: HQ Mode Still Shows Dot Matrix Pattern

## Date: 2026-01-23

## Current Status
Fixed **10 dispatch bugs** throughout the codebase, achieving significant progress:
- ✅ Small 16×16 grid in corner → Full-image dot-matrix pattern
- ✅ Pattern expanded from top-left corner to ~1/4 of image (top-left quadrant)
- ❌ Still only ~25% of tiles rendering correctly (rest is sparse/black)

## Visual Symptom
- **Top-left quadrant** (approximately 128 tiles × 96 tiles) shows better detail with dot-matrix pattern
- Remaining **3/4 of image** is mostly black/sparse
- Suggests exactly **1/4 of expected tiles** are being processed (12,288 out of 49,152 tiles)

## All 10 Dispatch Bugs Fixed

### Root Cause
`ComputeKernel.Dispatch()` expects thread counts (or pixel dimensions) and internally divides by WorkGroupSize to calculate workgroup counts. Multiple locations were pre-calculating workgroup counts and passing those, causing **double division**.

### Fixed Locations (VulkanComputePipeline.cs)
1. **ExecuteForwardFft** (~line 1757) - Now passes `nTilesX×nTilesY` instead of `groupsX×groupsY`
2. **ExecuteBackwardFft** (~line 1678) - Same fix
3. **ExecuteDeconvoluteFrequency** (~line 2076) - Now passes tile counts
4. **ExecuteReduceArtifacts** (~line 2108) - Now passes **pixel dimensions** (shader operates per-pixel!)
5. **DispatchPixel helper** (~line 1498) - Now passes width×height
6. **DispatchTile helper** (~line 1521) - Now passes tile counts
7. **DispatchTileGrid helper** (~line 1542) - Now passes nTilesX×nTilesY
8. **ExecuteCalculateRms** (~line 2041) - Now passes tile counts
9. **UpsampleAlignment** (~line 2300) - Now passes tile counts
10. **CorrectUpsamplingError & TileDiff** (~lines 2351, 2387) - Now pass tile counts

## Remaining Issue: Only 1/4 of Image Working

### Key Facts from Test Log
From `docs/test_log.txt` line 320-322:
```
[ExecuteBackwardFft] TileSize=8, NumTextures=3, Tiles=256x192
[ExecuteBackwardFft] WorkGroupSize: 16x16, Expected workgroups: 16x12
[ExecuteBackwardFft] Dispatching 256x192 threads
```

**Expected behavior:**
- Dispatching: 256×192 threads
- WorkGroupSize: 16×16
- Should create: 16×12 workgroups = 192 total workgroups
- Total threads: 256×192 = 49,152 threads
- Each thread processes one 8×8 tile

**Actual behavior:**
- Only ~128×96 tiles working = 12,288 tiles = exactly **1/4**
- Pattern suggests only **8×6 workgroups** executing instead of 16×12
- This is **exactly half** the expected workgroup count in each dimension!

### Debug Attempt
Added shader debug output to write thread IDs to corner pixels, but:
- Debug values corrupted (negative values in test log lines 323-327)
- Likely because FFT computation **overwrites** the debug pixels immediately
- Need alternative debug approach (separate debug texture or barrier)

### Critical Hypotheses

#### Hypothesis 1: Dispatch Division Issue ⭐ MOST LIKELY
The `ComputeKernel.Dispatch()` method (ComputeKernel.cs lines 102-111) does:
```csharp
uint groupX = (uint)Math.Ceiling(width / (double)WorkGroupSize.x);
uint groupY = (uint)Math.Ceiling(height / (double)WorkGroupSize.y);
_ctx.Vk.CmdDispatch(cmd, groupX, groupY, groupZ);
```

With `Dispatch(256, 192, 1)` and WorkGroupSize `16×16`:
- `groupX = ceil(256/16) = 16` ✓
- `groupY = ceil(192/16) = 12` ✓
- Should dispatch 16×12 workgroups

**But we're only getting 8×6 workgroups (half in each dimension)!**

Possible causes:
- Integer division issue in Math.Ceiling calculation
- Vulkan driver interpreting dispatch parameters differently
- Pipeline binding issue (line 104 rebinds pipeline redundantly)

#### Hypothesis 2: Shader Bounds Check
The backward_fft shader (lines 30-32) does:
```hlsl
if (DTid.x >= (uint)nTilesX || DTid.y >= (uint)nTilesY)
    return;
```

If shader calculates different nTilesX/nTilesY than C# code, threads would be rejected. But test log shows correct calculation in C#.

#### Hypothesis 3: Texture Dimension Mismatch
The shader queries texture dimensions:
```hlsl
OutputTexture.GetDimensions(outputWidth, outputHeight);
int nTilesX = outputWidth / TileSize;
```

If outputWidth ≠ 2048 or outputHeight ≠ 1536, bounds check would reject threads. Need to verify actual texture dimensions in shader.

## Recommended Next Steps

### Step 1: Add Logging to ComputeKernel.Dispatch
Modify `ComputeKernel.cs` line 106-110 to log actual workgroup counts:
```csharp
uint groupX = (uint)Math.Ceiling(width / (double)WorkGroupSize.x);
uint groupY = (uint)Math.Ceiling(height / (double)WorkGroupSize.y);
Console.WriteLine($"[ComputeKernel.Dispatch] width={width}, height={height}, WorkGroupSize=({WorkGroupSize.x},{WorkGroupSize.y})");
Console.WriteLine($"[ComputeKernel.Dispatch] Calculated groups: {groupX}x{groupY}, calling CmdDispatch");
_ctx.Vk.CmdDispatch(cmd, groupX, groupY, groupZ);
```

### Step 2: Test with Different Dispatch Method
Temporarily bypass `ComputeKernel.Dispatch()` and call `CmdDispatch` directly with pre-calculated workgroup counts to verify Vulkan dispatch works:
```csharp
// In ExecuteBackwardFft, replace:
_kernelBackwardFft.Dispatch(cmd, (uint)nTilesX, (uint)nTilesY, 1);

// With direct dispatch:
uint groupsX = 16;
uint groupsY = 12;
_kernelBackwardFft.BindPipeline(cmd);
_ctx.Vk.CmdDispatch(cmd, groupsX, groupsY, 1);
```

If this works (full image), confirms issue is in `ComputeKernel.Dispatch()`.

### Step 3: Shader Debug with Separate Texture
Create a dedicated debug output texture to avoid FFT overwriting debug data:
```hlsl
// Add to FrequencyCommon.hlsli:
[[vk::binding(11, 0)]]
RWTexture2D<float4> DebugTexture : register(u11);

// In backward_fft.hlsl, write to DebugTexture instead of OutputTexture:
if (DTid.x < 16 && DTid.y < 16) {
    DebugTexture[int2(DTid.x, DTid.y)] = float4(float(DTid.x), float(DTid.y), float(nTilesX), float(nTilesY));
}
```

### Step 4: Disable Bounds Check
Temporarily comment out the bounds check in backward_fft.hlsl to see if all threads execute:
```hlsl
// if (DTid.x >= (uint)nTilesX || DTid.y >= (uint)nTilesY)
//     return;
```

If this fixes the issue, confirms bounds check is rejecting threads (shader calculating wrong tile counts).

## Test Environment
- GPU: NVIDIA GeForce RTX 3080 Laptop GPU
- Input: 4096×3072 Bayer images (3 frames)
- RGBA intermediate: 2048×1536
- FFT tile size: 8×8
- Expected tile grid: 256×192 tiles
- Actual working: ~128×96 tiles (1/4)

## Files Modified
- `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` - 10 dispatch fixes + debug logging
- `BurstPhoto.Rendering/Shaders/Frequency/backward_fft.hlsl` - Added debug output (gets overwritten)
- `BurstPhoto.Rendering/ComputeKernel.cs` - (Original dispatch implementation, not modified yet)

## Key Code Locations

### ComputeKernel.Dispatch (THE LIKELY CULPRIT)
**File:** `BurstPhoto.Rendering/ComputeKernel.cs`
**Lines:** 102-111
```csharp
public void Dispatch(CommandBuffer cmd, uint width, uint height = 1, uint depth = 1)
{
    _ctx.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, Pipeline);

    uint groupX = (uint)Math.Ceiling(width / (double)WorkGroupSize.x);
    uint groupY = (uint)Math.Ceiling(height / (double)WorkGroupSize.y);
    uint groupZ = (uint)Math.Ceiling(depth / (double)WorkGroupSize.z);

    _ctx.Vk.CmdDispatch(cmd, groupX, groupY, groupZ);
}
```

### ExecuteBackwardFft (Where dispatch is called)
**File:** `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs`
**Lines:** 1636-1679

### backward_fft.hlsl (Shader that processes tiles)
**File:** `BurstPhoto.Rendering/Shaders/Frequency/backward_fft.hlsl`
**Lines:** 8-115 (Key: lines 20-32 for tile calculation and bounds check)

## Additional Context

### Why 1/4 specifically?
If we're getting 8×6 workgroups instead of 16×12:
- 8×6 = 48 workgroups
- 16×12 = 192 workgroups
- 48/192 = 0.25 = exactly 1/4 ✓

This suggests the dispatch is somehow getting **half the workgroups in each dimension**.

### Pattern in Test Results
Across 4 iterations, consistently seeing ~1/4 of image working. This rules out:
- ❌ Random/intermittent failure
- ❌ Memory corruption
- ❌ Race conditions

Points to:
- ✅ Systematic issue in dispatch calculation
- ✅ Consistent shader behavior

## Success Criteria
When issue is fixed, you should see:
- ✅ Full 256×192 tile grid rendering (all tiles have data)
- ✅ No more dot-matrix pattern
- ✅ Complete HDR merged image across entire frame
- ✅ Similar quality to Fast mode but with frequency domain improvements

## Test Command
```bash
# Run HQ mode test with your sample images
dotnet run --project BurstPhoto.CLI -c Release -- <burst-images> --mode HigherQuality --output test_output.dng
```

Look for log lines:
- `[ExecuteBackwardFft] Dispatching Nx M threads`
- `[ComputeKernel.Dispatch] Calculated groups: X x Y` (if logging added)
- `[DEBUG] Max thread IDs in debug region: (X, Y)` (should show 15, 15 if working)

## References
- Previous handoff: `docs/AGENT_HANDOFF_DISPATCH_FIXES.md`
- Original issue: `docs/AGENT_HANDOFF_GREEN_GRID.md`
- Test log: `docs/test_log.txt` (lines 320-327 show dispatch parameters)
