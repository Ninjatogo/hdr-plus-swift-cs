# Frequency Domain Implementation Analysis & Bug Report

## 1. Executive Summary

A deep analysis of the C#/HLSL/Vulkan implementation of the Frequency Domain Merging feature was conducted, comparing it against the original Swift/Metal reference. The goal was to identify the root cause of the "Forward FFT Zero Output" bug (Blocker) and validate the port's fidelity.

**Key Findings:**
1.  **Port Fidelity**: The HLSL shader logic (`MergeFrequency.hlsl`) is a faithful, line-by-line port of `frequency.metal`. Algorithmically, they are identical.
2.  **Zero Output Bug**: The most probable cause of the Forward FFT producing zeros is a failure in the `RefTexture.Load` operation or a silent failure in writing to `OutputTexture`.
3.  **Critical Missing Feature**: The `VulkanContext` does not enable the `shaderStorageImageWriteWithoutFormat` feature. While some shaders (like `prepare`) seem to work without it (likely due to driver leniency or fallback), the FFT shader's failure to write *any* output strongly suggests strict enforcement or an undefined behavior scenario for `RWTexture2D<float4>`.
4.  **Configuration Discrepancy**: The `VulkanImage` creation logic uses `MipLevels = 1` (default), which is correct for `Load(int3(x,y,0))`. However, any mismatch in texture coordinate bounds or descriptor type could cause `Load` to return 0.

## 2. Detailed Comparison

### 2.1. Shader Logic (`MergeFrequency.hlsl` vs `frequency.metal`)

| Feature | Metal | HLSL | Status |
| :--- | :--- | :--- | :--- |
| **Signature** | `forward_fft(texture2d<float> in, texture2d<float> out, ...)` | `forward_fft(RWTexture2D<float4> out, Texture2D<float4> in)` | ✅ Matches (Types Compatible) |
| **Input Access** | `in_texture.read(uint2(x, y))` | `RefTexture.Load(int3(x, y, 0))` | ✅ Matches |
| **Loop Logic** | Unrolled butterflies, `dm`/`dn` loops | Identical structure | ✅ Matches |
| **Constants** | `TileSize` passed in buffer | `TileSize` in `cbuffer` | ✅ Matches |
| **Data Types** | `float` (implies `float4` read) | `float4` (explicit) | ✅ Matches |

### 2.2. Pipeline & Descriptors (`VulkanComputePipeline.cs`)

| Feature | Implementation | Notes |
| :--- | :--- | :--- |
| **Descriptor Binding** | `UpdateImage` with `SampledImage` | Correctly maps to `Texture2D` (t1) |
| **Layouts** | `ImageLayout.General` | Valid for both Storage and Sampled usage |
| **Dispatch** | `Ceil(nTiles / 16)` | Correctly covers the grid. No bounds check in shader (minor risk for edge threads but valid threads should work). |
| **Struct Alignment** | `FrequencyParams` (C#) | Aligns perfectly with HLSL `cbuffer` (16-byte blocks). |

## 3. Investigation of "Zero Output" Bug

The logs indicate:
```
Iteration 1:
✅ Raw input data: sum=133507466
✅ After convert_to_rgba: sum=7250654 (Valid Data)
❌ After forward_fft: sum=0.00
```

Since the input `rgbaRefTexture` is confirmed to contain valid data, the failure occurs during the `forward_fft` execution.

### Potential Causes (Ranked by Probability)

#### 1. `RefTexture.Load` returns (0,0,0,0)
In HLSL, `Load` returns 0 if coordinates are out of bounds or if the texture is not bound correctly.
*   **Coordinate Analysis**:
    *   C#: `ExecuteForwardFft(..., tile_size_merge, rgbaWidth, rgbaHeight)`
    *   Dispatch: `nTilesX = rgbaWidth / 8`.
    *   Shader: `m0 = gid.x * 8`.
    *   Max `m0` approx `rgbaWidth`.
    *   Coordinates are within [0, width]. **Valid.**
*   **Binding Analysis**:
    *   HLSL: `[[vk::binding(1, 0)]] Texture2D<float4> RefTexture : register(t1);`
    *   C#: `_descriptors.UpdateImage(set, 1, input.View, ...)`
    *   **Verified**: Bindings match.

#### 2. `OutputTexture` writes are discarded
*   **Context**: The shader uses `RWTexture2D<float4>` without a format qualifier (e.g., `[[vk::image_format("rgba32f")]]`).
*   **Vulkan Spec**: Writing to a storage image without a format in the shader requires the `shaderStorageImageWriteWithoutFormat` feature to be enabled on the logical device.
*   **Finding**: `VulkanContext.cs` **does not** enable this feature.
*   **Evidence**:
    *   `prepare_texture_bayer` uses `RWTexture2D<float>` (R32F) and works.
    *   `forward_fft` uses `RWTexture2D<float4>` (RGBA32F) and fails.
    *   It is highly likely the driver/hardware combination being tested supports R32F writes implicitly but fails or ignores RGBA32F writes without the feature flag.

## 4. Recommendations & Fixes

### Fix 1: Enable Vulkan Features (Critical)
Modify `VulkanContext.cs` to explicitly query and enable `shaderStorageImageWriteWithoutFormat`.

```csharp
// In VulkanContext.cs
var features = new PhysicalDeviceFeatures {
    ShaderStorageImageWriteWithoutFormat = true
};
var deviceCreateInfo = new DeviceCreateInfo {
    // ...
    PEnabledFeatures = &features
};
```

### Fix 2: Explicit Image Formats in HLSL
To define strictly valid SPIR-V without relying on the feature, add format attributes to the HLSL resources.

**In `MergeFrequency.hlsl`:**
```hlsl
[[vk::binding(10, 0)]]
[[vk::image_format("rgba32f")]] // Add this
RWTexture2D<float4> OutputTexture : register(u10);
```

### Fix 3: Bounds Checking
Add explicit bounds checks to `forward_fft` to prevent out-of-bounds reads/writes for non-multiple-of-16 dimensions.

```hlsl
void forward_fft_impl(...) {
    uint width, height;
    outFT.GetDimensions(width, height);
    if (gid.x * ts >= width || gid.y * ts >= height) return;
    // ...
}
```

## 5. Conclusion
The "Forward FFT Zero Output" bug is most likely caused by the missing `shaderStorageImageWriteWithoutFormat` feature support for `float4` textures in the Vulkan context. Implementing **Fix 1** and **Fix 2** should resolve the blocker. The port logic itself is sound.
