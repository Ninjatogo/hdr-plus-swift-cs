// Texture Utilities compute shaders - converted from Metal to HLSL
// Original: burstphoto/texture/texture.metal
// Contains 25 utility kernels for texture operations

#include "Constants.hlsli"

// Textures and buffers
RWTexture2D<float> in_texture : register(u0);
RWTexture2D<float> in_texture_blurred : register(u1);
RWTexture2D<float> out_texture : register(u2);
RWTexture2D<float> norm_texture : register(u3);
RWTexture2D<float> weight_highlights_texture : register(u4);
RWTexture2D<uint> in_texture_uint : register(u5);
RWTexture2D<float> out_texture_uint16 : register(u6);
RWTexture2D<float> texture2 : register(u7);
RWTexture2D<float> weight_texture : register(u8);
RWTexture2D<float> average_texture : register(u9);
RWTexture2D<float> hotpixel_weight_texture : register(u10);
RWTexture1D<float> out_texture_1d : register(u11);
RWTexture2D<int> in_texture_int : register(u12);
RWTexture2D<int> out_texture_int : register(u13);

// Buffers
RWBuffer<float> out_buffer : register(u14);
RWBuffer<float> in_buffer : register(u15);
RWBuffer<float> mean_texture_buffer : register(u16);
RWBuffer<float> black_levels : register(u17);

// Constant buffers
cbuffer Params : register(b0)
{
    float n_textures;
    int exposure_bias;
    float white_level;
    float black_level_mean;
    float color_factor_mean;
    int kernel_size;
    int mosaic_pattern_width;
    int texture_size;
    int direction;
    int white_level_int;
    int factor_16bit;
    int pad_left;
    int pad_top;
    float hot_pixel_threshold;
    float hot_pixel_multiplicator;
    float correction_strength;
    int exposure_diff;
    float divisor;
    int buffer_size;
    float norm_scalar;
    int top;
    int left;
    int bottom;
    int width;
    float scale_x;
    float scale_y;
    float factor_red;
    float factor_blue;
};

// =============================================================================
// ADD_TEXTURE - Simple texture addition with averaging
// =============================================================================
[numthreads(8, 8, 1)]
void add_texture(uint3 gid : SV_DispatchThreadID)
{
    float color_value = out_texture[gid.xy].r + in_texture[gid.xy].r / n_textures;
    out_texture[gid.xy] = color_value;
}

// =============================================================================
// ADD_TEXTURE_EXPOSURE - Add texture with exposure-dependent weighting
// Used for merging frames with different exposures
// =============================================================================
[numthreads(8, 8, 1)]
void add_texture_exposure(uint3 gid : SV_DispatchThreadID)
{
    // Calculate weight based on exposure bias
    float weight_exposure = pow(2.0f, float(exposure_bias / 100.0f));

    // Extract pixel value
    float pixel_value = in_texture[gid.xy].r;

    // Adapt exposure weight based on luminosity relative to white level
    float luminance = min(white_level, in_texture_blurred[gid.xy].r / color_factor_mean);
    luminance = (luminance - black_level_mean) / white_level;

    // Shadows get exposure-dependent weight for optimal noise reduction
    // Midtones and highlights have reduced weight for better motion robustness
    weight_exposure = max(sqrt(weight_exposure),
                         weight_exposure * pow(weight_exposure, -0.5f / (0.25f - black_level_mean / white_level) * luminance));

    // Ensure smooth blending for pixel values between 0.25 and 0.99 of white level
    float weight_highlights = weight_highlights_texture[gid.xy].r;

    // Apply optimal weight
    pixel_value = weight_exposure * weight_highlights * pixel_value;

    out_texture[gid.xy] = out_texture[gid.xy].r + pixel_value;
    norm_texture[gid.xy] = norm_texture[gid.xy].r + weight_exposure * weight_highlights;
}

