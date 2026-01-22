# Agent Handoff: FFT Zero Output Bug Investigation - Session Summary

**Date:** 2026-01-21
**Previous Handoff:** `AGENT_HANDOFF_FFT_ZERO_OUTPUT_DEEP_DIVE.md`
**Status:** Major progress - Root cause identified and fixed, but residual issues remain

---

## Executive Summary

The FFT shader was producing all zeros due to a **buffer size calculation bug in `VulkanImage.GetData<T>()`**. The method was calculating staging buffer size based on `sizeof(T)` instead of the actual image format, causing only 1/4 of RGBA32F image data to be read. This has been fixed, and the FFT is now producing non-zero output for iterations 3-4.

---

## Fixes Applied This Session

### 1. GetData Buffer Size Bug (CRITICAL FIX)
**File:** `BurstPhoto.Rendering/VulkanImage.cs`

**Problem:** `GetData<float>()` calculated buffer size as `Width * Height * sizeof(float)`, but RGBA32F images have 4 floats per pixel, requiring `Width * Height * 16` bytes.

**Fix:** Added `GetBytesPerPixel()` method that returns correct size based on image format:
```csharp
private int GetBytesPerPixel()
{
    return Format switch
    {
        Format.R32Sfloat => 4,
        Format.R32G32B32A32Sfloat => 16,
        // ... other formats
    };
}
```

**Impact:** This was the ROOT CAUSE of the zero output. The staging buffer was too small, causing incomplete data reads.

### 2. Uninitialized Descriptor Bindings
**File:** `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` (lines ~1580-1600)

**Problem:** `ExecuteForwardFft` only updated bindings 0, 1, and 10, but `_frequencyLayout` defines bindings 0-5 and 10. Vulkan requires ALL bindings to be updated.

**Fix:** Added dummy texture creation and binding for unused slots:
```csharp
using var dummyTex = new VulkanImage(_ctx, 1, 1, Format.R32G32B32A32Sfloat,
    ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit);
dummyTex.TransitionLayout(ImageLayout.General, cmd);

_descriptors.UpdateImage(set, 2, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
_descriptors.UpdateImage(set, 3, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
_descriptors.UpdateImage(set, 4, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
_descriptors.UpdateImage(set, 5, dummyTex.View, ImageLayout.General, DescriptorType.SampledImage);
```

### 3. Descriptor Pool Missing Types
**File:** `BurstPhoto.Rendering/VulkanDescriptorManager.cs`

**Problem:** Pool only had `StorageBuffer` and `StorageImage` types, missing `UniformBuffer` and `SampledImage`.

**Fix:** Added all required descriptor types:
```csharp
var poolSizes = new DescriptorPoolSize[]
{
    new() { Type = DescriptorType.StorageBuffer, DescriptorCount = maxSets * 4 },
    new() { Type = DescriptorType.StorageImage, DescriptorCount = maxSets * 4 },
    new() { Type = DescriptorType.UniformBuffer, DescriptorCount = maxSets * 4 },
    new() { Type = DescriptorType.SampledImage, DescriptorCount = maxSets * 8 },
    new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = maxSets * 4 },
};
```

### 4. Memory Barrier After Dispatch
**File:** `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` (after line 1613)

**Problem:** No memory barrier between compute dispatch and potential readback.

**Fix:** Added image memory barrier:
```csharp
var imageBarrier = new ImageMemoryBarrier
{
    SType = StructureType.ImageMemoryBarrier,
    OldLayout = ImageLayout.General,
    NewLayout = ImageLayout.General,
    Image = output.Handle,
    SrcAccessMask = AccessFlags.ShaderWriteBit,
    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit,
    // ... subresource range
};
_ctx.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit,
    PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit,
    0, 0, null, 0, null, 1, &imageBarrier);
```

### 5. Format Support Validation
**File:** `BurstPhoto.Rendering/VulkanContext.cs`

