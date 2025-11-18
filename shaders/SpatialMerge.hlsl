// Spatial Merge compute shaders - converted from Metal to HLSL
// Original: burstphoto/merge/spatial.metal

#include "Constants.hlsli"

// Textures
RWTexture2D<float> texture1 : register(u0);
RWTexture2D<float> texture2 : register(u1);
RWTexture2D<float> out_texture : register(u2);
RWTexture2D<float> texture_diff : register(u3);
RWTexture2D<float> weight_texture : register(u4);

// Constant buffer
RWBuffer<float> noise_sd_buffer : register(u5);

cbuffer Params : register(b0)
{
    int mosaic_pattern_width;
    float robustness;
};

// =============================================================================
// COLOR_DIFFERENCE - Compute absolute difference between two textures
// Aggregates differences across a mosaic pattern block (e.g., 2x2 for Bayer)
// =============================================================================
[numthreads(8, 8, 1)]
void color_difference(uint3 gid : SV_DispatchThreadID)
{
    float total_diff = 0;
    int x0 = gid.x * mosaic_pattern_width;
    int y0 = gid.y * mosaic_pattern_width;

    for (int dx = 0; dx < mosaic_pattern_width; dx++) {
        for (int dy = 0; dy < mosaic_pattern_width; dy++) {
            int x = x0 + dx;
            int y = y0 + dy;
            float i1 = texture1[uint2(x, y)].r;
            float i2 = texture2[uint2(x, y)].r;
            total_diff += abs(i1 - i2);
        }
    }

    out_texture[gid.xy] = total_diff;
}

// =============================================================================
// COMPUTE_MERGE_WEIGHT - Calculate pixel-wise merging weights
// Implements robust merging based on motion detection
// Weight = 0 means aligned image is ignored (motion detected)
// Weight = 1 means aligned image has full weight (no motion)
// =============================================================================
[numthreads(8, 8, 1)]
void compute_merge_weight(uint3 gid : SV_DispatchThreadID)
{
    // Load noise standard deviation
    float noise_sd = noise_sd_buffer[0];

    // Load texture difference
    float diff = texture_diff[gid.xy].r;

    // Compute the weight to assign to the comparison frame
    float weight;
    if (robustness == 0) {
        // Robustness == 0 means that robust merge is turned off
        weight = 1;
    } else {
        // Compare the difference to image noise
        // As diff increases, the weight continuously decreases from 1.0 to 0.0
        // The two extreme cases are:
        // diff == 0                   --> aligned image will have weight 1.0
        // diff >= noise_sd/robustness --> aligned image will have weight 0.0
        float max_diff = noise_sd / robustness;
        weight = 1 - diff / max_diff;
        weight = clamp(weight, 0.0, 1.0);
    }

    // Write weight
    weight_texture[gid.xy] = weight;
}
