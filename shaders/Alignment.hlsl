// Alignment compute shaders - converted from Metal to HLSL
// Original: burstphoto/align/align.metal

// Constants (from ../misc/constants.h in Metal code)
#define FLOAT16_MIN_VAL -65504.0f
#define FLOAT16_05_VAL 0.5f

// Textures and buffers
RWTexture2D<float> in_texture : register(u0);
RWTexture2D<float> out_texture : register(u1);
RWTexture2D<float> ref_texture : register(u2);
RWTexture2D<float> comp_texture : register(u3);
RWTexture2D<int2> prev_alignment : register(u4);
RWTexture3D<float> tile_diff : register(u5);

// Constant buffers
cbuffer Params : register(b0)
{
    int scale;
    float black_level;
    int downscale_factor;
    int tile_size;
    int search_dist;
    int weight_ssd;
    float factor_red;
    float factor_green;
    float factor_blue;
    int texture_width;
    int texture_height;
};

// =============================================================================
// AVG_POOL - Average pooling with downscaling
// =============================================================================
[numthreads(8, 8, 1)]
void avg_pool(uint3 gid : SV_DispatchThreadID)
{
    float out_pixel = 0;
    int x0 = gid.x * scale;
    int y0 = gid.y * scale;

    for (int dx = 0; dx < scale; dx++) {
        for (int dy = 0; dy < scale; dy++) {
            int x = x0 + dx;
            int y = y0 + dy;
            out_pixel += (in_texture[uint2(x, y)].r - black_level);
        }
    }

    out_pixel /= (scale * scale);
    out_texture[gid.xy] = out_pixel;
}

// =============================================================================
// AVG_POOL_NORMALIZATION - Average pooling with color normalization
// =============================================================================
[numthreads(8, 8, 1)]
void avg_pool_normalization(uint3 gid : SV_DispatchThreadID)
{
    float out_pixel = 0;
    int x0 = gid.x * scale;
    int y0 = gid.y * scale;

    float norm_factors[4] = {factor_red, factor_green, factor_green, factor_blue};
    float mean_factor = 0.25f * (norm_factors[0] + norm_factors[1] + norm_factors[2] + norm_factors[3]);

    for (int dx = 0; dx < scale; dx++) {
        for (int dy = 0; dy < scale; dy++) {
            int x = x0 + dx;
            int y = y0 + dy;
            out_pixel += (mean_factor / norm_factors[dy*scale+dx] * in_texture[uint2(x, y)].r - black_level);
        }
    }

    out_pixel /= (scale * scale);
    out_texture[gid.xy] = out_pixel;
}

// =============================================================================
// COMPUTE_TILE_DIFFERENCES - Generic tile difference computation
// =============================================================================
[numthreads(4, 4, 4)]
void compute_tile_differences(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint tex_width, tex_height;
    ref_texture.GetDimensions(tex_width, tex_height);

    int n_pos_1d = 2 * search_dist + 1;
    float diff_abs;

    // Compute tile position if previous alignment were 0
    int x0 = gid.x * tile_size / 2;
    int y0 = gid.y * tile_size / 2;

    // Compute current tile displacement based on thread index
    int dy0 = gid.z / n_pos_1d - search_dist;
    int dx0 = gid.z % n_pos_1d - search_dist;

    // Factor in previous alignment
    int2 prev_align = prev_alignment[gid.xy];
    dx0 += downscale_factor * prev_align.x;
    dy0 += downscale_factor * prev_align.y;

    // Compute tile difference
    float diff = 0;
    for (int dx1 = 0; dx1 < tile_size; dx1++) {
        for (int dy1 = 0; dy1 < tile_size; dy1++) {
            // Compute the indices of the pixels to compare
            int ref_tile_x = x0 + dx1;
            int ref_tile_y = y0 + dy1;
            int comp_tile_x = ref_tile_x + dx0;
            int comp_tile_y = ref_tile_y + dy0;

            // If the comparison pixels are outside of the frame, attach a high loss
            if ((comp_tile_x < 0) || (comp_tile_y < 0) ||
                (comp_tile_x >= (int)tex_width) || (comp_tile_y >= (int)tex_height)) {
                diff_abs = abs(ref_texture[uint2(ref_tile_x, ref_tile_y)].r - 2 * FLOAT16_MIN_VAL);
            } else {
                diff_abs = abs(ref_texture[uint2(ref_tile_x, ref_tile_y)].r -
                               comp_texture[uint2(comp_tile_x, comp_tile_y)].r);
            }

            diff += (1 - weight_ssd) * diff_abs + weight_ssd * diff_abs * diff_abs;
        }
    }

    // Store tile difference
    tile_diff[gid] = diff;
}

