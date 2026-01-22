/*
 * Align.hlsl
 * Port of align.metal
 */

#include "Constants.hlsli"

// -------------------------------------------------------------------------
// Constant Buffers
// -------------------------------------------------------------------------

[[vk::binding(0, 0)]]
cbuffer AlignParams : register(b0)
{
    // avg_pool params
    int Scale;
    float BlackLevel;
    float FactorRed;
    float FactorGreen;
    float FactorBlue;

    // compute_tile_differences params
    int DownscaleFactor;
    int TileSize;
    int SearchDist;
    int WeightSSD;

    // warp params
    int HalfTileSize;
    int NumTilesX;
    int NumTilesY;

    // correct_upsampling_error params
    int UniformExposure;
};

// -------------------------------------------------------------------------
// Resources
// -------------------------------------------------------------------------
// NOTE: Using explicit [[vk::binding]] attributes to match C# descriptor layout.
// C# binds resources at: Binding 0 (b0), Binding 1 (t0), Binding 2 (t1), Binding 3 (t2), Binding 10 (u10).

// t0 -> Binding 1
[[vk::binding(1, 0)]]
Texture2D<float> InTexture : register(t0); // avg_pool, warp

[[vk::binding(1, 0)]]
Texture2D<float> RefTexture : register(t0); // compute_tile_diff, correct_upsample, find_best_tile (read tile_diff as t0?)

[[vk::binding(1, 0)]]
Texture3D<float> InTileDiff : register(t0); // find_best_tile_alignment (TileDiff is 3D input)

// t1 -> Binding 2
[[vk::binding(2, 0)]]
Texture2D<float> CompTexture : register(t1);

// t2 -> Binding 3
[[vk::binding(3, 0)]]
Texture2D<int4> PrevAlignment : register(t2);

// u10 -> Binding 10
[[vk::binding(10, 0)]]
RWTexture2D<float> OutTexture : register(u10); // avg_pool, warp

[[vk::binding(10, 0)]]
RWTexture3D<float> TileDiff : register(u10);   // compute_tile_differences

[[vk::binding(10, 0)]]
RWTexture2D<int4> OutAlignment : register(u10); // find_best_tile_alignment

[[vk::binding(10, 0)]]
RWTexture2D<int4> PrevAlignmentCorrected : register(u10); // correct_upsampling_error

// -------------------------------------------------------------------------
// Kernels
// -------------------------------------------------------------------------

// -------------------------------------------------------------------------
// avg_pool
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void avg_pool(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint width, height;
    OutTexture.GetDimensions(width, height);
    if (gid.x >= width || gid.y >= height) return;

    float out_pixel = 0;
    int x0 = gid.x * Scale;
    int y0 = gid.y * Scale;
    
    for (int dx = 0; dx < Scale; dx++) 
    {
        for (int dy = 0; dy < Scale; dy++) 
        {
            int x = x0 + dx;
            int y = y0 + dy;
            float val = InTexture.Load(int3(x, y, 0)).r;
            out_pixel += (val - BlackLevel);
        }
    }
    
    out_pixel /= (float)(Scale * Scale);
    OutTexture[gid] = out_pixel;
}

[numthreads(16, 16, 1)]
void avg_pool_normalization(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint width, height;
    OutTexture.GetDimensions(width, height);
    if (gid.x >= width || gid.y >= height) return;

    float out_pixel = 0;
    int x0 = gid.x * Scale;
    int y0 = gid.y * Scale;
    
    float norm_factors[4] = {FactorRed, FactorGreen, FactorGreen, FactorBlue};
    float mean_factor = 0.25f * (norm_factors[0] + norm_factors[1] + norm_factors[2] + norm_factors[3]);

    for (int dx = 0; dx < Scale; dx++) 
    {
        for (int dy = 0; dy < Scale; dy++) 
        {
            int x = x0 + dx;
            int y = y0 + dy;
            int idx = (dy * Scale + dx) % 4; 
            float val = InTexture.Load(int3(x, y, 0)).r;
            out_pixel += (mean_factor / norm_factors[idx] * val - BlackLevel);
        }
    }

    out_pixel /= (float)(Scale * Scale);
    OutTexture[gid] = out_pixel;
}

