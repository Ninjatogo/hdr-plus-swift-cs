/*
 * calculate_rms_rgba.hlsl
 * Calculates RMS (root mean square) values
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    float4 diff = RefTexture.Load(int3(DTid.xy, 0));
    OutputTexture[DTid.xy] = diff * diff;
}