// =============================================================================
// FIND_BEST_TILE_ALIGNMENT - Find minimum difference among all candidates
// =============================================================================
RWTexture2D<int2> current_alignment : register(u6);

[numthreads(8, 8, 1)]
void find_best_tile_alignment(uint3 gid : SV_DispatchThreadID)
{
    int n_pos_1d = 2 * search_dist + 1;
    int n_pos_2d = n_pos_1d * n_pos_1d;

    float min_diff = 1e10;
    int best_dx = 0;
    int best_dy = 0;

    // Search through all displacement candidates
    for (int i = 0; i < n_pos_2d; i++) {
        float diff = tile_diff[uint3(i, gid.x, gid.y)];
        if (diff < min_diff) {
            min_diff = diff;
            int dy = i / n_pos_1d - search_dist;
            int dx = i % n_pos_1d - search_dist;
            best_dx = dx;
            best_dy = dy;
        }
    }

    // Add to previous alignment
    int2 prev_align = prev_alignment[gid.xy];
    current_alignment[gid.xy] = int2(
        prev_align.x + best_dx,
        prev_align.y + best_dy
    );
}

// =============================================================================
// WARP_TEXTURE_BAYER - Warp texture using alignment vectors (Bayer pattern)
// =============================================================================
RWTexture2D<float> texture_to_warp : register(u7);
RWTexture2D<float> warped_texture : register(u8);
RWTexture2D<int2> alignment : register(u9);

cbuffer WarpParams : register(b1)
{
    int warp_tile_size;
    int n_tiles_x;
    int n_tiles_y;
};

[numthreads(8, 8, 1)]
void warp_texture_bayer(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint tex_width, tex_height;
    texture_to_warp.GetDimensions(tex_width, tex_height);

    if (gid.x >= tex_width || gid.y >= tex_height)
        return;

    // Find which tile this pixel belongs to
    int tile_x = (gid.x * 2) / warp_tile_size;
    int tile_y = (gid.y * 2) / warp_tile_size;

    // Clamp to valid tile range
    tile_x = min(tile_x, n_tiles_x - 1);
    tile_y = min(tile_y, n_tiles_y - 1);

    // Get alignment for this tile
    int2 align = alignment[uint2(tile_x, tile_y)];

    // Compute source position
    int src_x = (int)gid.x + align.x;
    int src_y = (int)gid.y + align.y;

    // Clamp to texture bounds
    src_x = clamp(src_x, 0, (int)tex_width - 1);
    src_y = clamp(src_y, 0, (int)tex_height - 1);

    // Copy pixel
    warped_texture[gid.xy] = texture_to_warp[uint2(src_x, src_y)];
}

// =============================================================================
// CORRECT_UPSAMPLING_ERROR - Test 3 alignment candidates to correct errors
// =============================================================================
RWTexture2D<int2> prev_alignment_corrected : register(u10);

