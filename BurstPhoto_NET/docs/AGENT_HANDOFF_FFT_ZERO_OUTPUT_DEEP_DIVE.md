# Agent Handoff: FFT Zero Output - Deep Dive Investigation

**Date:** 2026-01-21
**Status:** Critical bug identified - shader writes not persisting to output texture
**Priority:** HIGH - Blocking HigherQuality algorithm

---

## Executive Summary

After extensive debugging with print statements and trace logging, we've discovered that the Forward FFT shader in the HigherQuality pipeline produces all zeros **NOT because of algorithmic bugs, but because shader writes are not persisting to the output texture**. Even hardcoded constant writes like `OutputTexture[int2(0, 0)] = float4(999, 888, 777, 666)` produce zero output when read back.

This suggests a **Vulkan descriptor binding, synchronization, or texture format issue** rather than a shader code bug.

---

## What We've Tried (Chronological)

### 1. Initial Investigation - Shader Compilation ✅
- **Action:** Verified all shaders compile without errors
- **Result:** SUCCESS - All 10 frequency domain shaders compile cleanly
- **File:** `forward_fft.hlsl`, `FrequencyCommon.hlsli`
- **Conclusion:** Compilation is not the problem

### 2. Bounds Check Investigation ❌
- **Action:** Disabled bounds checks to see if threads were early-returning
- **Result:** NO CHANGE - Still produced zeros
- **Code Modified:** Lines 24-32 in `forward_fft.hlsl` (commented out bounds checks)
- **Conclusion:** Bounds checks are not the problem

### 3. Float4 Initialization Fix ❌
- **Action:** Changed `Re0 = Im0 = 0.0f` to `Re0 = Im0 = zeros`
- **Rationale:** Thought scalar-to-vector assignment might not broadcast correctly
- **Result:** NO CHANGE - Still produced zeros
- **Files Modified:** `FrequencyCommon.hlsli` lines 88, 113
- **Conclusion:** Initialization was fine (HLSL broadcasts scalars to vectors automatically)

### 4. Passthrough Test - Simple Texture Copy ❌
- **Action:** Bypassed FFT logic entirely, just copied input pixel to output
- **Code:** `float4 pixel = RefTexture.Load(...); OutputTexture[...] = pixel;`
- **Result:** ZERO OUTPUT
- **Conclusion:** Even simple texture reads/writes don't work

### 5. Hardcoded Constant Write Test ❌ **CRITICAL**
- **Action:** Wrote hardcoded constant to prove shader executes
- **Code:** `OutputTexture[int2(0, 0)] = float4(999.0, 888.0, 777.0, 666.0);`
- **Result:** ZERO OUTPUT when read back via `GetData<float>()`
- **Conclusion:** **Shader writes are NOT persisting to the output texture**

### 6. Debug Print Investigation ✅
- **Action:** Added extensive Console.WriteLine debugging in C# code
- **Discoveries:**
  - `ExecuteForwardFft` IS being called (4 times per run)
  - Reference RGBA conversion works: mean=387.3090 ✅
  - Reference FFT receives valid input ✅
  - Reference FFT produces zero output ❌
  - Comparison image warp produces zeros (separate bug)
  - Comparison image FFT receives zero input (cascading failure)

---

## Key Findings

### Finding 1: Two Separate Code Paths
There are TWO ways the Forward FFT shader is invoked:

1. **`ExecuteForwardFft()`** (line 432 in VulkanComputePipeline.cs)
   - Used for: Reference image FFT
   - Input: `rgbaRefTexture` (valid data, mean=387.3090)
   - Output: All zeros
   - Descriptor binding: Binding 1 = input, Binding 10 = output

2. **`DispatchTile()`** (line 1424 in ExecuteMergeFrequency)
   - Used for: Comparison image FFT
   - Input: `aligned` texture (all zeros due to upstream warp bug)
   - Output: All zeros
   - Descriptor binding: Same layout (Binding 1 = input, Binding 10 = output)

### Finding 2: Data Flow Trace (Iteration 1)

```
[✅] Raw DNG load          : sum=133507466, mean=13350.75
[✅] After prepare          : sum=22868327, mean=2286.83
[✅] After RGBA conversion  : sum=3873090, mean=387.31
[❌] After forward FFT      : sum=0, mean=0.0000  ← BREAKS HERE
[❌] After backward FFT     : sum=0, mean=0.0000
[❌] After merge            : sum=0, mean=0.0000
[❌] Final output           : Black image
```

### Finding 3: Comparison Image Pipeline (All Zeros)

```
[❌] warpedAlt BEFORE convert      : sum=0, mean=0.0000  ← Warp bug
[❌] alignedTextureRgba AFTER convert : sum=0, mean=0.0000
[❌] aligned input to FFT          : sum=0, mean=0.0000
[❌] FFT output                    : sum=0, mean=0.0000
```