// =============================================================================
// ADD_TEXTURE_HIGHLIGHTS - Add texture with highlight clipping prevention
// Extrapolates green pixels from surrounding red/blue pixels if clipped
// =============================================================================
[numthreads(8, 8, 1)]
void add_texture_highlights(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint texture_width, texture_height;
    in_texture.GetDimensions(texture_width, texture_height);

    int x = gid.x * 2;
    int y = gid.y * 2;

    float pixel_value4, pixel_value5, pixel_ratio4, pixel_ratio5, pixel_count, extrapolated_value, weight;

    // Extract pixel values of 2x2 super pixel (Bayer pattern)
    float pixel_value0 = in_texture[uint2(x, y)].r;
    float pixel_value1 = in_texture[uint2(x + 1, y)].r;
    float pixel_value2 = in_texture[uint2(x, y + 1)].r;
    float pixel_value3 = in_texture[uint2(x + 1, y + 1)].r;

    // Calculate ratio of pixel value and white level
    float pixel_ratio0 = (pixel_value0 - black_level_mean) / (white_level - black_level_mean);
    float pixel_ratio1 = (pixel_value1 - black_level_mean) / (white_level - black_level_mean);
    float pixel_ratio2 = (pixel_value2 - black_level_mean) / (white_level - black_level_mean);
    float pixel_ratio3 = (pixel_value3 - black_level_mean) / (white_level - black_level_mean);

    // Process first green channel if bright pixel detected
    if (pixel_ratio1 > 0.8f) {
        pixel_value4 = pixel_value5 = 0.0f;
        pixel_ratio4 = pixel_ratio5 = 0.0f;
        pixel_count = 2.0f;

        // Extract additional pixels close to the green pixel
        if (x + 2 < (int)texture_width) {
            pixel_value4 = in_texture[uint2(x + 2, y)].r;
            pixel_ratio4 = (pixel_value4 - black_level_mean) / (white_level - black_level_mean);
            pixel_count += 1.0f;
        }

        if (y - 1 >= 0) {
            pixel_value5 = in_texture[uint2(x + 1, y - 1)].r;
            pixel_ratio5 = (pixel_value5 - black_level_mean) / (white_level - black_level_mean);
            pixel_count += 1.0f;
        }

        // If at least one surrounding pixel is above normalized clipping threshold
        if (pixel_ratio0 > 0.99f * factor_red || pixel_ratio3 > 0.99f * factor_blue ||
            pixel_ratio4 > 0.99f * factor_red || pixel_ratio5 > 0.99f * factor_blue) {
            // Extrapolate green pixel from surrounding red and blue pixels
            extrapolated_value = ((pixel_value0 + pixel_value4) / factor_red +
                                 (pixel_value3 + pixel_value5) / factor_blue) / pixel_count;

            // Calculate weight for blending
            weight = 0.9f - 4.5f * clamp(1.0f - pixel_ratio1, 0.0f, 0.2f);

            pixel_value1 = weight * max(extrapolated_value, pixel_value1) + (1.0f - weight) * pixel_value1;
        }
    }

    // Process second green channel if bright pixel detected
    if (pixel_ratio2 > 0.8f) {
        pixel_value4 = pixel_value5 = 0.0f;
        pixel_ratio4 = pixel_ratio5 = 0.0f;
        pixel_count = 2.0f;

        if (x - 1 >= 0) {
            pixel_value4 = in_texture[uint2(x - 1, y + 1)].r;
            pixel_ratio4 = (pixel_value4 - black_level_mean) / (white_level - black_level_mean);
            pixel_count += 1.0f;
        }

        if (y + 2 < (int)texture_height) {
            pixel_value5 = in_texture[uint2(x, y + 2)].r;
            pixel_ratio5 = (pixel_value5 - black_level_mean) / (white_level - black_level_mean);
            pixel_count += 1.0f;
        }

        if (pixel_ratio0 > 0.99f * factor_red || pixel_ratio3 > 0.99f * factor_blue ||
            pixel_ratio5 > 0.99f * factor_red || pixel_ratio4 > 0.99f * factor_blue) {
            extrapolated_value = ((pixel_value0 + pixel_value5) / factor_red +
                                 (pixel_value3 + pixel_value4) / factor_blue) / pixel_count;

            weight = 0.9f - 4.5f * clamp(1.0f - pixel_ratio2, 0.0f, 0.2f);

            pixel_value2 = weight * max(extrapolated_value, pixel_value2) + (1.0f - weight) * pixel_value2;
        }
    }

    // Add to output texture
    pixel_value0 = out_texture[uint2(x, y)].r + pixel_value0;
    pixel_value1 = out_texture[uint2(x + 1, y)].r + pixel_value1;
    pixel_value2 = out_texture[uint2(x, y + 1)].r + pixel_value2;
    pixel_value3 = out_texture[uint2(x + 1, y + 1)].r + pixel_value3;

    out_texture[uint2(x, y)] = pixel_value0;
    out_texture[uint2(x + 1, y)] = pixel_value1;
    out_texture[uint2(x, y + 1)] = pixel_value2;
    out_texture[uint2(x + 1, y + 1)] = pixel_value3;
}

