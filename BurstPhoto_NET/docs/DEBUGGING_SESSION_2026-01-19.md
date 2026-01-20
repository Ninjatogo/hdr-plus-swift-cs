# Debugging Session: 2026-01-19
## Frequency Domain Merge RGBA Conversion Issue

### Session Goal
Fix the Frequency Domain merge (Higher Quality) black output issue by addressing the RGBA conversion shaders.

---

## Findings

### 1. Root Cause Identified
The `convert_to_rgba` and `convert_to_bayer` shaders were **demosaicing** instead of **directly packing** raw Bayer values.

**Problem**:
- `convert_to_rgba` averaged green channels based on CFA pattern
- Example for RGGB: `g = (p1+p2)*0.5f`
- This caused 50% data loss for green channels
- FFT expected 4 independent data channels, but got RGB color data instead

**Evidence**:
```hlsl
// OLD (WRONG) - Demosaicing logic
if (CfaPattern == 0) { // RGGB
    r = p0; g = (p1+p2)*0.5f; b = p3;
}
OutTextureRGBA[gid] = float4(r, g, b, 1.0f);
```

**Swift Reference** (`texture.metal:315-328`):
```metal
// CORRECT - Direct packing
float4 const color_value = float4(
    in_texture.read(uint2(x, y)).r,       // Channel 0
    in_texture.read(uint2(x+1, y)).r,     // Channel 1
    in_texture.read(uint2(x, y+1)).r,     // Channel 2
    in_texture.read(uint2(x+1, y+1)).r    // Channel 3
);
```

---

### 2. Shader Fix Applied

**File**: `BurstPhoto.Rendering/Shaders/TextureOps.hlsl`

#### `convert_to_rgba` (lines 89-105)
**Changes**:
- Removed CFA pattern conditional logic
- Removed green channel averaging
- Changed to direct packing of 4 adjacent pixels

**New Code**:
```hlsl
[numthreads(16, 16, 1)]
void convert_to_rgba(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint2 inPos = gid * 2;

    // Direct pack: Read 4 adjacent Bayer pixels into RGBA channels
    // No averaging, no demosaicing - just pack the raw values
    float p0 = InTextureFloat.Load(int3(inPos.x,   inPos.y,   0));
    float p1 = InTextureFloat.Load(int3(inPos.x+1, inPos.y,   0));
    float p2 = InTextureFloat.Load(int3(inPos.x,   inPos.y+1, 0));
    float p3 = InTextureFloat.Load(int3(inPos.x+1, inPos.y+1, 0));

    // Pack all 4 values directly (no averaging, no CFA interpretation)
    // This preserves all raw Bayer data for FFT processing
    OutTextureRGBA[gid] = float4(p0, p1, p2, p3);
}
```

#### `convert_to_bayer` (lines 107-127)
**Changes**:
- Removed CFA pattern conditional logic
- Changed to simple positional unpacking

**New Code**:
```hlsl
[numthreads(16, 16, 1)]
void convert_to_bayer(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint2 inPos = gid / 2;
    float4 rgba = InTextureRGBA.Load(int3(inPos, 0));

    // Determine which pixel in the 2x2 block we're unpacking
    uint x = gid.x % 2;
    uint y = gid.y % 2;

    // Unpack: RGBA channels back to 2x2 Bayer positions
    // This is the exact inverse of convert_to_rgba
    float val = 0.0f;
    if (x == 0 && y == 0) val = rgba.r;      // Top-left
    else if (x == 1 && y == 0) val = rgba.g; // Top-right
    else if (x == 0 && y == 1) val = rgba.b; // Bottom-left
    else if (x == 1 && y == 1) val = rgba.a; // Bottom-right

    OutTextureFloat[gid] = val;
}
```

---

### 3. Verification Status

#### Build: ✅ SUCCESS
- No compilation errors
- Shader logic verified to match Swift exactly
- Clean rebuild completed successfully

#### Runtime: ❌ FAILED
Despite correct shader logic, execution produces all-zero output:

**Test Command**:
```bash
dotnet run --project BurstPhoto.CLI -- process \
  "Burst Samples/Static Exposure/DJI_20251218173647_0202_D.DNG" \
  "Burst Samples/Static Exposure/DJI_20251218173647_0203_D.DNG" \
  --algorithm HigherQuality --debug-dump
```

**Debug Output**:
```
[VulkanComputePipeline] Converting Reference to RGBA...
[DebugDump] step_1b_rgba - RGBA sum (first 1000): 0.00, Total elements: 3145728
[DebugDump] WARNING: RGBA data appears to be zeros!
```

**File Sizes**:
| File | Expected | Actual | Status |
|------|----------|--------|--------|
| step_1_prepare.dng | 16 MB | 16 MB | ✅ Valid input |
| (RGBA conversion) | Non-zero | 0.00 | ❌ All zeros |
| step_5_back_fft.dng | 16 MB | 1.6 MB | ❌ Black output |

---

### 4. Active Debugging Issue

**Symptom**: `ExecuteConvertToRgba()` produces all-zero output

