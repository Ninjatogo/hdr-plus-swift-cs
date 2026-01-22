/*
 * normalize_mismatch.hlsl
 * Normalizes mismatch values
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    float val = RefTexture.Load(int3(DTid.xy, 0)).r;
    float norm = clamp(val / MeanMismatch, 0.0f, 1.0f);
    OutputTexture[DTid.xy] = (float4)norm;
}