This is a **cascading failure** from the warp operation producing zeros.

---

## Current Shader State

### forward_fft.hlsl (Current Debug Version)
```hlsl
[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint inputWidth, inputHeight;
    RefTexture.GetDimensions(inputWidth, inputHeight);

    uint outputWidth, outputHeight;
    OutputTexture.GetDimensions(outputWidth, outputHeight);

    int nTilesX = inputWidth / TileSize;
    int nTilesY = inputHeight / TileSize;

    // CRITICAL TEST: Write hardcoded value to prove shader executes
    OutputTexture[int2(0, 0)] = float4(999.0, 888.0, 777.0, 666.0);

    // CRITICAL TEST: Just copy first pixel to verify read/write works
    int m0 = DTid.x * TileSize;
    int n0 = DTid.y * TileSize;
    float4 testPixel = RefTexture.Load(int3(m0, n0, 0));
    OutputTexture[int2(2*m0, n0)] = testPixel;
    OutputTexture[int2(2*m0+1, n0)] = testPixel * 0.5;
    return; // Exit before calling FFT

    forward_fft_impl(TileSize, DTid, OutputTexture, RefTexture);
}
```

**Result:** All writes produce zeros when read back.

---

## Vulkan Configuration Analysis

### ExecuteForwardFft Descriptor Setup (lines 1585-1593)
```csharp
var set = _descriptors.Allocate(_frequencyLayout);
_descriptors.UpdateBuffer(set, 0, pb.Handle, (ulong)Marshal.SizeOf<FrequencyParams>(), DescriptorType.UniformBuffer);
_descriptors.UpdateImage(set, 1, input.View, ImageLayout.General, DescriptorType.SampledImage);  // RefTexture
_descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage); // OutputTexture
```

### Shader Descriptor Declarations (FrequencyCommon.hlsli)
```hlsl
[[vk::binding(0, 0)]]
cbuffer FrequencyParams : register(b0) { ... }

[[vk::binding(1, 0)]]
Texture2D<float4> RefTexture : register(t1);

[[vk::binding(10, 0)]]
RWTexture2D<float4> OutputTexture : register(u10);
```

**Analysis:** Bindings appear correct. Binding 1 → RefTexture (t1), Binding 10 → OutputTexture (u10).

### Image Layout Transitions (lines 1581-1582)
```csharp
input.TransitionLayout(ImageLayout.General, cmd);
output.TransitionLayout(ImageLayout.General, cmd);
```

**Analysis:** Both images transitioned to General layout before dispatch. Should be correct.

### Dispatch Configuration (lines 1601-1605)
```csharp
uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);  // 258/16 = 17
uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);  // 194/16 = 13
// Dispatches 17×13 = 221 workgroups
// Each workgroup has 16×16 = 256 threads
// Total: 272×208 = 56,576 threads
// Active (within bounds): 258×194 = 50,052 threads
```

**Analysis:** Dispatch math looks correct.

---

## Hypotheses for Root Cause

### Hypothesis 1: Image Format Mismatch ⚠️
**Likelihood:** MEDIUM
**Description:** The output texture might be created with the wrong format or usage flags.

**Evidence:**
- Input format: `R32G32B32A32Sfloat` ✅
- Output format: `R32G32B32A32Sfloat` ✅
- Output usage flags: `StorageBit | SampledBit | TransferSrcBit`

**Test:** Verify format is supported for storage writes on this GPU.

**Action Required:**
```csharp
// Check if R32G32B32A32Sfloat supports STORAGE_IMAGE usage
var formatProps = _ctx.Vk.GetPhysicalDeviceFormatProperties(_ctx.PhysicalDevice, Format.R32G32B32A32Sfloat);
Console.WriteLine($"Optimal features: {formatProps.OptimalTilingFeatures}");
Console.WriteLine($"Supports storage? {(formatProps.OptimalTilingFeatures & FormatFeatureFlags.StorageImageBit) != 0}");
```

### Hypothesis 2: Synchronization/Memory Barrier Missing ⚠️
**Likelihood:** HIGH
**Description:** The GPU writes might not be visible to CPU reads because of missing memory barriers.

**Evidence:**
- `EndSingleTimeCommands()` should wait for GPU completion
- But maybe pipeline barriers are needed between dispatch and readback?

**Test:** Add explicit pipeline barrier before `GetData()`