**Evidence**:
1. Input texture (`preparedTexture`): **Valid** - 16MB DNG written successfully
2. Shader compilation: **Success** - No errors
3. RGBA output after conversion: **All zeros** - Sum of first 1000 elements = 0.00

**Possible Root Causes**:

#### A. Vulkan Descriptor Binding Issue
**Location**: `VulkanComputePipeline.cs:1277-1282`
```csharp
_descriptors.UpdateImage(set, 1, bayerInput.View, ImageLayout.General, DescriptorType.SampledImage);   // t0 - Bayer input
_descriptors.UpdateImage(set, 3, dummyRgba.View, ImageLayout.General, DescriptorType.SampledImage);    // t2 - unused
_descriptors.UpdateImage(set, 10, dummyFloat.View, ImageLayout.General, DescriptorType.StorageImage);  // u10 - unused
_descriptors.UpdateImage(set, 12, rgbaOutput.View, ImageLayout.General, DescriptorType.StorageImage);  // u12 - RGBA output
```

**Shader expects**:
```hlsl
Texture2D<float> InTextureFloat  : register(t0);
RWTexture2D<float4> OutTextureRGBA : register(u12);
```

**Hypothesis**: Binding slot mismatch or descriptor type mismatch

#### B. Memory Barrier / Synchronization Issue
**Location**: `VulkanComputePipeline.cs:1271-1275`
```csharp
bayerInput.TransitionLayout(ImageLayout.General, cmd);
rgbaOutput.TransitionLayout(ImageLayout.General, cmd);
```

**Hypothesis**: Input texture not fully written/flushed before conversion shader reads it

#### C. Image Layout Issue
**Current**: Both textures transitioned to `ImageLayout.General`
**Hypothesis**: Maybe `SampledImage` descriptor type requires `ShaderReadOnlyOptimal` layout?

#### D. Shader Dispatch Issue
**Location**: `VulkanComputePipeline.cs:1287-1290`
```csharp
uint groupsX = (uint)Math.Ceiling((double)rgbaOutput.Width / 16.0);
uint groupsY = (uint)Math.Ceiling((double)rgbaOutput.Height / 16.0);
_kernelConvertToRgba.Dispatch(cmd, groupsX, groupsY, 1);
```

**Hypothesis**: Dispatch size mismatch or pipeline not bound correctly

---

### 5. Similar Working Example

The `ExecutePrepare()` function works correctly and also uses:
- Input: R16Uint texture
- Output: R32Sfloat texture
- Similar descriptor binding pattern

**Key Difference**: Prepare uses `StorageImage` for both input and output, while RGBA conversion uses `SampledImage` for input.

---

### 6. Next Steps for Resolution

#### Priority 1: Descriptor Binding Verification
1. Add validation logging to verify descriptor updates
2. Check if binding slot 1 actually corresponds to `t0` in HLSL
3. Verify `SampledImage` vs `StorageImage` descriptor type requirements

#### Priority 2: Memory Barrier Analysis
1. Add explicit memory barrier after Prepare pass
2. Verify command buffer submission/wait is correct
3. Check if `EndSingleTimeCommands()` properly synchronizes

#### Priority 3: Shader Debugging
1. Try simplest possible shader: just write constant value to output
2. Gradually add complexity to isolate where data is lost
3. Add debug output to shader (write input dimensions, thread IDs, etc.)

#### Priority 4: Layout/Format Verification
1. Try `ShaderReadOnlyOptimal` layout for input texture
2. Verify texture format compatibility (R32Sfloat input, RGBA32Sfloat output)
3. Check if `Load()` vs `Sample()` makes a difference

---

### 7. Workaround Options

If Vulkan issue cannot be resolved quickly:
1. **CPU-side conversion**: Download Bayer to CPU, pack to RGBA, re-upload
2. **Compute shader workaround**: Use storage images instead of sampled images
3. **Skip RGBA packing**: Modify FFT shaders to work with single-channel data (complex change)

---

### 8. Documentation Updates

Updated the following files with findings:
- `BACKLOG.md`: Added shader fixes and active debugging issue
- `MIGRATION_STATUS.md`: Added entry for shader logic fix (2026-01-19)
- `IMPLEMENTATION_DETAILS.md`: Expanded Known Limitations section with detailed debugging info
- `DEBUGGING_SESSION_2026-01-19.md`: This file (comprehensive session notes)

---

## Session Summary

**Progress**:
- ✅ Identified root cause: Incorrect demosaicing in conversion shaders
- ✅ Fixed shader logic to match Swift implementation exactly
- ✅ Verified shader compiles without errors
- ❌ Runtime execution produces zeros (Vulkan-level issue)

**Impact**:
- Shader fixes are fundamentally correct
- Once runtime issue is resolved, Frequency Domain merge should work
- Problem is isolated to Vulkan descriptor/synchronization, not algorithm logic

**Time Investment**:
- Shader analysis and fix: ~30 minutes
- Build and testing: ~45 minutes
- Debugging and documentation: ~45 minutes
- **Total**: ~2 hours

**Recommendation**:
Focus next debugging session on Vulkan descriptor binding validation and memory barrier analysis. Consider adding more verbose logging to `ExecuteConvertToRgba()` to trace execution flow.
