# FFT Debug Enhancements

This document describes the comprehensive debugging improvements added to diagnose the Frequency Domain FFT zero-output issue.

## Changes Made

### 1. Enhanced GPU Feature Detection (VulkanContext.cs)

**Added:**
- `SupportsStorageImageWriteWithoutFormat` public property
- Clear warning messages when feature is not supported
- Graceful degradation (Fast algorithm still works, HigherQuality fails with clear error)

**Output when feature NOT supported:**
```
╔════════════════════════════════════════════════════════════════
║ ⚠️  FEATURE NOT SUPPORTED
╠════════════════════════════════════════════════════════════════
║ ShaderStorageImageWriteWithoutFormat is NOT supported!
║
║ Impact:
║   ✅ 'Fast' (Spatial) algorithm: WILL WORK
║   ❌ 'HigherQuality' (Frequency) algorithm: WILL NOT WORK
║
║ Recommended action:
║   Use --algorithm Fast for processing on this device
║
║ Advanced options:
║   1. Update GPU drivers to the latest version
║   2. Use --gpu <index> to try a different GPU
╚════════════════════════════════════════════════════════════════
```

### 2. Shader Compilation Fix (MergeFrequency.hlsl, TextureOps.hlsl)

**Issue:** The `[[vk::image_format("rgba32f")]]` attribute caused DXC compilation errors:
```
error: 'image_format' : unrecognized attribute
```

**Fix:** Removed the `image_format` attributes and added explanatory comments:
```hlsl
// NOTE: image_format attribute removed - requires ShaderStorageImageWriteWithoutFormat feature instead
// [[vk::image_format("rgba32f")]]  // This causes DXC compilation errors on some systems
RWTexture2D<float4> OutputTexture : register(u10);
```

**Rationale:**
- The attribute is optional when `ShaderStorageImageWriteWithoutFormat` feature is enabled
- Some DXC versions don't recognize this Vulkan-specific attribute
- The feature flag handles the same requirement at runtime

### 3. Pipeline Initialization Check (VulkanComputePipeline.cs)

**Added to `EnsureMergeFrequencyPipeline()`:**
- Feature support check before attempting to create frequency domain pipeline
- Clear error message with actionable solutions
- Throws `NotSupportedException` with helpful message

**Output when trying HigherQuality without feature support:**
```
╔════════════════════════════════════════════════════════════════
║ ❌ FREQUENCY DOMAIN PIPELINE UNAVAILABLE
╠════════════════════════════════════════════════════════════════
║ Cannot initialize HigherQuality algorithm:
║
║ Required Vulkan feature NOT supported:
║   ShaderStorageImageWriteWithoutFormat = false
║
║ This feature is required for:
║   - RWTexture2D<float4> writes in compute shaders
║   - FFT-based frequency domain processing
║
║ Solution:
║   Use --algorithm Fast instead of HigherQuality
║
║ Or try:
║   1. Update GPU drivers
║   2. Use --list-gpus and --gpu <index> to try another GPU
╚════════════════════════════════════════════════════════════════
```

### 4. Comprehensive FFT Execution Tracing (VulkanComputePipeline.cs)

**Added to `ExecuteForwardFft()`:**

Detailed step-by-step logging of the entire FFT dispatch process:

```
╔════════════════════════════════════════════════════════════════
║ [FFT DEBUG] ExecuteForwardFft ENTRY
╠════════════════════════════════════════════════════════════════
║ [1/9] Ensuring pipeline is initialized...
║       ✓ Pipeline initialized successfully
║ [2/9] Configuration:
║       Input texture:  512x384 (format: R32G32B32A32Sfloat)
║       Output texture: 1024x384 (format: R32G32B32A32Sfloat)
║       TileSize: 8
║       Spatial dimensions: 512x384
║       Tile grid: 64x48 tiles
║       Expected threads: 3072
║ [3/9] Creating parameter buffer...
║       ✓ Parameter buffer created (TileSize=8)
║ [4/9] Beginning command buffer...
║       ✓ Command buffer created
║ [5/9] Transitioning image layouts...
║       ✓ Layouts transitioned to General
║ [6/9] Setting up descriptors...
║       ✓ Descriptors bound:
║         - Binding 0: UniformBuffer (FrequencyParams)
║         - Binding 1: SampledImage (input RGBA)
║         - Binding 10: StorageImage (output FT, double width)
║ [7/9] Binding pipeline...
║       ✓ Pipeline and descriptor sets bound
║ [8/9] Dispatching compute shader:
║       Workgroups: 4x3 = 12 groups
║       Threads: 64x48 = 3072 threads
║       Active threads (within bounds): 64x48 = 3072
║       Workgroup size: 16x16x1 = 256 threads/group
║       >>> DISPATCHING NOW <<<
║       ✓ Dispatch command recorded
║ [9/9] Executing command buffer and waiting for completion...
║       ✓ GPU execution COMPLETE (QueueWaitIdle returned)
╠════════════════════════════════════════════════════════════════
║ [FFT DEBUG] ExecuteForwardFft EXIT - SUCCESS
╚════════════════════════════════════════════════════════════════
```