// =============================================================================
// ADD_TEXTURE_UINT16 - Add unsigned 16-bit texture to float texture
// =============================================================================
[numthreads(8, 8, 1)]
void add_texture_uint16(uint3 gid : SV_DispatchThreadID)
{
    float color_value = out_texture[gid.xy].r + float(in_texture_uint[gid.xy].r) / n_textures;
    out_texture[gid.xy] = color_value;
}

// =============================================================================
// ADD_TEXTURE_WEIGHTED - Weighted blend of two textures
// =============================================================================
[numthreads(8, 8, 1)]
void add_texture_weighted(uint3 gid : SV_DispatchThreadID)
{
    float intensity1 = in_texture[gid.xy].r;
    float intensity2 = texture2[gid.xy].r;
    float weight = weight_texture[gid.xy].r;

    float out_intensity = weight * intensity2 + (1 - weight) * intensity1;
    out_texture[gid.xy] = out_intensity;
}

// =============================================================================
// BLUR_MOSAIC_TEXTURE - Separable binomial blur for mosaic patterns
// direction = 0: blur in x-direction, direction = 1: blur in y-direction
// =============================================================================
[numthreads(8, 8, 1)]
void blur_mosaic_texture(uint3 gid : SV_DispatchThreadID)
{
    // Set kernel weights of binomial filter
    float bw[9] = {1, 0, 0, 0, 0, 0, 0, 0, 0};
    int kernel_size_trunc = kernel_size;

    // Kernels are truncated so total contribution of removed weights < 0.25%
    if (kernel_size == 1) { bw[0] = 2; bw[1] = 1; }
    else if (kernel_size == 2) { bw[0] = 6; bw[1] = 4; bw[2] = 1; }
    else if (kernel_size == 3) { bw[0] = 20; bw[1] = 15; bw[2] = 6; bw[3] = 1; }
    else if (kernel_size == 4) { bw[0] = 70; bw[1] = 56; bw[2] = 28; bw[3] = 8; bw[4] = 1; }
    else if (kernel_size == 5) { bw[0] = 252; bw[1] = 210; bw[2] = 120; bw[3] = 45; bw[4] = 10; kernel_size_trunc = 4; }
    else if (kernel_size == 6) { bw[0] = 924; bw[1] = 792; bw[2] = 495; bw[3] = 220; bw[4] = 66; bw[5] = 12; kernel_size_trunc = 5; }
    else if (kernel_size == 7) { bw[0] = 3432; bw[1] = 3003; bw[2] = 2002; bw[3] = 1001; bw[4] = 364; bw[5] = 91; kernel_size_trunc = 5; }
    else if (kernel_size == 8) { bw[0] = 12870; bw[1] = 11440; bw[2] = 8008; bw[3] = 4368; bw[4] = 1820; bw[5] = 560; bw[6] = 120; kernel_size_trunc = 6; }
    else if (kernel_size == 16) { bw[0] = 601080390; bw[1] = 565722720; bw[2] = 471435600; bw[3] = 347373600; bw[4] = 225792840; bw[5] = 129024480; bw[6] = 64512240; bw[7] = 28048800; bw[8] = 10518300; kernel_size_trunc = 8; }

    float total_intensity = 0.0f;
    float total_weight = 0.0f;
    float weight;

    // direction = 0: blurring in x-direction, direction = 1: blurring in y-direction
    uint2 xy;
    xy[1 - direction] = gid[1 - direction];
    int i0 = gid[direction];

    for (int di = -kernel_size_trunc; di <= kernel_size_trunc; di++) {
        int i = i0 + mosaic_pattern_width * di;
        if (0 <= i && i < texture_size) {
            xy[direction] = i;
            weight = bw[abs(di)];
            total_intensity += weight * in_texture[xy].r;
            total_weight += weight;
        }
    }

    // Write output pixel
    float out_intensity = total_intensity / total_weight;
    out_texture[gid.xy] = out_intensity;
}

