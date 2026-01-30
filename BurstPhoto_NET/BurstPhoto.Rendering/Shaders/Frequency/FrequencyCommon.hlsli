/*
 * FrequencyCommon.hlsli
 * Shared constants, buffers, and helper functions for frequency domain shaders
 */

#include "../Constants.hlsli"

// -------------------------------------------------------------------------
// Constant Buffers
// -------------------------------------------------------------------------

[[vk::binding(0, 0)]]
cbuffer FrequencyParams : register(b0)
{
    float RobustnessNorm;
    float ReadNoise;
    float MaxMotionNorm;
    int TileSize;
    int UniformExposure;

    // Additional params
    int NumTextures;
    float ExposureFactor;
    float WhiteLevel;
    float BlackLevelMean;
    float MeanMismatch;

    // Per-channel black levels for reduce_artifacts_tile_border
    int BlackLevel0;
    int BlackLevel1;
    int BlackLevel2;
    int BlackLevel3;
};

// -------------------------------------------------------------------------
// Resources
// -------------------------------------------------------------------------
// NOTE: Using explicit [[vk::binding]] attributes to match C# descriptor layout.
// C# binds: Binding 0 = UBO, Binding 1 (t0), Binding 2 (t1), Binding 3 (t2), Binding 4 (t3), Binding 5 (t4), Binding 10 (u10)

// Primary Inputs
// IMPORTANT: These use register(t1-t5) but must map to Bindings 1-5 (not 2-6!)
[[vk::binding(1, 0)]]
Texture2D<float4> RefTexture     : register(t1);

[[vk::binding(2, 0)]]
Texture2D<float4> AlignedTexture : register(t2);

[[vk::binding(3, 0)]]
Texture2D<float4> AuxTexture0    : register(t3); // RMS

[[vk::binding(4, 0)]]
Texture2D<float4> AuxTexture1    : register(t4); // Mismatch

[[vk::binding(5, 0)]]
Texture2D<float4> AuxTexture2    : register(t5); // Highlights

// Outputs
[[vk::binding(10, 0)]]
// NOTE: image_format attribute removed - requires ShaderStorageImageWriteWithoutFormat feature instead
// [[vk::image_format("rgba32f")]]  // This causes DXC compilation errors on some systems
RWTexture2D<float4> OutputTexture : register(u10);

// -------------------------------------------------------------------------
// Shared Helper Functions
// -------------------------------------------------------------------------