[numthreads(8, 8, 1)]
void correct_upsampling_error(uint3 gid : SV_DispatchThreadID)
{
    if (gid.x >= (uint)n_tiles_x || gid.y >= (uint)n_tiles_y)
        return;

    int2 prev_align = prev_alignment[gid.xy];

    // Test 3 candidates: (2*prev), (2*prev + (1,0)), (2*prev + (0,1))
    int2 candidates[3];
    candidates[0] = 2 * prev_align;
    candidates[1] = 2 * prev_align + int2(1, 0);
    candidates[2] = 2 * prev_align + int2(0, 1);

    // Compute tile position
    int x0 = gid.x * tile_size / 2;
    int y0 = gid.y * tile_size / 2;

    float min_diff = 1e10;
    int2 best_align = candidates[0];

    // Test each candidate
    for (int c = 0; c < 3; c++) {
        int dx0 = candidates[c].x;
        int dy0 = candidates[c].y;

        float diff = 0;
        for (int dx = 0; dx < tile_size; dx++) {
            for (int dy = 0; dy < tile_size; dy++) {
                int ref_x = x0 + dx;
                int ref_y = y0 + dy;
                int comp_x = ref_x + dx0;
                int comp_y = ref_y + dy0;

                uint tex_w, tex_h;
                ref_texture.GetDimensions(tex_w, tex_h);

                float diff_val;
                if (comp_x < 0 || comp_y < 0 || comp_x >= (int)tex_w || comp_y >= (int)tex_h) {
                    diff_val = abs(ref_texture[uint2(ref_x, ref_y)].r);
                } else {
                    diff_val = abs(ref_texture[uint2(ref_x, ref_y)].r -
                                   comp_texture[uint2(comp_x, comp_y)].r);
                }

                diff += weight_ssd ? (diff_val * diff_val) : diff_val;
            }
        }

        if (diff < min_diff) {
            min_diff = diff;
            best_align = candidates[c];
        }
    }

    prev_alignment_corrected[gid.xy] = best_align;
}

// =============================================================================
// COMPUTE_TILE_DIFFERENCES25 - Optimized for search_distance == 2 (25 combinations)
// Highly-optimized function that reduces memory accesses by using a sliding
// buffer vector covering 5 complete rows of the comparison texture
// =============================================================================
[numthreads(8, 8, 1)]
void compute_tile_differences25(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint texture_width, texture_height;
    ref_texture.GetDimensions(texture_width, texture_height);

    int ref_tile_x, ref_tile_y, comp_tile_x, comp_tile_y, tmp_index, dx_i, dy_i;

    // Compute tile position if previous alignment were 0
    int x0 = gid.x * tile_size / 2;
    int y0 = gid.y * tile_size / 2;

    // Factor in previous alignment
    int2 prev_align = prev_alignment[gid.xy];
    int dx0 = downscale_factor * prev_align.x;
    int dy0 = downscale_factor * prev_align.y;

    float diff[25];
    for (int i = 0; i < 25; i++) diff[i] = 0;

    float diff_abs0, diff_abs1;
    float tmp_ref0, tmp_ref1;
    float tmp_comp[5 * 68]; // Buffer for 5 rows with tile_size + 4 columns each

    // Loop over first 4 rows of comp_texture
    for (int dy = -2; dy < +2; dy++) {
        // Loop over columns to copy first 4 rows into tmp_comp
        for (int dx = -2; dx < tile_size + 2; dx++) {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy;

            // Index in tmp_comp
            tmp_index = (dy + 2) * (tile_size + 4) + dx + 2;

            // If outside frame, use minimum value
            if ((comp_tile_x < 0) || (comp_tile_y < 0) ||
                (comp_tile_x >= (int)texture_width) || (comp_tile_y >= (int)texture_height)) {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            } else {
                tmp_comp[tmp_index] = FLOAT16_05_VAL * comp_texture[uint2(comp_tile_x, comp_tile_y)].r;
            }
        }
    }

    // Loop over rows of ref_texture
    for (int dy = 0; dy < tile_size; dy++) {
        // Copy 1 additional row of comp_texture into tmp_comp (sliding buffer)
        for (int dx = -2; dx < tile_size + 2; dx++) {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy + 2;

            // Circular buffer index
            tmp_index = ((dy + 4) % 5) * (tile_size + 4) + dx + 2;

            if ((comp_tile_x < 0) || (comp_tile_y < 0) ||
                (comp_tile_x >= (int)texture_width) || (comp_tile_y >= (int)texture_height)) {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            } else {
                tmp_comp[tmp_index] = FLOAT16_05_VAL * comp_texture[uint2(comp_tile_x, comp_tile_y)].r;
            }
        }

        // Loop over columns of ref_texture (process 2 pixels at a time)
        for (int dx = 0; dx < tile_size; dx += 2) {
            ref_tile_x = x0 + dx;
            ref_tile_y = y0 + dy;

            tmp_ref0 = ref_texture[uint2(ref_tile_x + 0, ref_tile_y)].r;
            tmp_ref1 = ref_texture[uint2(ref_tile_x + 1, ref_tile_y)].r;

            // Loop over 25 test displacements
            for (int i = 0; i < 25; i++) {
                dx_i = i % 5;
                dy_i = i / 5;

                // Index in tmp_comp (circular buffer)
                tmp_index = ((dy + dy_i) % 5) * (tile_size + 4) + dx + dx_i;

                diff_abs0 = abs(tmp_ref0 - 2.0f * tmp_comp[tmp_index + 0]);
                diff_abs1 = abs(tmp_ref1 - 2.0f * tmp_comp[tmp_index + 1]);

                // Add difference to corresponding combination
                diff[i] += ((1 - weight_ssd) * (diff_abs0 + diff_abs1) +
                           weight_ssd * (diff_abs0 * diff_abs0 + diff_abs1 * diff_abs1));
            }
        }
    }

    // Store tile differences in texture
    for (int i = 0; i < 25; i++) {
        tile_diff[uint3(i, gid.x, gid.y)] = diff[i];
    }
}