// =============================================================================
// CALCULATE_WEIGHT_HIGHLIGHTS - Calculate highlight protection weights
// =============================================================================
[numthreads(8, 8, 1)]
void calculate_weight_highlights(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint texture_width, texture_height;
    in_texture.GetDimensions(texture_width, texture_height);

    // Calculate weight based on exposure bias
    float weight_exposure = pow(2.0f, float(exposure_bias / 100.0f));

    // Find maximum intensity in kernel_size window around main pixel
    float pixel_value_max = 0.0f;

    for (int dy = -kernel_size; dy <= kernel_size; dy++) {
        int y = gid.y + dy;

        if (0 <= y && y < (int)texture_height) {
            for (int dx = -kernel_size; dx <= kernel_size; dx++) {
                int x = gid.x + dx;

                if (0 <= x && x < (int)texture_width) {
                    pixel_value_max = max(pixel_value_max, in_texture[uint2(x, y)].r);
                }
            }
        }
    }

    pixel_value_max = (pixel_value_max - black_level_mean) * weight_exposure + black_level_mean;

    // Ensure smooth blending for pixel values between 0.25 and 0.99 of white level
    float weight_highlights = clamp(0.99f / 0.74f - 1.0f / 0.74f * pixel_value_max / white_level, 0.0f, 1.0f);

    weight_highlights_texture[gid.xy] = weight_highlights;
}

// =============================================================================
// CONVERT_FLOAT_TO_UINT16 - Convert float texture to 16-bit unsigned integer
// =============================================================================
[numthreads(8, 8, 1)]
void convert_float_to_uint16(uint3 gid : SV_DispatchThreadID)
{
    // Load black level for this mosaic position
    float black_level = black_levels[(gid.x % mosaic_pattern_width) +
                                     mosaic_pattern_width * (gid.y % mosaic_pattern_width)];

    // Apply scaling to 16 bit and convert to integer
    int out_value = (int)round(factor_16bit * (in_texture[gid.xy].r - black_level) + black_level);
    out_value = clamp(out_value, 0, min(white_level_int, (int)UINT16_MAX_VAL));

    // Write back
    out_texture_uint16[gid.xy] = (uint)out_value;
}

// =============================================================================
// CONVERT_TO_BAYER - Convert RGBA to Bayer pattern
// =============================================================================
[numthreads(8, 8, 1)]
void convert_to_bayer(uint3 gid : SV_DispatchThreadID)
{
    int x = gid.x * 2;
    int y = gid.y * 2;

    float4 color_value = float4(in_texture[uint2(gid.x, gid.y)].r,
                                in_texture[uint2(gid.x, gid.y)].r,
                                in_texture[uint2(gid.x, gid.y)].r,
                                in_texture[uint2(gid.x, gid.y)].r);

    out_texture[uint2(x, y)] = color_value[0];
    out_texture[uint2(x + 1, y)] = color_value[1];
    out_texture[uint2(x, y + 1)] = color_value[2];
    out_texture[uint2(x + 1, y + 1)] = color_value[3];
}

// =============================================================================
// CONVERT_TO_RGBA - Convert Bayer pattern to RGBA
// =============================================================================
[numthreads(8, 8, 1)]
void convert_to_rgba(uint3 gid : SV_DispatchThreadID)
{
    int x = gid.x * 2 + pad_left;
    int y = gid.y * 2 + pad_top;

    float4 color_value = float4(in_texture[uint2(x, y)].r,
                                in_texture[uint2(x + 1, y)].r,
                                in_texture[uint2(x, y + 1)].r,
                                in_texture[uint2(x + 1, y + 1)].r);

    out_texture[gid.xy] = color_value.r; // Note: HLSL doesn't have float4 textures the same way
}