**Action Required:**
```csharp
// After dispatch, before EndSingleTimeCommands:
var barrier = new ImageMemoryBarrier
{
    SType = StructureType.ImageMemoryBarrier,
    OldLayout = ImageLayout.General,
    NewLayout = ImageLayout.General,
    SrcAccessMask = AccessFlags.ShaderWriteBit,
    DstAccessMask = AccessFlags.TransferReadBit,
    Image = output.Handle,
    SubresourceRange = new ImageSubresourceRange
    {
        AspectMask = ImageAspectFlags.ColorBit,
        LevelCount = 1,
        LayerCount = 1
    }
};
_ctx.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit,
    PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);
```

### Hypothesis 3: Descriptor Pool Corruption 🔴
**Likelihood:** MEDIUM
**Description:** The descriptor set allocation might be reusing a stale descriptor.

**Evidence:**
- Multiple FFT calls happen in sequence
- Descriptors are allocated from a pool
- Maybe pool isn't being reset between calls?

**Test:** Force descriptor pool recreation or add validation

**Action Required:**
```csharp
// Before allocating descriptor set:
_descriptors.ResetPool(); // If this method exists
```

### Hypothesis 4: Shader Compilation Issue (SPIR-V) 🟡
**Likelihood:** LOW
**Description:** DXC might be generating incorrect SPIR-V for the write operations.

**Evidence:**
- Shader compiles without errors
- But SPIR-V binary might be malformed

**Test:** Inspect SPIR-V with spirv-dis or spirv-val

**Action Required:**
```bash
# Save SPIR-V to file and validate
spirv-val forward_fft.spv
spirv-dis forward_fft.spv > forward_fft.spvasm
# Look for OpImageWrite instructions
```

### Hypothesis 5: Feature Not Enabled (ShaderStorageImageWriteWithoutFormat) 🟡
**Likelihood:** LOW (already checked)
**Description:** The required Vulkan feature isn't enabled.

**Evidence:**
- Code checks for `SupportsStorageImageWriteWithoutFormat` ✅
- Feature is reported as supported on RTX 3080 ✅

**Test:** Double-check feature is actually enabled at device creation

**Action Required:**
```csharp
// In VulkanContext device creation:
var features = _ctx.Device.EnabledFeatures;
Console.WriteLine($"StorageImageWriteWithoutFormat enabled: {features.ShaderStorageImageWriteWithoutFormat}");
```

### Hypothesis 6: Wrong Texture Being Read Back ⚠️
**Likelihood:** MEDIUM
**Description:** `GetData()` might be reading from a different texture than what the shader wrote to.

**Evidence:**
- The `output` parameter passed to `ExecuteForwardFft` is `refFT`
- But maybe `refFT` isn't the same object that gets read at line 436?

**Test:** Add pointer/handle comparison

**Action Required:**
```csharp
// In ExecuteForwardFft, after dispatch:
Console.WriteLine($"Output texture handle: {output.Handle.Handle}");

// At line 436, before GetData:
Console.WriteLine($"refFT texture handle: {refFT.Handle.Handle}");
// These should match!
```

---

## Comparison: Working vs Broken Code Paths

### DispatchTile (Used in ExecuteMergeFrequency) - Comparison Images
```csharp
var cmd2 = _ctx.BeginSingleTimeCommands();
var set = _descriptors.Allocate(_frequencyLayout);
_descriptors.UpdateBuffer(set, 0, paramBuffer.Handle, ...);
if(t0!=null) _descriptors.UpdateImage(set, 1, t0.View, ImageLayout.General, DescriptorType.SampledImage);
if(u0!=null) _descriptors.UpdateImage(set, 10, u0.View, ImageLayout.General, DescriptorType.StorageImage);

kernel.BindPipeline(cmd2);
_ctx.Vk.CmdBindDescriptorSets(cmd2, PipelineBindPoint.Compute, kernel.PipelineLayout, 0, 1, &set, 0, null);

uint groupsX = (uint)Math.Ceiling((double)width / tile_size_merge / 16.0);
uint groupsY = (uint)Math.Ceiling((double)height / tile_size_merge / 16.0);
kernel.Dispatch(cmd2, groupsX, groupsY, 1);

_ctx.EndSingleTimeCommands(cmd2);
```

**Key Difference:** Uses `paramBuffer` (a member variable), not a local `pb` variable!

### ExecuteForwardFft - Reference Image
```csharp
var cmd = _ctx.BeginSingleTimeCommands();
var set = _descriptors.Allocate(_frequencyLayout);
_descriptors.UpdateBuffer(set, 0, pb.Handle, ...);  // ← Local 'pb' variable
_descriptors.UpdateImage(set, 1, input.View, ImageLayout.General, DescriptorType.SampledImage);
_descriptors.UpdateImage(set, 10, output.View, ImageLayout.General, DescriptorType.StorageImage);

_kernelForwardFft!.BindPipeline(cmd);
_ctx.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _kernelForwardFft.PipelineLayout, 0, 1, &set, 0, null);

uint groupsX = (uint)Math.Ceiling((double)nTilesX / 16.0);
uint groupsY = (uint)Math.Ceiling((double)nTilesY / 16.0);
_kernelForwardFft.Dispatch(cmd, groupsX, groupsY, 1);

_ctx.EndSingleTimeCommands(cmd);
```