// =============================================================================
// COMPUTE_TILE_DIFFERENCES_EXPOSURE25 - With exposure correction (25 combinations)
// Optimized function that scales pixel intensities by the ratio of mean values
// of both tiles to handle slight exposure differences
// =============================================================================
[numthreads(8, 8, 1)]
void compute_tile_differences_exposure25(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint texture_width, texture_height;
    ref_texture.GetDimensions(texture_width, texture_height);

    int ref_tile_x, ref_tile_y, comp_tile_x, comp_tile_y, tmp_index, dx_i, dy_i;

    // Compute tile position
    int x0 = gid.x * tile_size / 2;
    int y0 = gid.y * tile_size / 2;

    // Factor in previous alignment
    int2 prev_align = prev_alignment[gid.xy];
    int dx0 = downscale_factor * prev_align.x;
    int dy0 = downscale_factor * prev_align.y;

    float sum_u[25], sum_v[25], diff[25], ratio[25];
    for (int i = 0; i < 25; i++) {
        sum_u[i] = 0;
        sum_v[i] = 0;
        diff[i] = 0;
    }

    float diff_abs0, diff_abs1;
    float tmp_ref0, tmp_ref1, tmp_comp_val0, tmp_comp_val1;
    float tmp_comp[5 * 68];

    // First pass: Calculate sums for exposure ratio estimation
    // Loop over first 4 rows
    for (int dy = -2; dy < +2; dy++) {
        for (int dx = -2; dx < tile_size + 2; dx++) {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy;
            tmp_index = (dy + 2) * (tile_size + 4) + dx + 2;

            if ((comp_tile_x < 0) || (comp_tile_y < 0) ||
                (comp_tile_x >= (int)texture_width) || (comp_tile_y >= (int)texture_height)) {
                tmp_comp[tmp_index] = FLOAT16_MAX_VAL;
            } else {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, FLOAT16_05_VAL * comp_texture[uint2(comp_tile_x, comp_tile_y)].r);
            }
        }
    }

    // Calculate sums for exposure ratio
    for (int dy = 0; dy < tile_size; dy++) {
        // Copy additional row
        for (int dx = -2; dx < tile_size + 2; dx++) {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy + 2;
            tmp_index = ((dy + 4) % 5) * (tile_size + 4) + dx + 2;

            if ((comp_tile_x < 0) || (comp_tile_y < 0) ||
                (comp_tile_x >= (int)texture_width) || (comp_tile_y >= (int)texture_height)) {
                tmp_comp[tmp_index] = FLOAT16_MAX_VAL;
            } else {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, FLOAT16_05_VAL * comp_texture[uint2(comp_tile_x, comp_tile_y)].r);
            }
        }

        for (int dx = 0; dx < tile_size; dx += 2) {
            ref_tile_x = x0 + dx;
            ref_tile_y = y0 + dy;

            tmp_ref0 = max(FLOAT16_ZERO_VAL, ref_texture[uint2(ref_tile_x + 0, ref_tile_y)].r);
            tmp_ref1 = max(FLOAT16_ZERO_VAL, ref_texture[uint2(ref_tile_x + 1, ref_tile_y)].r);

            for (int i = 0; i < 25; i++) {
                dx_i = i % 5;
                dy_i = i / 5;
                tmp_index = ((dy + dy_i) % 5) * (tile_size + 4) + dx + dx_i;

                tmp_comp_val0 = tmp_comp[tmp_index + 0];
                tmp_comp_val1 = tmp_comp[tmp_index + 1];

                if (tmp_comp_val0 > -1) {
                    sum_u[i] += tmp_ref0;
                    sum_v[i] += 2.0f * tmp_comp_val0;
                }

                if (tmp_comp_val1 > -1) {
                    sum_u[i] += tmp_ref1;
                    sum_v[i] += 2.0f * tmp_comp_val1;
                }
            }
        }
    }

    // Calculate exposure ratio for each displacement
    for (int i = 0; i < 25; i++) {
        ratio[i] = clamp(sum_u[i] / (sum_v[i] + 1e-9), 0.9f, 1.1f);
    }

    // Second pass: Calculate differences with exposure correction
    // Reload comparison texture
    for (int dy = -2; dy < +2; dy++) {
        for (int dx = -2; dx < tile_size + 2; dx++) {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy;
            tmp_index = (dy + 2) * (tile_size + 4) + dx + 2;

            if ((comp_tile_x < 0) || (comp_tile_y < 0) ||
                (comp_tile_x >= (int)texture_width) || (comp_tile_y >= (int)texture_height)) {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            } else {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, FLOAT16_05_VAL * comp_texture[uint2(comp_tile_x, comp_tile_y)].r);
            }
        }
    }

    for (int dy = 0; dy < tile_size; dy++) {
        for (int dx = -2; dx < tile_size + 2; dx++) {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy + 2;
            tmp_index = ((dy + 4) % 5) * (tile_size + 4) + dx + 2;

            if ((comp_tile_x < 0) || (comp_tile_y < 0) ||
                (comp_tile_x >= (int)texture_width) || (comp_tile_y >= (int)texture_height)) {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            } else {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, FLOAT16_05_VAL * comp_texture[uint2(comp_tile_x, comp_tile_y)].r);
            }
        }

        for (int dx = 0; dx < tile_size; dx += 2) {
            ref_tile_x = x0 + dx;
            ref_tile_y = y0 + dy;

            tmp_ref0 = max(FLOAT16_ZERO_VAL, ref_texture[uint2(ref_tile_x + 0, ref_tile_y)].r);
            tmp_ref1 = max(FLOAT16_ZERO_VAL, ref_texture[uint2(ref_tile_x + 1, ref_tile_y)].r);

            for (int i = 0; i < 25; i++) {
                dx_i = i % 5;
                dy_i = i / 5;
                tmp_index = ((dy + dy_i) % 5) * (tile_size + 4) + dx + dx_i;

                diff_abs0 = abs(tmp_ref0 - 2.0f * ratio[i] * tmp_comp[tmp_index + 0]);
                diff_abs1 = abs(tmp_ref1 - 2.0f * ratio[i] * tmp_comp[tmp_index + 1]);

                diff[i] += ((1 - weight_ssd) * (diff_abs0 + diff_abs1) +
                           weight_ssd * (diff_abs0 * diff_abs0 + diff_abs1 * diff_abs1));
            }
        }
    }

    // Store tile differences
    for (int i = 0; i < 25; i++) {
        tile_diff[uint3(i, gid.x, gid.y)] = diff[i];
    }
}

