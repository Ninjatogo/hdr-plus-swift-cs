/*
 * MergeSpatial.hlsl
 * Port of spatial.metal
 */

#include "Constants.hlsli"

// -------------------------------------------------------------------------
// Constant Buffers
// -------------------------------------------------------------------------

cbuffer SpatialParams : register(b0)
{
    float WhiteLevel;
    float BlackLevel;
    float Robustness;   // Single robustness parameter (was RobustnessParam1/2)
    float NoiseSd;      // Noise standard deviation
};

// -------------------------------------------------------------------------
// Resources
// -------------------------------------------------------------------------

Texture2D<float> RefTexture : register(t0); 
Texture2D<float> CompTexture : register(t1);
Texture2D<float> InDiff : register(t2); // Input for compute_merge_weight

// u0 -> u10 to avoid collision with t0
RWTexture2D<float> OutDiff : register(u10);   // color_difference output
RWTexture2D<float> OutWeight : register(u10); // compute_merge_weight output

// -------------------------------------------------------------------------
// Kernels
// -------------------------------------------------------------------------

// kernel void color_difference(...)
[numthreads(16, 16, 1)]
void color_difference(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint width, height;
    OutDiff.GetDimensions(width, height);
    if (gid.x >= width || gid.y >= height) return;

    // Metal calls this on y and u/v planes potentially. 
    // We assume float texture input.
    float refVal = RefTexture.Load(int3(gid.x, gid.y, 0)).r;
    float compVal = CompTexture.Load(int3(gid.x, gid.y, 0)).r;
    
    OutDiff[gid] = abs(refVal - compVal);
}

// kernel void compute_merge_weight(...)
// Port of spatial.metal compute_merge_weight
[numthreads(16, 16, 1)]
void compute_merge_weight(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    uint width, height;
    OutWeight.GetDimensions(width, height);
    if (gid.x >= width || gid.y >= height) return;
    
    float diff = InDiff.Load(int3(gid.x, gid.y, 0)).r;
    
    // compute the weight to assign to the comparison frame
    // weight == 0 means that the aligned image is ignored
    // weight == 1 means that the aligned image has full weight
    float weight;
    if (Robustness == 0.0f)
    {
        // robustness == 0 means that robust merge is turned off
        weight = 1.0f;
    }
    else
    {
        // compare the difference to image noise
        // as diff increases, the weight of the aligned image will continuously decrease from 1.0 to 0.0
        // the two extreme cases are:
        // diff == 0                   --> aligned image will have weight 1.0
        // diff >= noise_sd/robustness --> aligned image will have weight 0.0
        float max_diff = NoiseSd / Robustness;
        weight = 1.0f - diff / max(max_diff, 1e-6f);
        weight = clamp(weight, 0.0f, 1.0f);
    }
    
    OutWeight[gid] = weight;
}

