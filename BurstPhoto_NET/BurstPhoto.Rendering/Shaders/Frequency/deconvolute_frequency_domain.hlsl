/*
 * deconvolute_frequency_domain.hlsl
 * Deconvolves frequency domain data to sharpen the result
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    // Bounds check: Ensure thread ID corresponds to a valid tile
    uint width, height;
    RefTexture.GetDimensions(width, height);
    int nTilesX = width;  // RefTexture is tile grid (nTilesX × nTilesY)
    int nTilesY = height;

    if (DTid.x >= nTilesX || DTid.y >= nTilesY)
        return; // Skip out-of-bounds threads

    uint2 gid = DTid.xy;
    int ts = TileSize;
    int m0 = gid.x * ts;
    int n0 = gid.y * ts;

    float cw[16];
    if (ts == 8) {
        cw[0]=0; cw[1]=0.02; cw[2]=0.04; cw[3]=0.08; cw[4]=0.04; cw[5]=0.08; cw[6]=0.04; cw[7]=0.02;
        cw[8]=0; cw[9]=0; cw[10]=0; cw[11]=0; cw[12]=0; cw[13]=0; cw[14]=0; cw[15]=0;
    } else {
        cw[0]=0; cw[1]=0.01; cw[2]=0.02; cw[3]=0.03; cw[4]=0.04; cw[5]=0.06; cw[6]=0.08; cw[7]=0.06;
        cw[8]=0.04; cw[9]=0.06; cw[10]=0.08; cw[11]=0.06; cw[12]=0.04; cw[13]=0.03; cw[14]=0.02; cw[15]=0.01;
    }
    float mismatch = RefTexture.Load(int3(gid, 0)).r; // Mismatch as Input t0
    float mismatch_weight = clamp(1.0f - 10.0f*(mismatch - 0.2f), 0.0f, 1.0f);

    float4 dcRe = OutputTexture[int2(2*m0+0, n0)];
    float4 dcIm = OutputTexture[int2(2*m0+1, n0)];
    float4 dcMag = sqrt(dcRe*dcRe + dcIm*dcIm);
    float valZero = dcMag.r + dcMag.g + dcMag.b + dcMag.a;

    for (int dn = 0; dn < ts; dn++) {
        for (int dm = 0; dm < ts; dm++) {
            if (dm+dn > 0 && mismatch < 0.3f) {
                int m = 2*(m0 + dm);
                int n = n0 + dn;
                float4 re = OutputTexture[int2(m+0, n)];
                float4 im = OutputTexture[int2(m+1, n)];
                float4 mag = sqrt(re*re + im*im);
                float val = mag.r + mag.g + mag.b + mag.a;
                float weight = mismatch_weight * clamp(1.25f - 25.0f * val / (valZero + 1e-6f), 0.0f, 1.0f);
                float mult = (1.0f + weight * cw[dm]) * (1.0f + weight * cw[dn]);
                OutputTexture[int2(m+0, n)] = re * mult;
                OutputTexture[int2(m+1, n)] = im * mult;
            }
        }
    }
}
