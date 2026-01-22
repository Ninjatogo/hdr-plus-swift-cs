/*
 * calculate_mismatch_rgba.hlsl
 * Calculates mismatch metric between reference and aligned frames
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    float4 diff = RefTexture.Load(int3(DTid.xy, 0));
    float4 ref = AlignedTexture.Load(int3(DTid.xy, 0));
    float noise = ReadNoise;
    float robustness = RobustnessNorm;
    float4 denom = 2.0f * (noise*noise + ref/ExposureFactor);
    float4 dist = (diff*diff) / denom;
    float mismatch = dist.r + dist.g + dist.b;
    OutputTexture[DTid.xy] = (float4)(mismatch / robustness);
}