// =============================================================================
// COPY_TEXTURE - Simple texture copy
// =============================================================================
[numthreads(8, 8, 1)]
void copy_texture(uint3 gid : SV_DispatchThreadID)
{
    out_texture[gid.xy] = in_texture[gid.xy];
}

// =============================================================================
// CROP_TEXTURE - Crop texture with padding offset
// =============================================================================
[numthreads(8, 8, 1)]
void crop_texture(uint3 gid : SV_DispatchThreadID)
{
    int x = gid.x + pad_left;
    int y = gid.y + pad_top;

    float color_value = in_texture[uint2(x, y)].r;
    out_texture[gid.xy] = color_value;
}

// =============================================================================
// FILL_WITH_ZEROS - Fill texture with zeros
// =============================================================================
[numthreads(8, 8, 1)]
void fill_with_zeros(uint3 gid : SV_DispatchThreadID)
{
    out_texture[gid.xy] = 0;
}

// =============================================================================
// FIND_HOTPIXELS_BAYER - Hot pixel detection for Bayer pattern images
// Note: 2-pixel wide border is NOT analyzed for simplicity
// =============================================================================
[numthreads(8, 8, 1)]
void find_hotpixels_bayer(uint3 gid : SV_DispatchThreadID)
{
    // +2 offset from top-left edge to calculate sum of neighboring pixels
    int x = gid.x + 2;
    int y = gid.y + 2;

    // Extract color channel-dependent mean value and black level
    int ix = x % 2;
    int iy = y % 2;
    float black_level = float(black_levels[ix + 2 * iy]);
    float mean_texture = mean_texture_buffer[ix + 2 * iy] - black_level;

    // Calculate weighted sum of 8 surrounding pixels
    float sum = average_texture[uint2(x - 2, y - 2)].r;
    sum += average_texture[uint2(x + 2, y - 2)].r;
    sum += average_texture[uint2(x - 2, y + 2)].r;
    sum += average_texture[uint2(x + 2, y + 2)].r;
    sum += 2 * average_texture[uint2(x - 2, y + 0)].r;
    sum += 2 * average_texture[uint2(x + 2, y + 0)].r;
    sum += 2 * average_texture[uint2(x + 0, y - 2)].r;
    sum += 2 * average_texture[uint2(x + 0, y + 2)].r;

    sum /= 12.0;

    // Extract value of potential hot pixel and compute ratio
    float pixel_value = average_texture[uint2(x, y)].r;
    float pixel_ratio = max(1.0, pixel_value - black_level) / max(1.0, sum - black_level);

    // If hot pixel is detected
    if (pixel_ratio >= hot_pixel_threshold && pixel_value >= 2.0f * mean_texture) {
        // Calculate weight for smooth transition
        float weight = 0.5f * correction_strength * min(2.0f,
            hot_pixel_multiplicator * (pixel_ratio - hot_pixel_threshold));
        hotpixel_weight_texture[uint2(x, y)] = weight;
    }
}