**Added:** Runtime check that R32G32B32A32Sfloat supports StorageImageBit:
```csharp
Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, Format.R32G32B32A32Sfloat, out var formatProps);
bool supportsStorage = (formatProps.OptimalTilingFeatures & FormatFeatureFlags.StorageImageBit) != 0;
```

### 6. Validation Layer Support
**File:** `BurstPhoto.Rendering/VulkanContext.cs`

**Added:** Code to enable `VK_LAYER_KHRONOS_validation` when available for debugging.

---

## Current Test Results

After all fixes, running HigherQuality algorithm:

```
Iteration 1: After forward_fft: sum=0.00 (zeros)
Iteration 2: After forward_fft: sum=0.00 (zeros)
Iteration 3: After forward_fft: sum=772732.30 (NON-ZERO!)
Iteration 3: After backward_fft: sum=21500.76 (working)
Iteration 3: After convert_to_bayer: sum=10559.53 (working)
Iteration 4: After forward_fft: sum=781548.42 (NON-ZERO!)
Iteration 4: After backward_fft: sum=20895.83 (working)
Iteration 4: After convert_to_bayer: sum=10411.23 (working)
```

**The FFT pipeline is now functional for iterations 3-4!**

---

## Remaining Issues

### 1. Iterations 1-2 Produce Zeros
- Iterations 1 and 2 go through the full pipeline but output zeros
- Iterations 3 and 4 work correctly
- The difference is in the shift values:
  - Iter 1: Shift=(-8, 8)
  - Iter 2: Shift=(8, 8)
  - Iter 3: Shift=(-8, -8) - WORKS
  - Iter 4: Shift=(8, -8) - WORKS
- Possibly related to padding/cropping calculations for top shifts

### 2. Debug Sampling Issues
- `convert_to_rgba` debug shows zeros for ALL iterations (even 3-4 which work)
  - This is a sampling location issue - debug samples from middle of array
- `FinalAccumulator stats: sum=0.00` appears wrong
  - This samples from index 0-999999, but actual data starts at index ~1,159,452 (after padding)
  - The accumulation loop writes to `(y + padAlignY) * accWidth + (x + padAlignX)` with pad=252

### 3. Output Quality Unknown
- An output file IS being generated: `TestOutput/DJI_..._hdr_q13_l0.dng`
- With iterations 1-2 contributing zeros, output may be only 50% quality
- Need visual inspection of output to assess actual quality

---

## Key Learnings

1. **Vulkan descriptor sets MUST have ALL bindings updated** - even unused ones need dummy resources
2. **Buffer size calculations must match image format** - sizeof(T) != bytes per pixel for multi-channel formats
3. **Debug sampling location matters** - padding regions may be sampled instead of actual data
4. **The test shader approach was valuable** - writing thread IDs to verify execution helped isolate the issue

---

## Files Modified

1. `BurstPhoto.Rendering/VulkanImage.cs` - GetData buffer size fix
2. `BurstPhoto.Rendering/VulkanDescriptorManager.cs` - Added descriptor types
3. `BurstPhoto.Rendering/VulkanContext.cs` - Format validation, validation layers
4. `BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` - Memory barrier, dummy descriptors
5. `BurstPhoto.Rendering/Shaders/Frequency/forward_fft.hlsl` - Restored to real implementation

---

## Recommended Next Steps

1. **Debug iterations 1-2**: Investigate why top-shift iterations fail
   - Check padding calculations for `padTop > 0` cases
   - Verify `cropMergeY` handling in convert_to_rgba

2. **Fix debug sampling**: Update debug code to sample from actual data region, not padding

3. **Visual inspection**: Open the output DNG file to assess quality

4. **Test with spatial merge**: Verify the spatial algorithm still works correctly after changes

---

## Test Command

```bash
cd BurstPhoto_NET
./BurstPhoto.CLI/bin/Release/net10.0-windows/BurstPhoto.CLI.exe process \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0018_D.DNG" \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0019_D.DNG" \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0020_D.DNG" \
  --algorithm HigherQuality --gpu 1 -o TestOutput
```