**This tells us:**
1. **If the pipeline initializes** - Feature check passes
2. **Texture dimensions** - Verifies input/output size calculations
3. **Descriptor bindings** - Confirms correct resource mapping
4. **Dispatch parameters** - Shows exact thread counts and workgroup layout
5. **Execution status** - Whether GPU dispatch completes without errors
6. **Exception details** - If any step fails, shows exactly where and why

### 5. Shader Compilation Logging

**Added to pipeline initialization:**
```
[Pipeline] Initializing Frequency Domain (HigherQuality) pipeline...
[Pipeline]   Compiling forward_fft shader...
[Pipeline]   ✓ forward_fft compiled successfully (12548 bytes SPIR-V)
[Pipeline]   ✓ forward_fft kernel created
[Pipeline]   Compiling backward_fft shader...
[Pipeline]   ✓ backward_fft compiled successfully (15632 bytes SPIR-V)
[Pipeline]   ✓ backward_fft kernel created
```

**This tells us:**
- Whether shader compilation succeeds or fails
- The size of the compiled SPIR-V bytecode
- If kernel creation succeeds after compilation

## Diagnostic Workflow

### Step 1: Check Feature Support

When you run your application, look for this output during Vulkan initialization:

**If feature IS supported:**
```
✓ ShaderStorageImageWriteWithoutFormat feature is supported and enabled
```

**If feature is NOT supported:**
```
╔════════════════════════════════════════════════════════════════
║ ⚠️  FEATURE NOT SUPPORTED
...
```

### Step 2: Try HigherQuality Algorithm

If the feature is NOT supported, attempting to use HigherQuality will fail at pipeline creation:

```bash
burstphoto process --algorithm HigherQuality input.dng output.dng
```

**Expected output:**
```
╔════════════════════════════════════════════════════════════════
║ ❌ FREQUENCY DOMAIN PIPELINE UNAVAILABLE
...
NotSupportedException: HigherQuality algorithm requires ShaderStorageImageWriteWithoutFormat...
```

### Step 3: If Feature IS Supported, Check Compilation

If the feature IS supported but FFT still fails, the new debug output will show:

**Where it fails:**
- ❌ At shader compilation → DXC error
- ❌ At kernel creation → Vulkan pipeline error
- ❌ At dispatch → Execution error
- ✓ Dispatch succeeds but output is zeros → Shader logic or descriptor issue

**Example - Compilation failure:**
```
[Pipeline]   Compiling forward_fft shader...
Exception: Shader compilation failed: ...
```

**Example - Successful compilation but zero output:**
```
[Pipeline]   ✓ forward_fft compiled successfully (12548 bytes SPIR-V)
[Pipeline]   ✓ forward_fft kernel created
...
║ [8/9] Dispatching compute shader:
║       >>> DISPATCHING NOW <<<
║       ✓ Dispatch command recorded
║ [9/9] Executing command buffer and waiting for completion...
║       ✓ GPU execution COMPLETE (QueueWaitIdle returned)
...
❌ After forward_fft: sum=0.00, mean=0.00
```

This would indicate the shader ran but didn't write correct output (descriptor issue, bounds check rejecting all threads, etc.)

## Testing Checklist

### Test 1: Feature Detection
```bash
burstphoto process --list-gpus
```
Look for the feature support message for each GPU.

### Test 2: Fast Algorithm (Should always work)
```bash
burstphoto process --algorithm Fast input1.dng input2.dng -o output/
```

### Test 3: HigherQuality Algorithm
```bash
burstphoto process --algorithm HigherQuality input1.dng input2.dng -o output/
```

**Possible outcomes:**
1. ❌ Fails at pipeline init → Feature not supported (expected on your system)
2. ❌ Fails at shader compilation → DXC/SPIR-V issue
3. ❌ Dispatch succeeds, output zeros → Shader execution or descriptor issue
4. ✅ Works correctly → Feature supported and implementation correct

## Known Issues on Your System

Based on your GPU Compatibility Report:

**Issue:** `ShaderStorageImageWriteWithoutFormat` is **NOT supported** on either GPU
- GPU 0 (AMD Radeon): Feature not supported
- GPU 1 (NVIDIA): Feature not supported

**Root Cause:**
- Hardware limitation OR
- Driver version doesn't expose the feature OR
- Vulkan runtime version incompatibility

**Workaround:**
Use `--algorithm Fast` for all processing.

## Next Steps

1. **Update GPU drivers** to the absolute latest version
   - AMD: https://www.amd.com/en/support
   - NVIDIA: https://www.nvidia.com/drivers

2. **Check Vulkan version**
   ```bash
   vulkaninfo | findstr "apiVersion"
   ```
   Ensure you have Vulkan 1.2 or higher.

3. **Try different GPU** (if driver update doesn't help)
   ```bash
   burstphoto process --list-gpus
   burstphoto process --gpu 0 --algorithm HigherQuality ...
   burstphoto process --gpu 1 --algorithm HigherQuality ...
   ```

4. **Report the findings**
   Share the console output (especially GPU info and feature support messages) to help diagnose if this is:
   - Expected hardware limitation
   - Driver bug
   - Implementation issue

## Additional Debug Output Locations

All debug output is written to stdout. To save for analysis:

**Windows PowerShell:**
```powershell
burstphoto process ... 2>&1 | Tee-Object -FilePath debug.log
```

**Windows CMD:**
```cmd
burstphoto process ... > debug.log 2>&1
```

The debug.log file will contain all the diagnostic information for review.