// -------------------------------------------------------------------------
// compute_tile_differences
// -------------------------------------------------------------------------
[numthreads(8, 8, 4)]
void compute_tile_differences(uint3 DTid : SV_DispatchThreadID)
{
    uint3 gid = DTid;
    uint refW, refH;
    RefTexture.GetDimensions(refW, refH);
    
    int n_pos_1d = 2 * SearchDist + 1;
    
    int x0 = gid.x * TileSize / 2;
    int y0 = gid.y * TileSize / 2;
    
    int dy0 = (int)(gid.z / n_pos_1d) - SearchDist;
    int dx0 = (int)(gid.z % n_pos_1d) - SearchDist;
    
    int4 prev_align = PrevAlignment.Load(int3(gid.x, gid.y, 0));
    dx0 += DownscaleFactor * prev_align.x;
    dy0 += DownscaleFactor * prev_align.y;
    
    float diff = 0;
    float w = (float)WeightSSD;

    for (int dx1 = 0; dx1 < TileSize; dx1++)
    {
        for (int dy1 = 0; dy1 < TileSize; dy1++)
        {
            int ref_tile_x = x0 + dx1;
            int ref_tile_y = y0 + dy1;
            int comp_tile_x = ref_tile_x + dx0;
            int comp_tile_y = ref_tile_y + dy0;
            
            float diff_abs;
            if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
            {
                float refVal = RefTexture.Load(int3(ref_tile_x, ref_tile_y, 0)).r;
                diff_abs = abs(refVal - 2.0f * FLOAT16_MIN_VAL);
            }
            else
            {
                float refVal = RefTexture.Load(int3(ref_tile_x, ref_tile_y, 0)).r;
                float compVal = CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r;
                diff_abs = abs(refVal - compVal);
            }
            diff += (1.0f - w) * diff_abs + w * diff_abs * diff_abs;
        }
    }
    
    TileDiff[gid] = diff;
}

// -------------------------------------------------------------------------
// compute_tile_differences25
// Highly-optimized function for search_distance == 2 (25 total combinations).
// Uses sliding 5-row buffer to reduce memory reads ~25x compared to generic.
// Dispatch: 2D (n_tiles_x, n_tiles_y) - each thread computes all 25 differences.
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void compute_tile_differences25(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint refW, refH;
    RefTexture.GetDimensions(refW, refH);
    
    int ref_tile_x, ref_tile_y, comp_tile_x, comp_tile_y, tmp_index, dx_i, dy_i;
    
    // compute tile position if previous alignment were 0
    int x0 = gid.x * TileSize / 2;
    int y0 = gid.y * TileSize / 2;
    
    // factor in previous alignment
    int4 prev_align = PrevAlignment.Load(int3(gid.x, gid.y, 0));
    int dx0 = DownscaleFactor * prev_align.x;
    int dy0 = DownscaleFactor * prev_align.y;
    
    float diff[25];
    for (int i = 0; i < 25; i++) diff[i] = 0.0f;
    
    float diff_abs0, diff_abs1;
    float tmp_ref0, tmp_ref1;
    float tmp_comp[5 * 68]; // 5 rows * (TileSize + 4)
    
    int buffer_width = TileSize + 4;
    float w = (float)WeightSSD;
    
    // loop over first 4 rows of comp_texture to initialize sliding buffer
    for (int dy = -2; dy < 2; dy++)
    {
        for (int dx = -2; dx < TileSize + 2; dx++)
        {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy;
            
            tmp_index = (dy + 2) * buffer_width + dx + 2;
            
            if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
            {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            }
            else
            {
                tmp_comp[tmp_index] = 0.5f * CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r;
            }
        }
    }
    
    // loop over rows of ref_texture
    for (int dy = 0; dy < TileSize; dy++)
    {
        // copy 1 additional row of comp_texture into sliding buffer
        for (int dx = -2; dx < TileSize + 2; dx++)
        {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy + 2;
            
            tmp_index = ((dy + 4) % 5) * buffer_width + dx + 2;
            
            if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
            {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            }
            else
            {
                tmp_comp[tmp_index] = 0.5f * CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r;
            }
        }
        
        // loop over columns of ref_texture (process 2 at a time)
        for (int dx = 0; dx < TileSize; dx += 2)
        {
            ref_tile_x = x0 + dx;
            ref_tile_y = y0 + dy;
            
            tmp_ref0 = RefTexture.Load(int3(ref_tile_x + 0, ref_tile_y, 0)).r;
            tmp_ref1 = RefTexture.Load(int3(ref_tile_x + 1, ref_tile_y, 0)).r;
            
            // loop over 25 test displacements
            for (int i = 0; i < 25; i++)
            {
                dx_i = i % 5;
                dy_i = i / 5;
                
                tmp_index = ((dy + dy_i) % 5) * buffer_width + dx + dx_i;
                
                diff_abs0 = abs(tmp_ref0 - 2.0f * tmp_comp[tmp_index + 0]);
                diff_abs1 = abs(tmp_ref1 - 2.0f * tmp_comp[tmp_index + 1]);
                
                diff[i] += ((1.0f - w) * (diff_abs0 + diff_abs1) + w * (diff_abs0 * diff_abs0 + diff_abs1 * diff_abs1));
            }
        }
    }
    
    // store tile differences in texture (note: axis order for 3D texture)
    for (int i = 0; i < 25; i++)
    {
        TileDiff[uint3(i, gid.x, gid.y)] = diff[i];
    }
}

