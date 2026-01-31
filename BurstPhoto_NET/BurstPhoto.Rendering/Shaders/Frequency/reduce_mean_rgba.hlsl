/*
 * reduce_mean_rgba.hlsl
 * GPU parallel reduction to compute mean of first channel of RGBA texture.
 * Uses two-pass reduction: first reduces columns to rows, then rows to single value.
 *
 * Pass 1 (reduce_mean_columns): Each thread sums one column of tiles into a row buffer
 * Pass 2 (reduce_mean_rows): Single workgroup sums the row buffer to produce final mean
 */

#include "FrequencyCommon.hlsli"

// Output buffer for intermediate row sums and final result
[[vk::binding(11, 0)]]
RWStructuredBuffer<float> ReductionBuffer : register(u11);

// -------------------------------------------------------------------------
// Pass 1: Column Reduction
// -------------------------------------------------------------------------
// Dispatch: (nTilesX, 1, 1) threads
// Each thread sums all values in one column of the mismatch texture (.r channel)
// Writes partial sum to ReductionBuffer[threadIdx]

[numthreads(256, 1, 1)]
void reduce_mean_columns(uint3 DTid : SV_DispatchThreadID)
{
    uint width, height;
    RefTexture.GetDimensions(width, height);

    // Skip if beyond texture width
    if (DTid.x >= width)
    {
        return;
    }

    float sum = 0.0f;
    for (uint y = 0; y < height; y++)
    {
        // Only use .r channel (mismatch is scalar stored in .r)
        sum += RefTexture.Load(int3(DTid.x, y, 0)).r;
    }

    ReductionBuffer[DTid.x] = sum;
}

// -------------------------------------------------------------------------
// Pass 2: Row Reduction
// -------------------------------------------------------------------------
// Dispatch: (1, 1, 1) - single workgroup
// Sums all partial column sums and divides by total pixel count
// NumTextures parameter repurposed to hold total pixel count (nTilesX * nTilesY)
// Writes final mean to ReductionBuffer[0]

groupshared float sharedSums[256];

[numthreads(256, 1, 1)]
void reduce_mean_final(uint3 DTid : SV_DispatchThreadID, uint3 Gid : SV_GroupID, uint GI : SV_GroupIndex)
{
    uint width, height;
    RefTexture.GetDimensions(width, height);

    // Each thread accumulates a portion of the column sums
    float localSum = 0.0f;
    for (uint i = GI; i < width; i += 256)
    {
        localSum += ReductionBuffer[i];
    }

    // Store in shared memory
    sharedSums[GI] = localSum;
    GroupMemoryBarrierWithGroupSync();

    // Parallel reduction in shared memory
    for (uint stride = 128; stride > 0; stride >>= 1)
    {
        if (GI < stride)
        {
            sharedSums[GI] += sharedSums[GI + stride];
        }
        GroupMemoryBarrierWithGroupSync();
    }

    // Thread 0 computes and writes final mean
    if (GI == 0)
    {
        float totalPixels = (float)(width * height);
        float mean = sharedSums[0] / totalPixels;

        // Clamp to avoid division by zero issues downstream
        if (mean < 1e-6f)
        {
            mean = 1e-6f;
        }

        ReductionBuffer[0] = mean;
    }
}
