/*
 * calculate_highlights_norm_rgba.hlsl
 * Calculates normalized highlights weighting
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    float4 val = RefTexture.Load(int3(DTid.xy, 0));
    float mismatch = AlignedTexture.Load(int3(DTid.xy, 0)).r; // Using AlignedTexture slot for Mismatch input

    float WL = WhiteLevel;
    float max_val = max(val.r, max(val.g, val.b));
    float4 norm_val = val / WL;
    float weight = 0.0f;
    if (max_val > 0.9f * WL) {
        float m_weight = clamp(1.0f - 10.0f*(mismatch - 0.2f), 0.0f, 1.0f);
        weight = m_weight * clamp((WL - max_val)/(0.1f*WL), 0.0f, 1.0f);
    } else {
        weight = clamp(1.0f - 10.0f*(mismatch - 0.2f), 0.0f, 1.0f);
    }
    OutputTexture[DTid.xy] = norm_val * weight;
}