// =============================================================================
// FIND_HOTPIXELS_XTRANS - Hot pixel detection for X-Trans pattern images
// Uses weighted average of 4 nearest same-color pixels
// =============================================================================
[numthreads(8, 8, 1)]
void find_hotpixels_xtrans(uint3 gid : SV_DispatchThreadID)
{
    // Lookup table for relative positions of closest 4 same-color sub-pixels
    // [row][col][offset_index][x/y]
    static const int offset[6][6][4][2] = {
        // Row 0
        {{{0, -1}, {1, -1}, {1, 0}, {-1, 1}},   // G
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},  // G
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}},    // B
         {{0, -1}, {1, -1}, {1, 0}, {-1, 1}},   // G
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},  // G
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}}},   // R
        // Row 1
        {{{-1, 2}, {-2, 0}, {-1, -2}, {2, -1}}, // B
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},    // R
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}},  // G
         {{2, -1}, {2, 1}, {-1, 2}, {-2, 0}},   // R
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},    // B
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}}}, // G
        // Row 2
        {{{1, 0}, {1, 1}, {0, 1}, {-1, 1}},     // G
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},   // G
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}}, // B
         {{1, 0}, {1, 1}, {0, 1}, {-1, 1}},     // G
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},   // G
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}}},// R
        // Row 3
        {{{0, -1}, {1, -1}, {1, 0}, {-1, 1}},   // G
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},  // G
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}},    // R
         {{0, -1}, {1, -1}, {1, 0}, {-1, 1}},   // G
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},  // G
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}}},   // B
        // Row 4
        {{{-1, 2}, {-2, 0}, {-1, -2}, {2, -1}}, // R
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},    // B
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}},  // G
         {{2, -1}, {2, 1}, {-1, 2}, {-2, 0}},   // B
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},    // R
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}}}, // G
        // Row 5
        {{{1, 0}, {1, 1}, {0, 1}, {-1, 1}},     // G
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},   // G
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}}, // R
         {{1, 0}, {1, 1}, {0, 1}, {-1, 1}},     // G
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},   // G
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}}} // B
    };

    // +2 offset from edges
    int x = gid.x + 2;
    int y = gid.y + 2;

    // Extract color channel-dependent mean value and black level
    int ix = x % 6;
    int iy = y % 6;
    float black_level = black_levels[ix + 6 * iy];
    float mean_texture = mean_texture_buffer[ix + 6 * iy] - black_level;

    // Weighted average of 4 nearest same-color pixels
    float sum = 0.0;
    float total = 0.0;
    float weight = 0.0;
    int dx = 0;
    int dy = 0;

    for (int off = 0; off < 4; off++) {
        dx = offset[iy][ix][off][0];
        dy = offset[iy][ix][off][1];
        weight = 1.0 / sqrt(pow(float(dx), 2) + pow(float(dy), 2));

        total += weight;
        float val = average_texture[uint2(x + dx, y + dy)].r;
        sum += weight * val;
    }
    sum /= total;

    // Extract value of potential hot pixel and compute ratio
    float pixel_value = average_texture[uint2(x, y)].r;
    float pixel_ratio = max(1.0, pixel_value - black_level) / max(1.0, sum - black_level);

    if (pixel_ratio >= hot_pixel_threshold && pixel_value >= 2 * mean_texture) {
        // Calculate weight for smooth transition
        float weight = 0.5f * correction_strength * min(2.0f,
            hot_pixel_multiplicator * (pixel_ratio - hot_pixel_threshold));
        hotpixel_weight_texture[uint2(x, y)] = weight;
    }
}

// =============================================================================
// PREPARE_TEXTURE_BAYER - Prepare Bayer texture (float conversion, hot pixel
// correction, exposure equalization, padding extension)
// =============================================================================
[numthreads(8, 8, 1)]
void prepare_texture_bayer(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint texture_width, texture_height;
    in_texture_uint.GetDimensions(texture_width, texture_height);

    int x = gid.x;
    int y = gid.y;

    float pixel_value = float(in_texture_uint[gid.xy].r);

    float hotpixel_weight = hotpixel_weight_texture[gid.xy].r;

    if (hotpixel_weight > 0.001f && x >= 2 && x < (int)texture_width - 2 &&
        y >= 2 && y < (int)texture_height - 2) {
        // Calculate mean of 4 surrounding values
        float sum = in_texture_uint[uint2(x - 2, y + 0)].r;
        sum += in_texture_uint[uint2(x + 2, y + 0)].r;
        sum += in_texture_uint[uint2(x + 0, y - 2)].r;
        sum += in_texture_uint[uint2(x + 0, y + 2)].r;

        // Blend values and replace hot pixel
        pixel_value = hotpixel_weight * 0.25f * sum + (1.0f - hotpixel_weight) * pixel_value;
    }

    // Calculate exposure correction factor
    float corr_factor = pow(2.0f, float(exposure_diff / 100.0f));
    float black_level = black_levels[(gid.y % 2) * 2 + (gid.x % 2)];

    // Correct exposure
    pixel_value = (pixel_value - black_level) * corr_factor + black_level;
    pixel_value = max(pixel_value, 0.0f);

    out_texture[uint2(gid.x + pad_left, gid.y + pad_top)] = pixel_value;
}

