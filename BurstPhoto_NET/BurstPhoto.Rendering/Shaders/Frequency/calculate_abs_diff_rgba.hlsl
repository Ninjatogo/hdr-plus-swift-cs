/*
 * calculate_abs_diff_rgba.hlsl
 * Calculates absolute difference between reference and aligned textures
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    float4 ref = RefTexture.Load(int3(DTid.xy, 0));
    float4 aligned = AlignedTexture.Load(int3(DTid.xy, 0));
    OutputTexture[DTid.xy] = abs(ref - aligned);
}
