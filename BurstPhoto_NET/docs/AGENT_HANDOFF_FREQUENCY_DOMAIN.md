# Agent Handoff: Frequency Domain Pipeline Implementation

## Context

I'm working on a C# port of a burst photo HDR+ pipeline with frequency domain merging. The frequency domain (HigherQuality) algorithm is currently **non-functional** due to HLSL shader compilation issues, despite multiple attempts to fix it over the past week. The Fast (spatial) algorithm works perfectly.

## Current Status

### ✅ What Works
- **Fast Algorithm**: Fully functional spatial domain merging
- **GPU Detection**: Multi-GPU selection working (`--gpu` flag, `--list-gpus`)
- **Feature Detection**: Properly detects `ShaderStorageImageWriteWithoutFormat` support
- **Hardware**: NVIDIA RTX 3080 Laptop GPU **DOES support** the required Vulkan feature
- **Pipeline Infrastructure**: VulkanContext, VulkanComputePipeline, descriptor management all working

### ❌ What Doesn't Work
- **HigherQuality Algorithm**: Shader compilation fails with parsing errors
- **Root Cause**: HLSL-to-SPIR-V compilation fails at shader compile time
- **Error Example**:
  ```
  CSMain:584: error: 'declaration' : Expected
  CSMain: CSMain(584): error at column 6, HLSL parsing failed.
  ```

### 🔍 What We've Tried
1. **Feature enablement**: Enabled `ShaderStorageImageWriteWithoutFormat` in VulkanContext ✅
2. **Format attributes**: Added then removed `[[vk::image_format("rgba32f")]]` (caused compilation errors)
3. **Bounds checking**: Added comprehensive bounds checks to FFT shaders ✅
4. **Debug output**: Added extensive logging and diagnostics ✅
5. **GPU selection**: Tested on both AMD integrated and NVIDIA discrete GPUs
6. **Multiple debugging sessions**: Analyzed descriptor bindings, layouts, dispatch parameters

### 📁 Key Files

**Shader Code:**
- `BurstPhoto_NET/BurstPhoto.Rendering/Shaders/MergeFrequency.hlsl` - 547 lines, contains all frequency domain kernels
- `BurstPhoto_NET/BurstPhoto.Rendering/Shaders/Constants.hlsli` - Shared constants

**Pipeline Code:**
- `BurstPhoto_NET/BurstPhoto.Rendering/VulkanContext.cs` - Vulkan initialization, GPU selection
- `BurstPhoto_NET/BurstPhoto.Rendering/Implementations/VulkanComputePipeline.cs` - Main pipeline, shader compilation (see `EnsureMergeFrequencyPipeline()` around line 1128)
- `BurstPhoto_NET/BurstPhoto.Rendering/VulkanShaderCompiler.cs` - Wraps Shaderc for HLSL→SPIR-V compilation

**Documentation:**
- `BurstPhoto_NET/docs/FREQUENCY_DOMAIN_REPORT.md` - Original analysis of the porting effort
- `BurstPhoto_NET/docs/GPU_COMPATIBILITY_REPORT.md` - Test results on both GPUs
- `BurstPhoto_NET/docs/BACKLOG.md` - Project status and known issues
- `BurstPhoto_NET/docs/FFT_DEBUG_ENHANCEMENTS.md` - Recent debugging improvements

**Reference:**
- Original Metal implementation available in Swift codebase (if needed for comparison)

## The Problem

The current shader compilation approach in `VulkanComputePipeline.cs` (lines ~1186-1220):

```csharp
// Read entire MergeFrequency.hlsl file
string source = File.ReadAllText(shaderPath);

// Replace #include with file contents
source = source.Replace("#include \"Constants.hlsli\"", constants);

// For EACH kernel, do string replacement to rename entry point
string srcAbs = source.Replace("void calculate_abs_diff_rgba(", "void CSMain(");
_kernelAbsDiff = new ComputeKernel(_ctx, _frequencyLayout, _compiler.Compile(srcAbs, "CSMain"), "CSMain", 16, 16, 1);

// Repeat for ~10 different kernels...
```

**Issues with this approach:**
- String replacement is fragile and error-prone
- Creates shaders with multiple functions, some renamed, some not
- After preprocessing, line numbers in errors don't match source
- Hard to debug compilation failures
- Shaderc/DXC parser seems to choke on the resulting code

## Three Proposed Solutions

### Option 1: Fix Shader Compilation Pipeline ⭐ RECOMMENDED
**Effort:** 2-4 hours
**Risk:** Low
**Preserves existing work:** Yes

**Approach:**
1. **Split `MergeFrequency.hlsl` into separate files** (one per kernel)
   - `forward_fft.hlsl`
   - `backward_fft.hlsl`
   - `calculate_abs_diff_rgba.hlsl`
   - etc. (~10 files total)
2. **Keep `forward_fft_impl` as a shared helper** in a common include file
3. **Remove string replacement** - compile each file directly with its natural entry point
4. **Update `VulkanComputePipeline.cs`** to load and compile each shader file independently
5. **Add proper include path handling** to the compiler

**Benefits:**
- Clean, maintainable shader code
- Proper error messages with correct line numbers
- Standard HLSL compilation workflow
- Easy to debug individual shaders
- Minimal changes to C# pipeline code