// =============================================================================
// WARP_TEXTURE_XTRANS - Warp texture for X-Trans sensor pattern
// Uses weighted blending of alignment vectors from multiple overlapping tiles
// =============================================================================
[numthreads(8, 8, 1)]
void warp_texture_xtrans(uint3 gid : SV_DispatchThreadID)
{
    // Get texture dimensions
    uint texture_width, texture_height;
    texture_to_warp.GetDimensions(texture_width, texture_height);

    int tile_half_size = warp_tile_size / 2;

    // Load coordinates of output pixel
    int x1_pix = gid.x;
    int y1_pix = gid.y;

    // Compute the coordinates of output pixel in tile-grid units
    float x1_grid = float(x1_pix - tile_half_size) / float(texture_width - warp_tile_size - 1) * (n_tiles_x - 1);
    float y1_grid = float(y1_pix - tile_half_size) / float(texture_height - warp_tile_size - 1) * (n_tiles_y - 1);

    // Compute the two possible tile-grid indices that the output pixel belongs to
    int x_grid_list[4] = {(int)floor(x1_grid), (int)floor(x1_grid), (int)ceil(x1_grid), (int)ceil(x1_grid)};
    int y_grid_list[4] = {(int)floor(y1_grid), (int)ceil(y1_grid), (int)floor(y1_grid), (int)ceil(y1_grid)};

    // Loop over the four possible tile-grid indices
    float total_intensity = 0;
    float total_weight = 0;

    for (int i = 0; i < 4; i++) {
        int x_grid = x_grid_list[i];
        int y_grid = y_grid_list[i];

        // Compute pixel coordinates of the center of the reference tile
        int x0_pix = (int)floor(tile_half_size + float(x_grid) / float(n_tiles_x - 1) *
                     (texture_width - warp_tile_size - 1));
        int y0_pix = (int)floor(tile_half_size + float(y_grid) / float(n_tiles_y - 1) *
                     (texture_height - warp_tile_size - 1));

        // Check that the output pixel falls within the reference tile
        if ((abs(x1_pix - x0_pix) <= tile_half_size) && (abs(y1_pix - y0_pix) <= tile_half_size)) {
            // Compute tile displacement
            int2 prev_align = alignment[uint2(x_grid, y_grid)];
            int dx = downscale_factor * prev_align.x;
            int dy = downscale_factor * prev_align.y;

            // Load coordinates of the corresponding pixel from the comparison tile
            int x2_pix = x1_pix + dx;
            int y2_pix = y1_pix + dy;

            // Compute the weight of the aligned pixel (based on distance from tile center)
            int dist_x = abs(x1_pix - x0_pix);
            int dist_y = abs(y1_pix - y0_pix);
            float weight_x = warp_tile_size - dist_x - dist_y;
            float weight_y = warp_tile_size - dist_x - dist_y;
            float curr_weight = weight_x * weight_y;
            total_weight += curr_weight;

            // Add pixel value to the output
            total_intensity += curr_weight * texture_to_warp[uint2(x2_pix, y2_pix)].r;
        }
    }

    // Write output pixel
    float out_intensity = total_intensity / total_weight;
    warped_texture[gid.xy] = out_intensity;
}