// -------------------------------------------------------------------------
// compute_tile_differences_exposure25
// Same as compute_tile_differences25 but with exposure ratio correction.
// First computes intensity sums to calculate exposure ratio, then applies.
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void compute_tile_differences_exposure25(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint refW, refH;
    RefTexture.GetDimensions(refW, refH);
    
    int ref_tile_x, ref_tile_y, comp_tile_x, comp_tile_y, tmp_index, dx_i, dy_i;
    
    int x0 = gid.x * TileSize / 2;
    int y0 = gid.y * TileSize / 2;
    
    int4 prev_align = PrevAlignment.Load(int3(gid.x, gid.y, 0));
    int dx0 = DownscaleFactor * prev_align.x;
    int dy0 = DownscaleFactor * prev_align.y;
    
    float sum_u[25], sum_v[25], diff[25], ratio[25];
    for (int i = 0; i < 25; i++) { sum_u[i] = 0.0f; sum_v[i] = 0.0f; diff[i] = 0.0f; }
    
    float diff_abs0, diff_abs1;
    float tmp_ref0, tmp_ref1, tmp_comp_val0, tmp_comp_val1;
    float tmp_comp[5 * 68];
    
    int buffer_width = TileSize + 4;
    float w = (float)WeightSSD;
    
    // --- Pass 1: Compute exposure sums ---
    // Initialize first 4 rows
    for (int dy = -2; dy < 2; dy++)
    {
        for (int dx = -2; dx < TileSize + 2; dx++)
        {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy;
            tmp_index = (dy + 2) * buffer_width + dx + 2;
            
            if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
            {
                tmp_comp[tmp_index] = FLOAT16_MAX_VAL;
            }
            else
            {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, 0.5f * CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r);
            }
        }
    }
    
    // Compute sums
    for (int dy = 0; dy < TileSize; dy++)
    {
        for (int dx = -2; dx < TileSize + 2; dx++)
        {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy + 2;
            tmp_index = ((dy + 4) % 5) * buffer_width + dx + 2;
            
            if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
            {
                tmp_comp[tmp_index] = FLOAT16_MAX_VAL;
            }
            else
            {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, 0.5f * CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r);
            }
        }
        
        for (int dx = 0; dx < TileSize; dx += 2)
        {
            ref_tile_x = x0 + dx;
            ref_tile_y = y0 + dy;
            
            tmp_ref0 = max(FLOAT16_ZERO_VAL, RefTexture.Load(int3(ref_tile_x + 0, ref_tile_y, 0)).r);
            tmp_ref1 = max(FLOAT16_ZERO_VAL, RefTexture.Load(int3(ref_tile_x + 1, ref_tile_y, 0)).r);
            
            for (int i = 0; i < 25; i++)
            {
                dx_i = i % 5;
                dy_i = i / 5;
                tmp_index = ((dy + dy_i) % 5) * buffer_width + dx + dx_i;
                
                tmp_comp_val0 = tmp_comp[tmp_index + 0];
                tmp_comp_val1 = tmp_comp[tmp_index + 1];
                
                if (tmp_comp_val0 > -1.0f)
                {
                    sum_u[i] += tmp_ref0;
                    sum_v[i] += 2.0f * tmp_comp_val0;
                }
                if (tmp_comp_val1 > -1.0f)
                {
                    sum_u[i] += tmp_ref1;
                    sum_v[i] += 2.0f * tmp_comp_val1;
                }
            }
        }
    }
    
    // Calculate exposure ratios
    for (int i = 0; i < 25; i++)
    {
        ratio[i] = clamp(sum_u[i] / (sum_v[i] + 1e-9f), 0.9f, 1.1f);
    }
    
    // --- Pass 2: Compute differences with ratio correction ---
    // Re-initialize buffer
    for (int dy = -2; dy < 2; dy++)
    {
        for (int dx = -2; dx < TileSize + 2; dx++)
        {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy;
            tmp_index = (dy + 2) * buffer_width + dx + 2;
            
            if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
            {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            }
            else
            {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, 0.5f * CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r);
            }
        }
    }
    
    for (int dy = 0; dy < TileSize; dy++)
    {
        for (int dx = -2; dx < TileSize + 2; dx++)
        {
            comp_tile_x = x0 + dx0 + dx;
            comp_tile_y = y0 + dy0 + dy + 2;
            tmp_index = ((dy + 4) % 5) * buffer_width + dx + 2;
            
            if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
            {
                tmp_comp[tmp_index] = FLOAT16_MIN_VAL;
            }
            else
            {
                tmp_comp[tmp_index] = max(FLOAT16_ZERO_VAL, 0.5f * CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r);
            }
        }
        
        for (int dx = 0; dx < TileSize; dx += 2)
        {
            ref_tile_x = x0 + dx;
            ref_tile_y = y0 + dy;
            
            tmp_ref0 = max(FLOAT16_ZERO_VAL, RefTexture.Load(int3(ref_tile_x + 0, ref_tile_y, 0)).r);
            tmp_ref1 = max(FLOAT16_ZERO_VAL, RefTexture.Load(int3(ref_tile_x + 1, ref_tile_y, 0)).r);
            
            for (int i = 0; i < 25; i++)
            {
                dx_i = i % 5;
                dy_i = i / 5;
                tmp_index = ((dy + dy_i) % 5) * buffer_width + dx + dx_i;
                
                diff_abs0 = abs(tmp_ref0 - 2.0f * ratio[i] * tmp_comp[tmp_index + 0]);
                diff_abs1 = abs(tmp_ref1 - 2.0f * ratio[i] * tmp_comp[tmp_index + 1]);
                
                diff[i] += ((1.0f - w) * (diff_abs0 + diff_abs1) + w * (diff_abs0 * diff_abs0 + diff_abs1 * diff_abs1));
            }
        }
    }
    
    for (int i = 0; i < 25; i++)
    {
        TileDiff[uint3(i, gid.x, gid.y)] = diff[i];
    }
}

