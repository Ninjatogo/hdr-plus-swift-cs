/*
 * Exposure.hlsl
 * Port of exposure.metal
 */

#include "Constants.hlsli"

// -------------------------------------------------------------------------
// Constant Buffers
// -------------------------------------------------------------------------

[[vk::binding(0, 0)]]
cbuffer ExposureParams : register(b0)
{
    float WhiteLevel;
    float LinearGain;
    float ColorFactorMean;
    float BlackLevelMean;
    float BlackLevelMin;
    int ExposureBias;
    int TargetExposure;
    int MosaicPatternWidth;
    int TextureWidth; // For max_x kernel

    // Padding
};

// -------------------------------------------------------------------------
// Resources
// -------------------------------------------------------------------------
// NOTE: Using explicit [[vk::binding]] attributes to match C# descriptor layout.
// C# binds: Binding 0 (b0), Binding 1 (t0), Binding 2 (t1), Binding 3 (t2), Binding 4 (t3), Binding 10 (u0), Binding 11 (u1)

// Generic slots
[[vk::binding(1, 0)]]
Texture2D<float> InTexture      : register(t0);

[[vk::binding(2, 0)]]
Texture2D<float> InBlurred      : register(t1);

[[vk::binding(3, 0)]]
StructuredBuffer<float> BlackLevelsMean : register(t2); // Array of black levels

[[vk::binding(4, 0)]]
StructuredBuffer<float> MaxTextureBuffer : register(t3); // Buffer containing max value

[[vk::binding(10, 0)]]
RWTexture2D<float> OutTexture   : register(u0);

[[vk::binding(11, 0)]]
RWBuffer<float> OutBuffer       : register(u1); // For reduction output

// -------------------------------------------------------------------------
// Kernels
// -------------------------------------------------------------------------

[numthreads(16, 16, 1)]
void correct_exposure(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    int bw = MosaicPatternWidth;
    
    // Load black level from buffer (flat index)
    int bl_idx = bw * (gid.y % bw) + (gid.x % bw);
    float black_level = BlackLevelsMean[bl_idx];
    
    // Calculate gains
    float correction_stops = (float)(TargetExposure - ExposureBias) / 100.0f;
    float max_tex = MaxTextureBuffer[0];
    
    float linear_gain = (WhiteLevel - BlackLevelMin) / (max_tex - BlackLevelMin);
    linear_gain = clamp(0.9f * linear_gain, 1.0f, 16.0f);
    
    float gain_stops = clamp(correction_stops - log2(linear_gain), 0.0f, 4.0f);
    float gain0 = pow(2.0f, gain_stops - 0.05f * max(0.0f, gain_stops - 1.5f));
    float gain1 = pow(2.0f, gain_stops / 1.4f);
    
    float pixel_value = OutTexture[gid]; // Input is also Output (RW) in Metal?
    // Metal: final_texture (RW) read/write.
    // So we read u0, write u0.
    // If not readable, we need input texture (t0) mapped to same.
    // Assuming Readable RW.
    
    float rescale_factor = WhiteLevel - BlackLevelMin;
    pixel_value = clamp((pixel_value - black_level) / rescale_factor, 0.0f, 1.0f);
    
    float luminance_before = InBlurred.Load(int3(gid, 0)); // t1
    luminance_before = clamp((luminance_before - BlackLevelMean) / (rescale_factor * ColorFactorMean), 1e-12f, 1.0f);
    
    float luminance_after0 = linear_gain * gain0 * luminance_before;
    float luminance_after1 = linear_gain * gain1 * luminance_before;
    
    // Tone mapping
    luminance_after0 = luminance_after0 * (1.0f + luminance_after0 / (gain0 * gain0)) / (1.0f + luminance_after0);
    
    float luminance_max = gain1 * (0.4f + gain1 / (gain1 * gain1)) / (0.4f + gain1);
    luminance_after1 = luminance_after1 * (0.4f + luminance_after1 / (gain1 * gain1)) / ((0.4f + luminance_after1) * luminance_max);
    
    float weight = clamp(gain_stops * 0.25f, 0.0f, 1.0f);
    
    pixel_value = pixel_value * ((1.0f - weight) * luminance_after0 + weight * luminance_after1) / luminance_before * rescale_factor + black_level;
    pixel_value = clamp(pixel_value, 0.0f, (float)UINT16_MAX_VAL);
    
    OutTexture[gid] = pixel_value;
}

[numthreads(16, 16, 1)]
void correct_exposure_linear(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    int bw = MosaicPatternWidth;
    int bl_idx = bw * (gid.y % bw) + (gid.x % bw);
    float black_level = BlackLevelsMean[bl_idx];
    
    float max_tex = MaxTextureBuffer[0];
    float corr_factor = (WhiteLevel - BlackLevelMin) / (max_tex - BlackLevelMin);
    corr_factor = clamp(0.9f * corr_factor, 1.0f, 16.0f);
    corr_factor = max(LinearGain, corr_factor);
    
    float pixel_value = OutTexture[gid];
    
    pixel_value = max(0.0f, pixel_value - black_level) * corr_factor + black_level;
    pixel_value = clamp(pixel_value, 0.0f, (float)UINT16_MAX_VAL);
    
    OutTexture[gid] = pixel_value;
}

// -------------------------------------------------------------------------
// Reductions
// -------------------------------------------------------------------------

// max_y: Reduce 2D texture to 1D texture (Max of column)
// This is slow single-threaded per column. Ideally use threadgroup memory.
// Porting Metal implementation directly.
[numthreads(64, 1, 1)] // Metal uses 'uint gid', implies 1D grid? Or 2D?
// Metal max_y: 'uint gid [[thread_position_in_grid]]'. Texture1D output.
// Loop y=0..height.
void max_y(uint3 DTid : SV_DispatchThreadID)
{
    uint x = DTid.x;
    uint width, height;
    InTexture.GetDimensions(width, height);
    
    if (x >= width) return;
    
    float max_val = 0.0f;
    for (uint y = 0; y < height; y++) {
        float val = InTexture.Load(int3(x, y, 0));
        max_val = max(max_val, val);
    }
    
    // Write to OutputTexture (as 1D texture, height=1?)
    OutTexture[uint2(x, 0)] = max_val;
}

// max_x: Reduce 1D texture (or buffer?) to scalar buffer.
// Metal max_x: texture1d input.
// Loop x=0..width.
[numthreads(1, 1, 1)] 
void max_x(uint3 DTid : SV_DispatchThreadID)
{
    // Single thread reduces entire row.. slow but matches Metal.
    // InTexture is the 1D result from max_y?
    // Metal max_x reads texture1d.
    // HLSL Texture2D with height 1?
    
    uint width = TextureWidth; 
    // Or GetDimensions
    
    float max_val = 0.0f;
    for (uint x = 0; x < width; x++) {
       // Assuming input is t0
       float val = InTexture.Load(int3(x, 0, 0));
       max_val = max(max_val, val);
    }
    
    OutBuffer[0] = max_val;
}