**Implementation checklist:**
- [ ] Create `Shaders/Frequency/` subdirectory
- [ ] Split MergeFrequency.hlsl into individual kernel files
- [ ] Create `FrequencyCommon.hlsli` with shared helper functions
- [ ] Update `VulkanShaderCompiler` to support include directories
- [ ] Update `EnsureMergeFrequencyPipeline()` to compile each shader separately
- [ ] Test compilation of each shader individually
- [ ] Test full pipeline execution

---

### Option 2: Port to GLSL
**Effort:** 1-2 days
**Risk:** Medium
**Preserves existing work:** Partial (need to rewrite shaders)

**Approach:**
1. Port all HLSL shaders to GLSL 4.50+ (Vulkan's native shading language)
2. Use proper GLSL syntax (`layout(set=0, binding=N)` instead of `[[vk::binding]]`)
3. Update `VulkanShaderCompiler` to use GLSL compilation path

**Benefits:**
- Native Vulkan support (better compatibility)
- Better error messages
- More examples and documentation available
- No `[[vk::binding]]` attribute confusion

**Challenges:**
- Need to rewrite all shaders (~500+ lines)
- Different syntax for some operations (texture sampling, etc.)
- Learning curve if unfamiliar with GLSL

**Implementation checklist:**
- [ ] Convert Constants.hlsli to GLSL header
- [ ] Port forward_fft and backward_fft to GLSL
- [ ] Port helper kernels (abs_diff, rms, mismatch, etc.)
- [ ] Update VulkanShaderCompiler for GLSL
- [ ] Update descriptor bindings to GLSL layout syntax
- [ ] Test each kernel individually
- [ ] Verify numerical correctness against Metal implementation

---

### Option 3: Simplified Frequency Domain Algorithm
**Effort:** 3-5 days
**Risk:** High (may not match quality)
**Preserves existing work:** No

**Approach:**
Replace full FFT implementation with a simpler frequency-domain inspired approach:
1. Use Discrete Cosine Transform (DCT) instead of FFT (simpler, no complex numbers)
2. Or use spatial domain with frequency-weighted merging
3. Keep the multi-iteration refinement concept

**Benefits:**
- Simpler shader code
- Potentially better performance
- Avoid FFT complexity entirely
- Better hardware compatibility

**Challenges:**
- Won't match Metal implementation exactly
- Quality may differ from reference
- Need to validate results empirically
- More research/experimentation required

**Implementation checklist:**
- [ ] Research DCT-based image merging approaches
- [ ] Prototype simplified algorithm in standalone shader
- [ ] Integrate into pipeline
- [ ] Compare quality against spatial algorithm and Metal reference
- [ ] Tune parameters for acceptable quality

---

## Recommendation

**Start with Option 1** because:
1. ✅ Fastest path to a working solution
2. ✅ Preserves all the porting work done so far
3. ✅ Addresses root cause (fragile string replacement)
4. ✅ Standard software engineering practice (separate concerns)
5. ✅ Low risk - if it fails, Options 2 and 3 are still available

**If Option 1 fails** (shaders still don't compile after splitting):
→ Move to **Option 2** (GLSL port) as the next best alternative

**Only consider Option 3** if both 1 and 2 fail, or if you want to explore algorithmic alternatives.

## What I Need You To Do

Please implement **Option 1** (Fix Shader Compilation Pipeline). Specifically:

1. **Review the current shader compilation approach** in `VulkanComputePipeline.cs::EnsureMergeFrequencyPipeline()`
2. **Split `MergeFrequency.hlsl`** into separate shader files (one per compute kernel entry point)
3. **Update the compilation code** to load and compile each shader file independently (no string replacement)
4. **Add include directory support** to `VulkanShaderCompiler` if needed
5. **Test each shader compilation** individually to verify it works
6. **Run the full pipeline** with `--algorithm HigherQuality` and verify it executes without errors

## Success Criteria

✅ All frequency domain shaders compile successfully
✅ No HLSL parsing errors
✅ Pipeline initializes without exceptions
✅ ExecuteForwardFft runs and produces non-zero output
✅ Debug output shows successful shader execution

## Failure Criteria / Fallback

❌ If shaders still fail to compile after splitting:
- Save the failing shader source using the existing debug mechanism
- Report which specific shader(s) are failing and why
- Recommend moving to Option 2 (GLSL port)

## Testing Command

```bash
# Test HigherQuality algorithm with debug output
burstphoto process --algorithm HigherQuality --gpu 1 --debug-dump \
  "Burst Samples/Static Exposure/DJI_20251218173647_0202_D.DNG" \
  "Burst Samples/Static Exposure/DJI_20251218173647_0203_D.DNG" \
  "Burst Samples/Static Exposure/DJI_20251218173647_0204_D.DNG" \
  -o TestOutput/
```

## Additional Notes

- The system has comprehensive debug output already in place
- The `--debug-dump` flag saves intermediate DNG files for inspection
- Failed shaders are automatically saved to `FailedShader_*.hlsl` files
- GPU selection works: use `--list-gpus` to see available devices
- The NVIDIA RTX 3080 (GPU 1) supports all required features

## Questions to Ask Me

If you need clarification on:
- Metal shader reference implementation details
- Expected FFT output format/values
- Descriptor binding layout specifics
- Testing methodology or validation criteria

Good luck! I'm confident Option 1 will work and get us past this blocker.