// -------------------------------------------------------------------------
// find_best_tile_alignment
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)] 
void find_best_tile_alignment(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    int n_pos_1d = 2 * SearchDist + 1;
    int n_pos_2d = n_pos_1d * n_pos_1d;
    
    float current_diff;
    float min_diff_val = 1e20f;
    int min_diff_idx = 0;
    
    for (int i = 0; i < n_pos_2d; i++) {
        current_diff = InTileDiff.Load(int4(i, gid.x, gid.y, 0)).r;
        if (current_diff < min_diff_val) {
            min_diff_val = current_diff;
            min_diff_idx = i;
        }
    }
    
    int dx = min_diff_idx % n_pos_1d - SearchDist;
    int dy = min_diff_idx / n_pos_1d - SearchDist;
    
    int4 prev_align = DownscaleFactor * PrevAlignment.Load(int3(gid.x, gid.y, 0));
    
    int4 outVal = int4(prev_align.x + dx, prev_align.y + dy, 0, 0);
    OutAlignment[gid] = outVal;
}

// -------------------------------------------------------------------------
// correct_upsampling_error
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void correct_upsampling_error(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint refW, refH;
    RefTexture.GetDimensions(refW, refH);
    
    int x0 = gid.x * TileSize / 2;
    int y0 = gid.y * TileSize / 2;
    
    int3 x_shift = int3(0, ((gid.x % 2 == 0) ? -1 : 1), 0);
    int3 y_shift = int3(0, 0, ((gid.y % 2 == 0) ? -1 : 1));
    
    int3 x_clamped = clamp(int3(gid.x, gid.x, gid.x) + x_shift, 0, NumTilesX - 1);
    int3 y_clamped = clamp(int3(gid.y, gid.y, gid.y) + y_shift, 0, NumTilesY - 1);
    
    int4 prev_align0 = PrevAlignment.Load(int3(x_clamped.x, y_clamped.x, 0));
    int4 prev_align1 = PrevAlignment.Load(int3(x_clamped.y, y_clamped.y, 0));
    int4 prev_align2 = PrevAlignment.Load(int3(x_clamped.z, y_clamped.z, 0));
    
    int3 dx0 = DownscaleFactor * int3(prev_align0.x, prev_align1.x, prev_align2.x);
    int3 dy0 = DownscaleFactor * int3(prev_align0.y, prev_align1.y, prev_align2.y);
    
    float diff[3] = {0.0f, 0.0f, 0.0f};
    float ratio[3] = {1.0f, 1.0f, 1.0f};
    
    float tmp_ref[64];
    
    if (UniformExposure != 1) 
    {
        float sum_u[3] = {0.0f, 0.0f, 0.0f};
        float sum_v[3] = {0.0f, 0.0f, 0.0f};
        
        for (int dy = 0; dy < TileSize; dy += 64/TileSize) 
        {
            for (int i = 0; i < 64; i++) {
                tmp_ref[i] = max(FLOAT16_ZERO_VAL, RefTexture.Load(int3(x0 + (i % TileSize), y0 + dy + (i / TileSize), 0)).r);
            }
            
            for (int c = 0; c < 3; c++) 
            {
                int tmp_tile_x = x0 + dx0[c];
                int tmp_tile_y = y0 + dy0[c] + dy;
                
                for (int i = 0; i < 64; i++) {
                    int comp_tile_x = tmp_tile_x + (i % TileSize);
                    int comp_tile_y = tmp_tile_y + (i / TileSize);
                    
                    if (comp_tile_x >= 0 && comp_tile_y >= 0 && comp_tile_x < (int)refW && comp_tile_y < (int)refH) {
                        sum_u[c] += tmp_ref[i];
                        sum_v[c] += max(FLOAT16_ZERO_VAL, CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r);
                    }
                }
            }
        }
        
        for (int c = 0; c < 3; c++) {
            ratio[c] = clamp(sum_u[c] / (sum_v[c] + 1e-9f), 0.9f, 1.1f);
        }
    }
    
    for (int dy = 0; dy < TileSize; dy += 64/TileSize) 
    {
        for (int i = 0; i < 64; i++) {
             tmp_ref[i] = RefTexture.Load(int3(x0 + (i % TileSize), y0 + dy + (i / TileSize), 0)).r;
        }
        
        for (int c = 0; c < 3; c++) 
        {
             int tmp_tile_x = x0 + dx0[c];
             int tmp_tile_y = y0 + dy0[c] + dy;
             
             for (int i = 0; i < 64; i++) {
                 int comp_tile_x = tmp_tile_x + (i % TileSize);
                 int comp_tile_y = tmp_tile_y + (i / TileSize);
                 
                 int weight_outside = 0;
                 if (comp_tile_x < 0 || comp_tile_y < 0 || comp_tile_x >= (int)refW || comp_tile_y >= (int)refH)
                     weight_outside = 1;
                 
                 float compVal = (weight_outside == 0) ? CompTexture.Load(int3(comp_tile_x, comp_tile_y, 0)).r : 0.0f;
                 
                 float diff_abs = abs(tmp_ref[i] - (1.0f - weight_outside) * ratio[c] * compVal - weight_outside * 2.0f * FLOAT16_MIN_VAL);
                 
                 float w = (float)WeightSSD;
                 diff[c] += (1.0f - w) * diff_abs + w * diff_abs * diff_abs;
             }
        }
    }
    
    if (diff[0] < diff[1] && diff[0] < diff[2]) {
        PrevAlignmentCorrected[gid] = prev_align0;
    } else if (diff[1] < diff[2]) {
         PrevAlignmentCorrected[gid] = prev_align1;
    } else {
         PrevAlignmentCorrected[gid] = prev_align2;
    }
}