// =============================================================================
// PREPARE_TEXTURE_XTRANS - Prepare X-Trans texture (same as Bayer but for
// X-Trans pattern with 6x6 mosaic)
// =============================================================================
[numthreads(8, 8, 1)]
void prepare_texture_xtrans(uint3 gid : SV_DispatchThreadID)
{
    // Lookup table (same as find_hotpixels_xtrans)
    static const int offset[6][6][4][2] = {
        // Row 0
        {{{0, -1}, {1, -1}, {1, 0}, {-1, 1}},
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}},
         {{0, -1}, {1, -1}, {1, 0}, {-1, 1}},
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}}},
        // Row 1
        {{{-1, 2}, {-2, 0}, {-1, -2}, {2, -1}},
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}},
         {{2, -1}, {2, 1}, {-1, 2}, {-2, 0}},
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}}},
        // Row 2
        {{{1, 0}, {1, 1}, {0, 1}, {-1, 1}},
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}},
         {{1, 0}, {1, 1}, {0, 1}, {-1, 1}},
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}}},
        // Row 3
        {{{0, -1}, {1, -1}, {1, 0}, {-1, 1}},
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}},
         {{0, -1}, {1, -1}, {1, 0}, {-1, 1}},
         {{0, -1}, {1, 1}, {-1, 0}, {-1, -1}},
         {{1, -2}, {2, 1}, {0, 2}, {-2, 1}}},
        // Row 4
        {{{-1, 2}, {-2, 0}, {-1, -2}, {2, -1}},
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}},
         {{2, -1}, {2, 1}, {-1, 2}, {-2, 0}},
         {{1, -2}, {2, 0}, {1, 2}, {-2, 1}},
         {{1, -1}, {1, 1}, {-1, 1}, {-1, -1}}},
        // Row 5
        {{{1, 0}, {1, 1}, {0, 1}, {-1, 1}},
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}},
         {{1, 0}, {1, 1}, {0, 1}, {-1, 1}},
         {{1, -1}, {0, 1}, {-1, 1}, {-1, 0}},
         {{-2, -1}, {0, -2}, {2, -1}, {1, -2}}}
    };

    // Get texture dimensions
    uint texture_width, texture_height;
    in_texture_uint.GetDimensions(texture_width, texture_height);

    int x = gid.x;
    int y = gid.y;
    int ix = x % 6;
    int iy = y % 6;

    float pixel_value = float(in_texture_uint[gid.xy].r);

    float hotpixel_weight = hotpixel_weight_texture[gid.xy].r;

    if (hotpixel_weight > 0.001f && x >= 2 && x < (int)texture_width - 2 &&
        y >= 2 && y < (int)texture_height - 2) {
        float sum = 0.0;
        float total = 0.0;
        float weight = 0.0;
        int dx = 0;
        int dy = 0;

        for (int off = 0; off < 4; off++) {
            dx = offset[iy][ix][off][0];
            dy = offset[iy][ix][off][1];
            weight = 1.0 / sqrt(pow(float(dx), 2) + pow(float(dy), 2));

            total += weight;
            float val = in_texture_uint[uint2(x + dx, y + dy)].r;
            sum += weight * val;
        }
        sum /= total;

        // Blend values and replace hot pixel
        pixel_value = hotpixel_weight * 0.25f * sum + (1.0f - hotpixel_weight) * pixel_value;
    }

    float corr_factor = pow(2.0f, float(exposure_diff / 100.0f));
    float black_level = black_levels[ix + 6 * iy];

    pixel_value = (pixel_value - black_level) * corr_factor + black_level;
    pixel_value = max(pixel_value, 0.0f);

    out_texture[uint2(gid.x + pad_left, gid.y + pad_top)] = pixel_value;
}

// =============================================================================
// DIVIDE_BUFFER - Divide buffer values by divisor
// =============================================================================
[numthreads(256, 1, 1)]
void divide_buffer(uint3 gid : SV_DispatchThreadID)
{
    out_buffer[gid.x] = in_buffer[gid.x] / divisor;
}

// =============================================================================
// SUM_DIVIDE_BUFFER - Calculate sum of buffer, divide by divisor
// =============================================================================
[numthreads(1, 1, 1)]
void sum_divide_buffer(uint3 gid : SV_DispatchThreadID)
{
    for (int i = 0; i < buffer_size; i++) {
        out_buffer[0] += in_buffer[i];
    }
    out_buffer[0] /= divisor;
}