// Forward FFT Implementation (shared by forward_fft shader)
void forward_fft_impl(int ts, uint3 gid, RWTexture2D<float4> outFT, Texture2D<float4> inTex)
{
    uint2 id = gid.xy;
    int m0 = id.x * ts;
    int n0 = id.y * ts;
    int sz14 = ts / 4;
    int sz24 = ts / 2;
    int sz34 = (ts / 4) * 3;
    float angle = -2.0f * PI / (float)ts;
    float4 zeros = (float4)0.0f;
    // Array sizes match Metal: tmp_data[2*ts], tmp_tile[(ts/2+1)*2*ts]
    // For ts=8: tmp_data[16], tmp_tile[80]
    float4 tmp_data[16];
    float4 tmp_tile[80];
    float coefRe, coefIm, norm_cosine0, norm_cosine1;
    float4 Re0, Re1, Re2, Re3, Im0, Im1, Im2, Im3;
    float4 Re00, Re11, Re22, Re33, Im00, Im11, Im22, Im33, dataRe, dataIm;

    for (int dm = 0; dm < ts; dm += 2) {
        for (int dn = 0; dn < ts; dn++) {
            tmp_data[2*dn+0] = inTex.Load(int3(m0+dm+0, n0+dn, 0));
            tmp_data[2*dn+1] = inTex.Load(int3(m0+dm+1, n0+dn, 0));
        }
        for (int dn = 0; dn <= ts/2; dn++) {
            int n_tmp = dn * 2 * ts;
            Re0 = Im0 = Re1 = Im1 = zeros;
            for (int dy = 0; dy < ts; dy++) {
                norm_cosine0 = (0.5f - 0.5f * cos(-angle * (dm + 0.5f))) * (0.5f - 0.5f * cos(-angle * (dy + 0.5f)));
                norm_cosine1 = (0.5f - 0.5f * cos(-angle * (dm + 1.5f))) * (0.5f - 0.5f * cos(-angle * (dy + 0.5f)));
                coefRe = cos(angle * dn * dy);
                coefIm = sin(angle * dn * dy);
                dataRe = norm_cosine0 * tmp_data[2*dy+0];
                Re0 += (coefRe * dataRe);
                Im0 += (coefIm * dataRe);
                dataRe = norm_cosine1 * tmp_data[2*dy+1];
                Re1 += (coefRe * dataRe);
                Im1 += (coefIm * dataRe);
            }
            tmp_tile[n_tmp+2*dm+0] = Re0; tmp_tile[n_tmp+2*dm+1] = Im0;
            tmp_tile[n_tmp+2*dm+2] = Re1; tmp_tile[n_tmp+2*dm+3] = Im1;
        }
    }
    for (int dn = 0; dn <= ts/2; dn++) {
        int n = n0 + dn;
        for (int dm = 0; dm < ts; dm++) {
            tmp_data[2*dm+0] = tmp_tile[dn*2*ts + 2*dm+0];
            tmp_data[2*dm+1] = tmp_tile[dn*2*ts + 2*dm+1];
        }
        for (int dm = 0; dm < ts/4; dm++) {
            int m = 2*(m0 + dm);
            Re0 = Im0 = Re1 = Im1 = Re2 = Im2 = Re3 = Im3 = zeros;
            for (int dx = 0; dx < ts; dx+=4) {
                coefRe = cos(angle * dm * dx); coefIm = sin(angle * dm * dx);
                dataRe = tmp_data[2*dx+0]; dataIm = tmp_data[2*dx+1];
                Re0 += (coefRe*dataRe - coefIm*dataIm); Im0 += (coefIm*dataRe + coefRe*dataIm);
                dataRe = tmp_data[2*dx+2]; dataIm = tmp_data[2*dx+3];
                Re2 += (coefRe*dataRe - coefIm*dataIm); Im2 += (coefIm*dataRe + coefRe*dataIm);
                dataRe = tmp_data[2*dx+4]; dataIm = tmp_data[2*dx+5];
                Re1 += (coefRe*dataRe - coefIm*dataIm); Im1 += (coefIm*dataRe + coefRe*dataIm);
                dataRe = tmp_data[2*dx+6]; dataIm = tmp_data[2*dx+7];
                Re3 += (coefRe*dataRe - coefIm*dataIm); Im3 += (coefIm*dataRe + coefRe*dataIm);
            }
            coefRe = cos(angle * 2 * dm); coefIm = sin(angle * 2 * dm);
            Re00 = Re0 + coefRe*Re1 - coefIm*Im1; Im00 = Im0 + coefIm*Re1 + coefRe*Im1;
            Re22 = Re2 + coefRe*Re3 - coefIm*Im3; Im22 = Im2 + coefIm*Re3 + coefRe*Im3;
            coefRe = cos(angle * 2 * (dm + sz14)); coefIm = sin(angle * 2 * (dm + sz14));
            Re11 = Re0 + coefRe*Re1 - coefIm*Im1; Im11 = Im0 + coefIm*Re1 + coefRe*Im1;
            Re33 = Re2 + coefRe*Re3 - coefIm*Im3; Im33 = Im2 + coefIm*Re3 + coefRe*Im3;
            Re0 = Re00 + cos(angle*dm)*Re22 - sin(angle*dm)*Im22; Im0 = Im00 + sin(angle*dm)*Re22 + cos(angle*dm)*Im22;
            Re2 = Re00 + cos(angle*(dm+sz24))*Re22 - sin(angle*(dm+sz24))*Im22; Im2 = Im00 + sin(angle*(dm+sz24))*Re22 + cos(angle*(dm+sz24))*Im22;
            Re1 = Re11 + cos(angle*(dm+sz14))*Re33 - sin(angle*(dm+sz14))*Im33; Im1 = Im11 + sin(angle*(dm+sz14))*Re33 + cos(angle*(dm+sz14))*Im33;
            Re3 = Re11 + cos(angle*(dm+sz34))*Re33 - sin(angle*(dm+sz34))*Im33; Im3 = Im11 + sin(angle*(dm+sz34))*Re33 + cos(angle*(dm+sz34))*Im33;
            outFT[int2(m+0, n)] = Re0; outFT[int2(m+1, n)] = Im0;
            outFT[int2(m+sz24+0, n)] = Re1; outFT[int2(m+sz24+1, n)] = Im1;
            outFT[int2(m+ts+0, n)] = Re2; outFT[int2(m+ts+1, n)] = Im2;
            outFT[int2(m+sz24*3+0, n)] = Re3; outFT[int2(m+sz24*3+1, n)] = Im3;
            if (dn > 0 && dn != ts/2) {
                int n2 = n0 + ts - dn;
                int factor = (dm < 1 ? dm : 1);
                int m20 = 2*(m0 + factor*(ts-dm));
                int m21 = 2*(m0 + ts - dm - sz14);
                int m22 = 2*(m0 + ts - dm - sz24);
                int m23 = 2*(m0 + ts - dm - sz34);
                outFT[int2(m20+0, n2)] = Re0; outFT[int2(m20+1, n2)] = -Im0;
                outFT[int2(m21+0, n2)] = Re1; outFT[int2(m21+1, n2)] = -Im1;
                outFT[int2(m22+0, n2)] = Re2; outFT[int2(m22+1, n2)] = -Im2;
                outFT[int2(m23+0, n2)] = Re3; outFT[int2(m23+1, n2)] = -Im3;
            }
        }
    }
}