// -------------------------------------------------------------------------
// upsample_alignment
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void upsample_alignment(uint3 DTid : SV_DispatchThreadID)
{
    // Upsample integer alignment vectors by factor of 2 (nearest neighbor)
    // Input: PrevAlignment (smaller), Output: OutAlignment (larger)
    // Coords in DTid are for the Output texture
    
    // Read from smaller texture at half coordinates
    // Using simple nearest neighbor (floor of coord / 2)
    int4 val = PrevAlignment.Load(int3(DTid.xy / 2, 0));
    
    // Write to output
    OutAlignment[DTid.xy] = val;
}

// -------------------------------------------------------------------------
// warp_texture_bayer
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void warp_texture_bayer(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    int x = (int)gid.x;
    int y = (int)gid.y;
    
    float half_tile_size_float = (float)HalfTileSize;
    
    float x_grid = (x + 0.5f) / half_tile_size_float - 1.0f;
    float y_grid = (y + 0.5f) / half_tile_size_float - 1.0f;
    
    int x_grid_floor = (int)(max(0.0f, floor(x_grid)) + 0.1f);
    int y_grid_floor = (int)(max(0.0f, floor(y_grid)) + 0.1f);
    int x_grid_ceil  = (int)(min(ceil(x_grid), (float)NumTilesX - 1.0f) + 0.1f);
    int y_grid_ceil  = (int)(min(ceil(y_grid), (float)NumTilesY - 1.0f) + 0.1f);
    
    float weight_x = ((x % HalfTileSize) + 0.5f) / (2.0f * half_tile_size_float);
    float weight_y = ((y % HalfTileSize) + 0.5f) / (2.0f * half_tile_size_float);
    
    int4 prev_align0 = DownscaleFactor * PrevAlignment.Load(int3(x_grid_floor, y_grid_floor, 0));
    int4 prev_align1 = DownscaleFactor * PrevAlignment.Load(int3(x_grid_ceil,  y_grid_floor, 0));
    int4 prev_align2 = DownscaleFactor * PrevAlignment.Load(int3(x_grid_floor, y_grid_ceil, 0));
    int4 prev_align3 = DownscaleFactor * PrevAlignment.Load(int3(x_grid_ceil,  y_grid_ceil, 0));
    
    float val0 = InTexture.Load(int3(x + prev_align0.x, y + prev_align0.y, 0)).r;
    float w0 = (1.0f - weight_x) * (1.0f - weight_y);
    
    float val1 = InTexture.Load(int3(x + prev_align1.x, y + prev_align1.y, 0)).r;
    float w1 = weight_x * (1.0f - weight_y);
    
    float val2 = InTexture.Load(int3(x + prev_align2.x, y + prev_align2.y, 0)).r;
    float w2 = (1.0f - weight_x) * weight_y;
    
    float val3 = InTexture.Load(int3(x + prev_align3.x, y + prev_align3.y, 0)).r;
    float w3 = weight_x * weight_y;
    
    float pixel_value = val0 * w0 + val1 * w1 + val2 * w2 + val3 * w3;
    float total_weight = w0 + w1 + w2 + w3;
    
    OutTexture[gid] = pixel_value / total_weight;
}

