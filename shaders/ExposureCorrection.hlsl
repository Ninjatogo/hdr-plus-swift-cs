// Exposure Correction compute shaders - converted from Metal to HLSL
// Original: burstphoto/exposure/exposure.metal

#include "Constants.hlsli"

// Textures and buffers
RWTexture2D<float> final_texture_blurred : register(u0);
RWTexture2D<float> final_texture : register(u1);
RWTexture1D<float> in_texture_1d : register(u2);
RWTexture1D<float> out_texture_1d : register(u3);

// Buffers
RWBuffer<float> black_levels_mean_buffer : register(u4);
RWBuffer<float> max_texture_buffer : register(u5);
RWBuffer<float> out_buffer : register(u6);

// Constant buffers
cbuffer Params : register(b0)
{
    int exposure_bias;
    int target_exposure;
    int mosaic_pattern_width;
    float white_level;
    float color_factor_mean;
    float black_level_mean;
    float black_level_min;
    float linear_gain;
    int width;
};

// =============================================================================
// CORRECT_EXPOSURE - Correction of underexposure with Reinhard tone mapping
// Inspired by https://www-old.cs.utah.edu/docs/techreports/2002/pdf/UUCS-02-001.pdf
// =============================================================================
[numthreads(8, 8, 1)]
void correct_exposure(uint3 gid : SV_DispatchThreadID)
{
    // Load black level for this mosaic position
    float black_level = black_levels_mean_buffer[mosaic_pattern_width * (gid.y % mosaic_pattern_width) +
                                                 (gid.x % mosaic_pattern_width)];

    // Calculate gain for intensity correction
    float correction_stops = float((target_exposure - exposure_bias) / 100.0f);

    // Calculate linear gain to get close to full range before tone mapping
    float linear_gain_calc = (white_level - black_level_min) / (max_texture_buffer[0] - black_level_min);
    linear_gain_calc = clamp(0.9f * linear_gain_calc, 1.0f, 16.0f);

    // Gain is limited to 4.0 stops and damped for values > 2.0 stops
    float gain_stops = clamp(correction_stops - log2(linear_gain_calc), 0.0f, 4.0f);
    float gain0 = pow(2.0f, gain_stops - 0.05f * max(0.0f, gain_stops - 1.5f));
    float gain1 = pow(2.0f, gain_stops / 1.4f);

    // Extract pixel value
    float pixel_value = final_texture[gid.xy].r;

    // Subtract black level and rescale intensity to range 0 to 1
    float rescale_factor = (white_level - black_level_min);
    pixel_value = clamp((pixel_value - black_level) / rescale_factor, 0.0f, 1.0f);

    // Use luminance estimated as binomial weighted mean in 3x3 window
    // Apply correction with color factors to reduce clipping of green channel
    float luminance_before = final_texture_blurred[gid.xy].r;
    luminance_before = clamp((luminance_before - black_level_mean) / (rescale_factor * color_factor_mean),
                            1e-12, 1.0f);

    // Apply gains
    float luminance_after0 = linear_gain_calc * gain0 * luminance_before;
    float luminance_after1 = linear_gain_calc * gain1 * luminance_before;

    // Apply tone mapping operator from Reinhard paper (equation 4)
    // Linear in shadows and midtones while protecting highlights
    luminance_after0 = luminance_after0 * (1.0f + luminance_after0 / (gain0 * gain0)) / (1.0f + luminance_after0);

    // Modified tone mapping operator for better highlight protection
    float luminance_max = gain1 * (0.4f + gain1 / (gain1 * gain1)) / (0.4f + gain1);
    luminance_after1 = luminance_after1 * (0.4f + luminance_after1 / (gain1 * gain1)) /
                      ((0.4f + luminance_after1) * luminance_max);

    // Calculate weight for blending the two tone mapping curves
    float weight = clamp(gain_stops * 0.25f, 0.0f, 1.0f);

    // Apply scaling derived from luminance values and return to original scale
    pixel_value = pixel_value * ((1.0f - weight) * luminance_after0 + weight * luminance_after1) /
                 luminance_before * rescale_factor + black_level;
    pixel_value = clamp(pixel_value, 0.0f, float(UINT16_MAX_VAL));

    final_texture[gid.xy] = pixel_value;
}

// =============================================================================
// CORRECT_EXPOSURE_LINEAR - Correction of underexposure with simple linear scaling
// =============================================================================
[numthreads(8, 8, 1)]
void correct_exposure_linear(uint3 gid : SV_DispatchThreadID)
{
    // Load black level for this mosaic position
    float black_level = black_levels_mean_buffer[mosaic_pattern_width * (gid.y % mosaic_pattern_width) +
                                                 (gid.x % mosaic_pattern_width)];

    // Calculate correction factor to get close to full range
    float corr_factor = (white_level - black_level_min) / (max_texture_buffer[0] - black_level_min);
    corr_factor = clamp(0.9f * corr_factor, 1.0f, 16.0f);
    // Use maximum of specified linear gain and correction factor
    corr_factor = max(linear_gain, corr_factor);

    // Extract pixel value
    float pixel_value = final_texture[gid.xy].r;

    // Correct exposure
    pixel_value = max(0.0f, pixel_value - black_level) * corr_factor + black_level;
    pixel_value = clamp(pixel_value, 0.0f, float(UINT16_MAX_VAL));

    final_texture[gid.xy] = pixel_value;
}

// =============================================================================
// MAX_X - Find maximum value along x-axis of 1D texture
// =============================================================================
[numthreads(256, 1, 1)]
void max_x(uint3 gid : SV_DispatchThreadID)
{
    float max_value = 0;

    for (int x = 0; x < width; x++) {
        max_value = max(max_value, in_texture_1d[uint(x)].r);
    }

    out_buffer[0] = max_value;
}

// =============================================================================
// MAX_Y - Find maximum value along y-axis of 2D texture, output to 1D texture
// =============================================================================
[numthreads(256, 1, 1)]
void max_y(uint3 gid : SV_DispatchThreadID)
{
    uint x = gid.x;
    uint texture_height;
    final_texture.GetDimensions(texture_height, texture_height);

    float max_value = 0;

    for (int y = 0; y < (int)texture_height; y++) {
        max_value = max(max_value, final_texture[uint2(x, y)].r);
    }

    out_texture_1d[x] = max_value;
}
