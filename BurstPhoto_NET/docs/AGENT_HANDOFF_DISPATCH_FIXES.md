# Agent Handoff: Dispatch Bug Fixes and Remaining Issue

## Date: 2026-01-23

## Summary
Fixed 10 critical dispatch bugs across the frequency domain and alignment pipelines. However, HQ mode still shows dot-matrix pattern with only ~1/4 of image (top-left quadrant) properly filled.

## Bugs Fixed

### Root Cause
`ComputeKernel.Dispatch()` expects thread counts and internally divides by WorkGroupSize to calculate workgroup counts. Multiple locations were pre-calculating workgroup counts and passing those, causing double division.

### All 10 Fixes
1. **ExecuteForwardFft** (~line 1757) - Now passes `nTilesX×nTilesY` instead of pre-calculated groups
2. **ExecuteBackwardFft** (~line 1678) - Same fix
3. **ExecuteDeconvoluteFrequency** (~line 2076) - Now passes tile counts
4. **ExecuteReduceArtifacts** (~line 2108) - Now passes pixel dimensions (shader is per-pixel!)
5. **DispatchPixel helper** (~line 1498) - Now passes width×height
6. **DispatchTile helper** (~line 1521) - Now passes tile counts
7. **DispatchTileGrid helper** (~line 1542) - Now passes nTilesX×nTilesY
8. **ExecuteCalculateRms** (~line 2041) - Now passes tile counts
9. **UpsampleAlignment** (~line 2300) - Now passes tile counts
10. **CorrectUpsamplingError & TileDiff** (~lines 2351, 2387) - Now pass tile counts

## Current Issue: Only 1/4 of Image Working

### Symptoms
- Top-left quadrant (~half width × half height) shows better detail with dot-matrix pattern
- Remaining 3/4 of image is mostly sparse/black
- Pattern suggests ~128×96 tiles working out of 256×192 total = exactly 1/4

### Key Facts
- Dispatching: `Dispatch(256, 192, 1)` threads
- WorkGroupSize: 16×16
- Expected workgroups: 16×12 = 192 workgroups
- Expected total threads: 256×192 = 49,152 threads
- Actual working region: ~128×96 = 12,288 tiles (1/4)

### Hypotheses to Investigate

1. **Shader Bounds Check Issue**
   - Backward FFT shader (line 20-25) calculates `nTilesX = outputWidth / TileSize`
   - If `outputWidth` is wrong in shader, threads would be rejected
   - Check: Does shader see correct texture dimensions?

2. **Integer Division in Dispatch**
   - `ComputeKernel.Dispatch()` line 106-107 does division
   - Verify actual workgroup counts being passed to Vulkan
   - Add logging to see actual `CmdDispatch` parameters

3. **Multiple Dispatch Calls**
   - Check if there's another shader execution overwriting data
   - Verify reduce_artifacts and convert_to_bayer are working correctly

4. **Vulkan Driver Limits**
   - Unlikely but check if there's a workgroup count limit
   - 16×12 workgroups should be well within any reasonable limit

### Debug Steps to Try

1. **Add logging in backward_fft shader**
   - Log thread IDs that pass bounds check
   - Verify nTilesX/nTilesY calculations in shader

2. **Verify texture dimensions**
   - Check that outputTextureRgba is actually 2048×1536
   - Verify shader sees same dimensions

3. **Check reduce_artifacts dispatch**
   - This was changed to pixel-based dispatch (2048×1536)
   - Verify it's not overwriting with sparse data

4. **Test without reduce_artifacts**
   - Temporarily skip reduce_artifacts to see if backward FFT alone works

## Files Modified
- `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` - 10 dispatch fixes

## Next Steps
1. Run test with new logging to see actual dispatch parameters
2. Check if shader bounds check is rejecting threads
3. Verify all intermediate textures have correct dimensions
4. Consider adding shader-side debug output to verify thread execution

## Test Command
```bash
# Use same command as before to test HQ mode
```