// -------------------------------------------------------------------------
// warp_texture_xtrans
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void warp_texture_xtrans(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint width, height;
    InTexture.GetDimensions(width, height);
    
    int texture_width = (int)width;
    int texture_height = (int)height;
    int tile_half_size = HalfTileSize; // Assuming TileSize/2 passed as HalfTileSize? 
    // Metal param: constant int& tile_size [[buffer(1)]]
    // My CBuffer has TileSize and HalfTileSize. 
    // warp_texture_xtrans uses `tile_size` in metal for `tile_half_size = tile_size/2`?
    // Metal code: `int tile_half_size = tile_size / 2;`
    // I can use TileSize / 2 or HalfTileSize.
    
    // load args
    int x1_pix = (int)gid.x;
    int y1_pix = (int)gid.y;
    
    // compute grid coords
    float x1_grid = (float)(x1_pix - tile_half_size) / (float)(texture_width  - TileSize - 1) * (float)(NumTilesX - 1);
    float y1_grid = (float)(y1_pix - tile_half_size) / (float)(texture_height - TileSize - 1) * (float)(NumTilesY - 1);
    
    int x_grid_list[4] = { (int)floor(x1_grid), (int)floor(x1_grid), (int)ceil(x1_grid), (int)ceil(x1_grid) };
    int y_grid_list[4] = { (int)floor(y1_grid), (int)ceil(y1_grid), (int)floor(y1_grid), (int)ceil(y1_grid) };
    
    float total_intensity = 0;
    float total_weight = 0;
    
    for (int i = 0; i < 4; i++) {
        int x_grid = x_grid_list[i];
        int y_grid = y_grid_list[i];
        
        // compute pixel center of ref tile
        int x0_pix = (int)floor( (float)tile_half_size + (float)x_grid/(float)(NumTilesX-1) * (float)(texture_width - TileSize - 1) );
        int y0_pix = (int)floor( (float)tile_half_size + (float)y_grid/(float)(NumTilesY-1) * (float)(texture_height - TileSize - 1) );
        
        if (abs(x1_pix - x0_pix) <= tile_half_size && abs(y1_pix - y0_pix) <= tile_half_size) {
            
            // Check bounds for prev_alignment
            // Metal doesn't explicitly check? 
            int4 prev_align = PrevAlignment.Load(int3(x_grid, y_grid, 0)); 
            
            int dx = DownscaleFactor * prev_align.x;
            int dy = DownscaleFactor * prev_align.y;
            
            int x2_pix = x1_pix + dx;
            int y2_pix = y1_pix + dy;
            
            int dist_x = abs(x1_pix - x0_pix);
            int dist_y = abs(y1_pix - y0_pix);
            float weight_x = (float)(TileSize - dist_x - dist_y);
            float weight_y = (float)(TileSize - dist_x - dist_y);
            float curr_weight = weight_x * weight_y; // Metal: weight_x * weight_y
            
            total_weight += curr_weight;
            total_intensity += curr_weight * InTexture.Load(int3(x2_pix, y2_pix, 0)).r;
        }
    }
    
    OutTexture[gid] = total_intensity / total_weight;
}
