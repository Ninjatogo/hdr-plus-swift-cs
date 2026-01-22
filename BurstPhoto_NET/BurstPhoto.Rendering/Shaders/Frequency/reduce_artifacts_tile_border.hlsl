/*
 * reduce_artifacts_tile_border.hlsl
 * Reduces artifacts at tile borders using cosine windowing
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    int ts = TileSize;
    int x0 = (gid.x / ts) * ts;
    int y0 = (gid.y / ts) * ts;
    int dx = gid.x - x0;
    int dy = gid.y - y0;
    float angle = -2.0f * PI / (float)ts;
    float norm_cosine = (0.5f - 0.5f*cos(-angle*(dx+0.5f))) * (0.5f - 0.5f*cos(-angle*(dy+0.5f)));

    float4 p = OutputTexture[gid];
    if (dx==0 || dx==ts-1 || dy==0 || dy==ts-1) {
        float4 refP = RefTexture.Load(int3(gid, 0));
        p = 0.5f * (norm_cosine * refP + p);
    }
    OutputTexture[gid] = p;
}
