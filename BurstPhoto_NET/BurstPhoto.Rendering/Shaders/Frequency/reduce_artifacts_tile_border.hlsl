/*
 * reduce_artifacts_tile_border.hlsl
 * Reduces artifacts at tile borders using cosine windowing
 *
 * REWRITTEN to match Swift Metal implementation in frequency.metal exactly:
 * - Dispatch by TILE COUNT (n_tiles_x, n_tiles_y), NOT pixel dimensions
 * - Each thread processes ALL pixels in one tile via nested loops
 * - gid.xy is the TILE INDEX, not pixel coordinate
 */

#include "FrequencyCommon.hlsli"

#define UINT16_MAX_VAL 65535.0f

// DEBUG MODE:
// 0 = Normal operation with border blending
// 1 = Output RefTexture directly (to verify GPU can read it)
// 2 = Output difference between RefTexture and OutputTexture
// 3 = Clamp only (no border blending) - for testing
#define DEBUG_MODE 0

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    // gid is TILE INDEX (matches Swift: gid [[thread_position_in_grid]])
    uint2 gid = DTid.xy;
    int ts = TileSize;

    // Compute tile top-left corner from tile index (matches Swift: x0 = gid.x * tile_size)
    int x0 = gid.x * ts;
    int y0 = gid.y * ts;

    // Set min values and max values (per-channel black levels)
    // Matches Swift: float4 const min_values = float4(black_level0-1.0f, ...)
    float4 min_values = float4(
        (float)(BlackLevel0 - 1),
        (float)(BlackLevel1 - 1),
        (float)(BlackLevel2 - 1),
        (float)(BlackLevel3 - 1)
    );
    float4 max_values = float4(UINT16_MAX_VAL, UINT16_MAX_VAL, UINT16_MAX_VAL, UINT16_MAX_VAL);

    // Pre-calculate factor for cosine calculation (matches Swift: angle = -2*PI/float(tile_size))
    float angle = -2.0f * PI / (float)ts;

    float4 pixel_value;
    float norm_cosine;

    // Process ALL pixels in this tile via nested loops (matches Swift exactly)
    for (int dy = 0; dy < ts; dy++) {
        for (int dx = 0; dx < ts; dx++) {
            int x = x0 + dx;
            int y = y0 + dy;

            // Calculate modified raised cosine window weight for blending tiles to suppress artifacts
            // See section "Overlapped tiles" in https://graphics.stanford.edu/papers/hdrp/hasinoff-hdrplus-sigasia16.pdf
            // Matches Swift: norm_cosine = (0.5f-0.5f*cos(-angle*(dx+0.5f)))*(0.5f-0.5f*cos(-angle*(dy+0.5f)));
            norm_cosine = (0.5f - 0.5f * cos(-angle * (dx + 0.5f))) * (0.5f - 0.5f * cos(-angle * (dy + 0.5f)));

            // Extract RGBA pixel values (matches Swift: pixel_value = out_texture.read(uint2(x, y)))
            pixel_value = OutputTexture[uint2(x, y)];

#if DEBUG_MODE == 1
            // DEBUG: Output RefTexture directly to verify GPU can read it
            float4 refP = RefTexture.Load(int3(x, y, 0));
            OutputTexture[uint2(x, y)] = refP;
#elif DEBUG_MODE == 2
            // DEBUG: Output difference between RefTexture and OutputTexture
            float4 refP = RefTexture.Load(int3(x, y, 0));
            OutputTexture[uint2(x, y)] = abs(refP - pixel_value);
#elif DEBUG_MODE == 3
            // Clamp only - no border blending
            pixel_value = clamp(pixel_value, norm_cosine * min_values, max_values);
            OutputTexture[uint2(x, y)] = pixel_value;
#else
            // Normal operation (MODE 0)
            // Clamp values - this reduces potential artifacts (black lines) at tile borders
            // by removing pixels with negative entries (negative when black level is subtracted)
            // Matches Swift: pixel_value = clamp(pixel_value, norm_cosine*min_values, max_values);
            pixel_value = clamp(pixel_value, norm_cosine * min_values, max_values);

            // Border blending with reference texture (matches Swift frequency.metal lines 447-450)
            // At tile borders, blend with the reference texture weighted by the cosine window
            if (dx == 0 || dx == ts - 1 || dy == 0 || dy == ts - 1) {
                float4 refP = RefTexture.Load(int3(x, y, 0));
                pixel_value = 0.5f * (norm_cosine * refP + pixel_value);
            }

            // Write back (matches Swift: out_texture.write(pixel_value, uint2(x, y)))
            OutputTexture[uint2(x, y)] = pixel_value;
#endif
        }
    }
}
