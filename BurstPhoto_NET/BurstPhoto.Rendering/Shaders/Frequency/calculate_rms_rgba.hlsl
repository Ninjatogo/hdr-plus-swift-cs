/*
 * calculate_rms_rgba.hlsl
 * Calculates per-tile mean values for signal-dependent noise estimation.
 *
 * For the Poisson-Gaussian noise model (variance = alpha*signal + beta), we need the
 * mean signal level per tile to estimate local noise variance.
 *
 * This shader computes the mean of each tile (TileSize x TileSize pixels)
 * and outputs to a tile-grid sized texture.
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    // DTid.xy is the tile index, not pixel position
    uint2 tileIdx = DTid.xy;
    int ts = TileSize;

    // Compute top-left pixel of this tile
    int x0 = tileIdx.x * ts;
    int y0 = tileIdx.y * ts;

    // Accumulate sum over the tile
    float4 sum = (float4)0.0f;

    for (int dy = 0; dy < ts; dy++) {
        for (int dx = 0; dx < ts; dx++) {
            float4 pixel = RefTexture.Load(int3(x0 + dx, y0 + dy, 0));
            sum += pixel;
        }
    }

    // Compute mean (tile area = ts * ts)
    float4 tileMean = sum / (float)(ts * ts);

    // Output the tile mean for signal-dependent noise estimation
    OutputTexture[tileIdx] = tileMean;
}
