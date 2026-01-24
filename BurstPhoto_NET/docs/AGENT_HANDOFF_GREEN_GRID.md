# HQ Mode Debug Handoff Document

## Bug Summary
HQ (Frequency Domain) mode in BurstPhoto produces incorrect output.

### Current Visual Symptom (Updated)
**The output now shows a 16×16 grid of greenish-yellow and white blobs** instead of the expected merged HDR image.

> [!IMPORTANT]
> The 16×16 grid pattern strongly suggests an issue with the **FFT tile processing**. The frequency domain merge uses 8×8 tiles for FFT, and the visual pattern matching a power-of-2 grid indicates data is only being processed/written correctly for certain tile positions.

### Previous Symptom
Before the dispatch fixes, the output showed a "green rectangle" in the top-left corner only (data only in first ~128×96 pixels).

## Work Completed

### Fix 1: ExecuteConvertToRgba Dispatch Bug ✅
**File**: `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` (around line 1903-1907)

**Problem**: Was passing pre-calculated groups to `Dispatch()`, but `ComputeKernel.Dispatch()` internally divides by WorkGroupSize again.
- Passed: `128, 96` (groups)
- Dispatch calculated: `128/16, 96/16` = `8, 6` groups
- Result: Only 128×96 pixels written instead of 2048×1536

**Fix**: Now passes output dimensions directly:
```csharp
_kernelConvertToRgba.Dispatch(cmd, rgbaOutput.Width, rgbaOutput.Height, 1);
```

### Fix 2: ExecuteConvertToBayer Dispatch Bug ✅
**File**: Same file, around line 1987-1990

**Problem**: Same issue - passing pre-calculated groups instead of dimensions.

**Fix**: 
```csharp
_kernelConvertToBayer.Dispatch(cmd, bayerOutput.Width, bayerOutput.Height, 1);
```

### Fix 3: convert_to_bayer Output Dimensions ✅  
**File**: Same file, around line 695-698

**Problem**: `outputTextureBayer` used padded dimensions (`iterOutWidth × iterOutHeight`) but the RGBA input was unpadded, causing OOB reads.

**Fix**: Changed to unpadded dimensions:
```csharp
int bayerWidth = rgbaWidth * 2;   // = width (4096)
int bayerHeight = rgbaHeight * 2; // = height (3072)
using var outputTextureBayer = new VulkanImage(_ctx, (uint)bayerWidth, (uint)bayerHeight, ...);
```

---

## Remaining Bug: FinalAccumulator Download Returns Zeros

### Evidence from Test Output (`test_output.txt`)

**After Iteration 4 SetData** (lines 1125-1127):
```
[DEBUG] After SetData: accumulator data region sum=104083.95 (at offset 1159452)
[DEBUG] First 5 values at data region: 10.6358, 10.6028, 6.8398, 15.0671, 7.2604
[DEBUG] CPU accData data region sum=104083.95
```
✅ The accumulator HAS valid data after SetData!

**Final Download** (lines 1131-1133):
```
[VulkanComputePipeline] Downloading from finalAccumulator: Width=4600, Height=3576
[VulkanComputePipeline] Downloaded 16449600 floats (expected 16449600)
[VulkanComputePipeline] FinalAccumulator stats: sum=0.00, min=0.00, max=0.00, mean=0.00
```
❌ But the download returns ALL ZEROS!

### Key Observation
- Data correctly accumulates across 4 iterations (sum goes 26k → 52k → 78k → 104k)
- After final iteration's `SetData`, the data is verified present by immediately reading back
- But when downloaded later for exposure correction, all zeros

### Likely Causes to Investigate
1. **Image Layout Transition Issue**: `SetData` transitions layout to TransferDst→General, but something later might invalidate it
2. **Different Image Being Downloaded**: Check if the `finalAccumulator` object reference is the same between iterations and final download
3. **Staging Buffer Scope**: Check if `VulkanImage.GetData` has any issue with the staging buffer

---

## Key Files

| File | Purpose |
|------|---------|
| `VulkanComputePipeline.cs` | Main pipeline orchestration, iteration loop, accumulator management |
| `VulkanImage.cs` | `SetData`/`GetData` implementation for GPU textures |
| `TextureOps.hlsl` | `convert_to_rgba` and `convert_to_bayer` shaders |
| `ComputeKernel.cs` | `Dispatch` method (divides by WorkGroupSize) |

---

## Code Locations to Check

### Accumulator Creation (line ~290-295)
```csharp
using var finalAccumulator = new VulkanImage(_ctx, (uint)accWidth, (uint)accHeight, Format.R32Sfloat, ...);
```
- accWidth = 4600, accHeight = 3576

### Accumulator Update Loop (lines ~724-736)
```csharp
float[] accData = finalAccumulator.GetData<float>();
for (int y = 0; y < height; y++) {
    for (int x = 0; x < width; x++) {
        int srcIdx = y * bayerWidth + x;
        int dstIdx = (y + padAlignY) * accWidth + (x + padAlignX);
        accData[dstIdx] += iterOutput[srcIdx] / 4.0f;
    }
}
finalAccumulator.SetData(accData);
```

### Final Download (search for "All 4 iterations complete", line ~769)
- After this line, the `finalAccumulator` is downloaded and used for exposure correction
- The download happens somewhere around line 770-800

### VulkanImage.SetData (line 200-241 in VulkanImage.cs)
- Creates staging buffer, copies data, transitions layout
- Important: Uses `BufferImageCopy` with `BufferRowLength = 0` (tightly packed)

### VulkanImage.GetData (line 265-307 in VulkanImage.cs)
- Transitions to TransferSrc, copies to staging, reads back
- Check if layout transitions are correct

---

## Debug Commands to Run

```powershell
cd "C:\Users\maxwe\RiderProjects\hdr-plus-swift-cs\BurstPhoto_NET"
dotnet build BurstPhoto.CLI -c Release
.\BurstPhoto.CLI\bin\Release\net10.0-windows\BurstPhoto.CLI.exe process `
    ".\Burst Samples\Bracketed Exposure\Input\DJI_20250925172104_0018_D.DNG" `
    ".\Burst Samples\Bracketed Exposure\Input\DJI_20250925172104_0019_D.DNG" `
    ".\Burst Samples\Bracketed Exposure\Input\DJI_20250925172104_0020_D.DNG" `
    --algorithm HigherQuality --tile-size Medium --noise-reduction 13 `
    --exposure-control LinearFullRange --gpu 1 `
    -o "C:\Users\maxwe\Desktop\TestOutput\Debug_Test" 2>&1 | Tee-Object test_output.txt
```

---

## Suggested Next Steps

1. **Add debug immediately before final download**:
   - Right after "All 4 iterations complete", add a GetData call with debug to verify accumulator still has data

2. **Check if finalAccumulator is the same object**:
   - Verify the `finalAccumulator` reference isn't being replaced or disposed

3. **Check VulkanImage.SetData memory barrier**:
   - Ensure proper synchronization after staging buffer copy

4. **Verify layout state**:
   - Add logging for `CurrentLayout` at key points

5. **Check for GPU memory issues**:
   - The accumulator is large (4600×3576 = 16.4M floats = 65.8 MB)