**Potential Issue:** `pb` is created with `using var` which disposes it immediately after the method returns. But the command buffer hasn't executed yet when `pb` gets disposed!

---

## 🔴 CRITICAL DISCOVERY: Use-After-Free Bug? 🔴

Looking at line 1574 in `ExecuteForwardFft`:
```csharp
using var pb = new VulkanBuffer(_ctx, (ulong)Marshal.SizeOf<FrequencyParams>(), ...);
pb.SetData(new[] { freqParams });
// ...
_descriptors.UpdateBuffer(set, 0, pb.Handle, ...);
// ...
_ctx.EndSingleTimeCommands(cmd); // GPU executes here
} // pb.Dispose() called here ← BUG?
```

The `using` statement disposes `pb` when the method exits. But **`EndSingleTimeCommands()` submits the command buffer to the GPU and waits for completion**. So the buffer should still be alive during GPU execution.

**However:** In `DispatchTile` (line 1353), it uses `paramBuffer` which is a **member variable** created earlier in `ExecuteMergeFrequency` at line 1300, so it stays alive longer.

**Test:** Change `using var pb` to a regular variable and dispose it manually AFTER reading back the result.

---

## Debug Commands That Worked

### Check Input Data
```bash
cd BurstPhoto_NET && ./BurstPhoto.CLI/bin/Release/net10.0-windows/BurstPhoto.CLI.exe \
  process \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0018_D.DNG" \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0019_D.DNG" \
  "Burst Samples/Bracketed Exposure/Input/DJI_20250925172104_0020_D.DNG" \
  --algorithm HigherQuality --gpu 1 -o TestOutput 2>&1 | grep "DEBUG\|WARNING\|FFT DEBUG\|WARP DEBUG"
```

### Check Shader Compilation
```bash
grep -r "forward_fft compiled" BurstPhoto_NET/BurstPhoto.CLI/bin/Release/net10.0-windows/
```

---

## Files Modified During Investigation

1. **forward_fft.hlsl** - Added test code to write hardcoded values
2. **FrequencyCommon.hlsli** - Changed `0.0f` to `zeros` for float4 initialization
3. **VulkanComputePipeline.cs** - Added extensive debug logging around FFT calls

---

## Recommended Next Steps

### Priority 1: Fix Potential Use-After-Free
1. Change `using var pb` to regular variable in `ExecuteForwardFft`
2. Dispose `pb` manually AFTER `GetData()` call at line 436
3. Test if this fixes the zero output

### Priority 2: Add Memory Barrier
1. Insert pipeline barrier between dispatch and `EndSingleTimeCommands`
2. Ensure shader writes are visible to transfer operations
3. Test if this fixes the zero output

### Priority 3: Enable Vulkan Validation Layers
1. Check if validation layers can be enabled
2. Look for Vulkan errors/warnings during execution
3. Common errors: descriptor mismatches, synchronization issues, format unsupported

### Priority 4: Compare with Working Code
1. Find a working compute shader in the codebase (e.g., spatial merge)
2. Compare descriptor setup, dispatch, and readback code
3. Identify any differences in how it handles buffers/images

### Priority 5: Verify Texture Handle Consistency
1. Log texture handles before dispatch and before readback
2. Ensure they're the same object
3. Rule out accidental texture swapping

---

## Questions for Next Agent

1. **Is `using var` disposing the buffer too early?** The GPU might be accessing freed memory.
2. **Are pipeline barriers needed?** Vulkan memory model requires explicit synchronization.
3. **Is the format supported for storage writes?** Check format properties on this GPU.
4. **Are validation layers enabled?** They would catch most Vulkan errors.
5. **Why does `DispatchTile` use a member variable for paramBuffer while `ExecuteForwardFft` uses a local?** This difference might be significant.

---

## Success Criteria

✅ Forward FFT produces non-zero output when given valid input
✅ Hardcoded test write `float4(999, 888, 777, 666)` appears in output
✅ Simple passthrough (copy input to output) works
✅ Full FFT algorithm produces expected frequency domain data

---

## Hardware/Environment

- **GPU:** NVIDIA GeForce RTX 3080 Laptop GPU
- **Driver:** (Check with `nvidia-smi`)
- **Vulkan Version:** (Check with `vulkaninfo`)
- **OS:** Windows
- **Platform:** .NET 10.0

---

Good luck! The shader code itself is likely fine - this looks like a Vulkan resource management or synchronization issue. The `using var pb` disposal timing is my top suspect. 🔍