// =============================================================================
// NORMALIZE_TEXTURE - Normalize texture by normalization texture
// =============================================================================
[numthreads(8, 8, 1)]
void normalize_texture(uint3 gid : SV_DispatchThreadID)
{
    in_texture[gid.xy] = in_texture[gid.xy].r / (norm_texture[gid.xy].r + norm_scalar);
}

// =============================================================================
// SUM_RECT_COLUMNS_FLOAT - Sum columns in rectangle (float texture)
// Used for calculating texture_mean
// =============================================================================
[numthreads(8, 8, 1)]
void sum_rect_columns_float(uint3 gid : SV_DispatchThreadID)
{
    uint x = left + gid.x;

    float total = 0;
    for (int y = top + gid.y; y < bottom; y += mosaic_pattern_width) {
        total += in_texture[uint2(x, y)].r;
    }

    out_texture[gid.xy] = total;
}

// =============================================================================
// SUM_RECT_COLUMNS_UINT - Sum columns in rectangle (uint texture)
// Used for calculating black level from masked areas
// =============================================================================
[numthreads(8, 8, 1)]
void sum_rect_columns_uint(uint3 gid : SV_DispatchThreadID)
{
    uint x = left + gid.x;

    float total = 0;
    for (int y = top + gid.y; y < bottom; y += mosaic_pattern_width) {
        total += in_texture_uint[uint2(x, y)].r;
    }

    out_texture[gid.xy] = total;
}

// =============================================================================
// SUM_ROW - Sum texture values along rows
// =============================================================================
[numthreads(8, 8, 1)]
void sum_row(uint3 gid : SV_DispatchThreadID)
{
    float total = 0.0;

    for (int x = gid.x; x < width; x += mosaic_pattern_width) {
        total += in_texture[uint2(x, gid.y)].r;
    }

    out_buffer[gid.x + mosaic_pattern_width * gid.y] = total;
}

// =============================================================================
// UPSAMPLE_BILINEAR_FLOAT - Bilinear upsampling for float textures
// =============================================================================
[numthreads(8, 8, 1)]
void upsample_bilinear_float(uint3 gid : SV_DispatchThreadID)
{
    float x = float(gid.x) / scale_x;
    float y = float(gid.y) / scale_y;
    float epsilon = 1e-5;

    // Interpolate over x-axis
    float4 i1, i2;
    if (abs(x - round(x)) < epsilon) {
        i1 = float4(in_texture[uint2(round(x), floor(y))].r, 0, 0, 0);
        i2 = float4(in_texture[uint2(round(x), ceil(y))].r, 0, 0, 0);
    } else {
        float4 i11 = float4(in_texture[uint2(floor(x), floor(y))].r, 0, 0, 0);
        float4 i12 = float4(in_texture[uint2(floor(x), ceil(y))].r, 0, 0, 0);
        float4 i21 = float4(in_texture[uint2(ceil(x), floor(y))].r, 0, 0, 0);
        float4 i22 = float4(in_texture[uint2(ceil(x), ceil(y))].r, 0, 0, 0);
        i1 = (ceil(x) - x) * i11 + (x - floor(x)) * i21;
        i2 = (ceil(x) - x) * i12 + (x - floor(x)) * i22;
    }

    // Interpolate over y-axis
    float4 i;
    if (abs(y - round(y)) < epsilon) {
        i = i1;
    } else {
        i = (ceil(y) - y) * i1 + (y - floor(y)) * i2;
    }

    out_texture[gid.xy] = i.r;
}

// =============================================================================
// UPSAMPLE_NEAREST_INT - Nearest neighbor upsampling for int textures
// =============================================================================
[numthreads(8, 8, 1)]
void upsample_nearest_int(uint3 gid : SV_DispatchThreadID)
{
    int x = (int)round(float(gid.x) / scale_x);
    int y = (int)round(float(gid.y) / scale_y);

    int2 out_color = in_texture_int[uint2(x, y)];
    out_texture_int[gid.xy] = out_color;
}
