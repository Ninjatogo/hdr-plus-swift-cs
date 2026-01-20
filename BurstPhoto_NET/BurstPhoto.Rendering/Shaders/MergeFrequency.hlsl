/*
 * MergeFrequency.hlsl
 * Port of frequency.metal
 */

#include "Constants.hlsli"

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
RWTexture2D<float4> OutputTexture : register(u10);

// -------------------------------------------------------------------------
// Helpers
// -------------------------------------------------------------------------

// Forward FFT Implementation
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
            Re0 = Im0 = Re1 = Im1 = 0.0f;
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
            Re0 = Im0 = Re1 = Im1 = Re2 = Im2 = Re3 = Im3 = 0.0f;
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

[numthreads(16, 16, 1)]
void forward_fft(uint3 DTid : SV_DispatchThreadID)
{
    forward_fft_impl(TileSize, DTid, OutputTexture, RefTexture);
}

// -------------------------------------------------------------------------
// Backward FFT
// -------------------------------------------------------------------------
[numthreads(16, 16, 1)]
void backward_fft(uint3 DTid : SV_DispatchThreadID)
{
    // Bounds check: Ensure thread ID corresponds to a valid tile
    uint width, height;
    RefTexture.GetDimensions(width, height);
    int nTilesX = (width / 2) / TileSize;  // RefTexture has double width for complex numbers
    int nTilesY = height / TileSize;

    if (DTid.x >= nTilesX || DTid.y >= nTilesY)
        return; // Skip out-of-bounds threads

    uint2 gid = DTid.xy;
    int ts = TileSize;
    int m0 = gid.x * ts;
    int n0 = gid.y * ts;
    int sz14 = ts / 4;
    int sz24 = ts / 2;
    int sz34 = (ts / 4) * 3;
    float angle = -2.0f * PI / (float)ts;
    float4 zeros = (float4)0.0f;
    float4 norm_factor = (float)(NumTextures * ts * ts);
    float4 tmp_data[64]; 
    float4 tmp_tile[512]; 
    float coefRe, coefIm;
    float4 Re0, Re1, Re2, Re3, Im0, Im1, Im2, Im3;
    float4 Re00, Re11, Re22, Re33, Im00, Im11, Im22, Im33, dataRe, dataIm;
    
    // t0=InputFT, u0=Output
    
    for (int dn = 0; dn < ts; dn++) {
        int n_tmp = dn * 2 * ts;
        for (int dm = 0; dm < ts; dm++) {
            tmp_data[2*dm+0] = RefTexture.Load(int3(2*(m0+dm)+0, n0+dn, 0));
            tmp_data[2*dm+1] = RefTexture.Load(int3(2*(m0+dm)+1, n0+dn, 0));
        }
        for (int dm = 0; dm < ts/4; dm++) {
            Re0 = Im0 = Re1 = Im1 = Re2 = Im2 = Re3 = Im3 = zeros;
            for (int dx = 0; dx < ts; dx+=4) {
                coefRe = cos(angle * dm * dx); coefIm = sin(angle * dm * dx);
                dataRe = tmp_data[2*dx+0]; dataIm = tmp_data[2*dx+1];
                Re0 += (coefRe*dataRe + coefIm*dataIm); Im0 += (coefIm*dataRe - coefRe*dataIm);
                dataRe = tmp_data[2*dx+2]; dataIm = tmp_data[2*dx+3];
                Re2 += (coefRe*dataRe + coefIm*dataIm); Im2 += (coefIm*dataRe - coefRe*dataIm);
                dataRe = tmp_data[2*dx+4]; dataIm = tmp_data[2*dx+5];
                Re1 += (coefRe*dataRe + coefIm*dataIm); Im1 += (coefIm*dataRe - coefRe*dataIm);
                dataRe = tmp_data[2*dx+6]; dataIm = tmp_data[2*dx+7];
                Re3 += (coefRe*dataRe + coefIm*dataIm); Im3 += (coefIm*dataRe - coefRe*dataIm);
            }
            coefRe = cos(angle*2*dm); coefIm = sin(angle*2*dm);
            Re00 = Re0 + coefRe*Re1 - coefIm*Im1; Im00 = Im0 + coefIm*Re1 + coefRe*Im1;
            Re22 = Re2 + coefRe*Re3 - coefIm*Im3; Im22 = Im2 + coefIm*Re3 + coefRe*Im3;
            coefRe = cos(angle*2*(dm+sz14)); coefIm = sin(angle*2*(dm+sz14)); // Corrected dn->dm
            // Check Metal: `coefRe = cos(angle*2*(dm+tile_size_14));`. Correct. Copy paste err.
            Re11 = Re0 + coefRe*Re1 - coefIm*Im1; Im11 = Im0 + coefIm*Re1 + coefRe*Im1;
            Re33 = Re2 + coefRe*Re3 - coefIm*Im3; Im33 = Im2 + coefIm*Re3 + coefRe*Im3;
            Re0 = Re00 + cos(angle*dm)*Re22 - sin(angle*dm)*Im22; Re2 = Re00 + cos(angle*(dm+sz24))*Re22 - sin(angle*(dm+sz24))*Im22;
            Re1 = Re11 + cos(angle*(dm+sz14))*Re33 - sin(angle*(dm+sz14))*Im33; Re3 = Re11 + cos(angle*(dm+sz34))*Re33 - sin(angle*(dm+sz34))*Im33;
            tmp_tile[n_tmp+2*dm+0] = Re0; tmp_tile[n_tmp+2*dm+1] = -Im0;
            tmp_tile[n_tmp+2*dm+sz24+0] = Re1; tmp_tile[n_tmp+2*dm+sz24+1] = -Im1;
            tmp_tile[n_tmp+2*dm+ts+0] = Re2; tmp_tile[n_tmp+2*dm+ts+1] = -Im2;
            tmp_tile[n_tmp+2*dm+sz24*3+0] = Re3; tmp_tile[n_tmp+2*dm+sz24*3+1] = -Im3;
        }
    }
    for (int dm = 0; dm < ts; dm++) {
        for (int dn = 0; dn < ts; dn++) {
            tmp_data[2*dn+0] = tmp_tile[dn*2*ts + 2*dm+0];
            tmp_data[2*dn+1] = tmp_tile[dn*2*ts + 2*dm+1];
        }
        for (int dn = 0; dn < ts/4; dn++) {
            Re0 = Im0 = Re1 = Im1 = Re2 = Im2 = Re3 = Im3 = zeros;
            for (int dy = 0; dy < ts; dy+=4) {
                 coefRe = cos(angle*dn*dy); coefIm = sin(angle*dn*dy);
                dataRe = tmp_data[2*dy+0]; dataIm = tmp_data[2*dy+1];
                Re0 += (coefRe*dataRe + coefIm*dataIm); Im0 += (coefIm*dataRe - coefRe*dataIm);
                dataRe = tmp_data[2*dy+2]; dataIm = tmp_data[2*dy+3];
                Re2 += (coefRe*dataRe + coefIm*dataIm); Im2 += (coefIm*dataRe - coefRe*dataIm);
                dataRe = tmp_data[2*dy+4]; dataIm = tmp_data[2*dy+5];
                Re1 += (coefRe*dataRe + coefIm*dataIm); Im1 += (coefIm*dataRe - coefRe*dataIm);
                dataRe = tmp_data[2*dy+6]; dataIm = tmp_data[2*dy+7];
                Re3 += (coefRe*dataRe + coefIm*dataIm); Im3 += (coefIm*dataRe - coefRe*dataIm);
            }
            coefRe = cos(angle*2*dn); coefIm = sin(angle*2*dn);
            Re00 = Re0 + coefRe*Re1 - coefIm*Im1; Im00 = Im0 + coefIm*Re1 + coefRe*Im1;
            Re22 = Re2 + coefRe*Re3 - coefIm*Im3; Im22 = Im2 + coefIm*Re3 + coefRe*Im3;
            coefRe = cos(angle*2*(dn+sz14)); coefIm = sin(angle*2*(dn+sz14));
            Re11 = Re0 + coefRe*Re1 - coefIm*Im1; Im11 = Im0 + coefIm*Re1 + coefRe*Im1;
            Re33 = Re2 + coefRe*Re3 - coefIm*Im3; Im33 = Im2 + coefIm*Re3 + coefRe*Im3;
            Re0 = Re00 + cos(angle*dn)*Re22 - sin(angle*dn)*Im22; Re2 = Re00 + cos(angle*(dn+sz24))*Re22 - sin(angle*(dn+sz24))*Im22;
            Re1 = Re11 + cos(angle*(dn+sz14))*Re33 - sin(angle*(dn+sz14))*Im33; Re3 = Re11 + cos(angle*(dn+sz34))*Re33 - sin(angle*(dn+sz34))*Im33;
            OutputTexture[int2(m0+dm, n0+dn)] = Re0 / norm_factor;
            OutputTexture[int2(m0+dm, n0+dn+sz14)] = Re1 / norm_factor;
            OutputTexture[int2(m0+dm, n0+dn+sz24)] = Re2 / norm_factor;
            OutputTexture[int2(m0+dm, n0+dn+sz34)] = Re3 / norm_factor;
        }
    }
}

// -------------------------------------------------------------------------
// Helpers & Merge
// -------------------------------------------------------------------------

[numthreads(16, 16, 1)]
void calculate_abs_diff_rgba(uint3 DTid : SV_DispatchThreadID)
{
    float4 ref = RefTexture.Load(int3(DTid.xy, 0));
    float4 aligned = AlignedTexture.Load(int3(DTid.xy, 0));
    OutputTexture[DTid.xy] = abs(ref - aligned);
}

[numthreads(16, 16, 1)]
void calculate_highlights_norm_rgba(uint3 DTid : SV_DispatchThreadID)
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

[numthreads(16, 16, 1)]
void calculate_mismatch_rgba(uint3 DTid : SV_DispatchThreadID)
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

[numthreads(16, 16, 1)]
void calculate_rms_rgba(uint3 DTid : SV_DispatchThreadID)
{
    float4 diff = RefTexture.Load(int3(DTid.xy, 0));
    OutputTexture[DTid.xy] = diff * diff;
}

[numthreads(16, 16, 1)]
void normalize_mismatch(uint3 DTid : SV_DispatchThreadID)
{
    float val = RefTexture.Load(int3(DTid.xy, 0)).r;
    float norm = clamp(val / MeanMismatch, 0.0f, 1.0f);
    OutputTexture[DTid.xy] = (float4)norm;
}

[numthreads(16, 16, 1)]
void reduce_artifacts_tile_border(uint3 DTid : SV_DispatchThreadID)
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

[numthreads(16, 16, 1)]
void deconvolute_frequency_domain(uint3 DTid : SV_DispatchThreadID)
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

[numthreads(16, 16, 1)]
void merge_frequency_domain(uint3 DTid : SV_DispatchThreadID)
{
    // Bounds check: Ensure thread ID corresponds to a valid tile
    uint width, height;
    AuxTexture0.GetDimensions(width, height);
    int nTilesX = width;  // AuxTexture0 is tile grid (nTilesX × nTilesY)
    int nTilesY = height;

    if (DTid.x >= nTilesX || DTid.y >= nTilesY)
        return; // Skip out-of-bounds threads

    // Mapping:
    // u0 = RefFT (RW) = OutputTexture
    // t0 = RefFT (Read) (If reading logic needed, can read u0)
    // t1 = AlignedFT = AlignedTexture
    // t2 = RMS = AuxTexture0
    // t3 = Mismatch = AuxTexture1
    // t4 = Highlights = AuxTexture2

    uint2 gid = DTid.xy;
    int ts = TileSize;
    int m0 = gid.x * ts;
    int n0 = gid.y * ts;
    
    float4 noise_est = AuxTexture0.Load(int3(gid, 0)) + ReadNoise;
    float4 noise_norm = noise_est * (float)(ts*ts) * RobustnessNorm;
    
    float mismatch = AuxTexture1.Load(int3(gid, 0)).r;
    float mismatch_weight = clamp(1.0f - 10.0f*(mismatch - 0.2f), 0.0f, 1.0f);
    float motion_norm = clamp(MaxMotionNorm - (mismatch-0.02f)*(MaxMotionNorm-1.0f)/0.15f, 1.0f, MaxMotionNorm);
    
    float highlights_norm = AuxTexture2.Load(int3(gid, 0)).r;
    
    float angle = -2.0f * PI / (float)ts;
    float shift_step = 1.0f / 6.0f;
    float total_diff[49];
    for (int k=0; k<49; k++) total_diff[k] = 0.0f;
    
    float4 refRe, refIm, algRe, algIm;
    
    // Subpixel Search
    for (int dn = 0; dn < ts; dn++) {
        for (int dm = 0; dm < ts; dm++) {
            int m = 2*(m0 + dm);
            int n = n0 + dn;
            
            refRe = RefTexture.Load(int3(m+0, n, 0)); 
            refIm = RefTexture.Load(int3(m+1, n, 0));
            algRe = AlignedTexture.Load(int3(m+0, n, 0)); // Aligned is Texture
            algIm = AlignedTexture.Load(int3(m+1, n, 0));
            
            for (int i=0; i<49; i++) {
                float sx = -0.5f + (float)(i % 7) * shift_step;
                float sy = -0.5f + (float)(i / 7) * shift_step;
                float phase = angle * (dm*sx + dn*sy);
                float c = cos(phase), s = sin(phase);
                
                // algRe2 = refRe - (c*algRe - s*algIm)
                // Metal: refRe - (coefRe*alignedRe - coefIm*alignedIm)
                float4 diffRe = refRe - (c*algRe - s*algIm);
                float4 diffIm = refIm - (s*algRe + c*algIm);
                float4 w = diffRe*diffRe + diffIm*diffIm;
                total_diff[i] += (w.r + w.g + w.b + w.a);
            }
        }
    }
    
    float best_diff = 1e20f;
    int best_i = 0;
    for (int i=0; i<49; i++) {
        if (total_diff[i] < best_diff) {
            best_diff = total_diff[i];
            best_i = i;
        }
    }
    
    float best_sx = -0.5f + (float)(best_i % 7) * shift_step;
    float best_sy = -0.5f + (float)(best_i / 7) * shift_step;
    
    // Merge
    for (int dn = 0; dn < ts; dn++) {
        for (int dm = 0; dm < ts; dm++) {
            int m = 2*(m0 + dm);
            int n = n0 + dn;
            
            refRe = RefTexture.Load(int3(m+0, n, 0)); 
            refIm = RefTexture.Load(int3(m+1, n, 0));
            algRe = AlignedTexture.Load(int3(m+0, n, 0)); 
            algIm = AlignedTexture.Load(int3(m+1, n, 0));
            
            float phase = angle * (dm*best_sx + dn*best_sy);
            float c = cos(phase), s = sin(phase);
            
            float4 algRe2 = c*algRe - s*algIm;
            float4 algIm2 = s*algRe + c*algIm;
            
            float magnitude_norm = 1.0f;
            if (dm+dn > 0 && mismatch < 0.3f && UniformExposure == 1) {
                float4 rm = sqrt(refRe*refRe + refIm*refIm);
                float4 am = sqrt(algRe2*algRe2 + algIm2*algIm2);
                float ratio = (am.r+am.g+am.b+am.a)/(rm.r+rm.g+rm.b+rm.a + 1e-6f);
                magnitude_norm = mismatch_weight * clamp(ratio*ratio*ratio*ratio, 0.5f, 3.0f);
            }
            
            float4 diffRe = refRe - algRe2;
            float4 diffIm = refIm - algIm2;
            float4 w4 = diffRe*diffRe + diffIm*diffIm;
            w4 = w4 / (w4 + magnitude_norm * motion_norm * noise_norm * highlights_norm + 1e-6f);
            
            float min_w = min(w4.r, min(w4.g, min(w4.b, w4.a)));
            float max_w = max(w4.r, max(w4.g, max(w4.b, w4.a)));
            float w = clamp(0.5f*(w4.r+w4.g+w4.b+w4.a - min_w - max_w), 0.0f, 1.0f);
            
            // merged = accumulated + (1-w)*aligned + w*ref?
            // Metal: merged = out.read + (1-w)aligned + w*ref.
            // Wait. Metal: 'mergedRe = out_texture_ft.read(...) + (1.0f-weight)*alignedRe2 + weight*refRe;'
            // Here 'refRe' is read from 'ref_texture_ft' (texture 0).
            // And 'out_texture_ft' is texture 2 (Accumulator).
            // So logic implies Separated Accumulation.
            // Frame 0 is Ref. Frame 0 is likely copied to Out first?
            // If Out starts at 0, then:
            // Frame 1 merge: Out = 0 + (1-w)A + wR.
            // R is Frame 0. A is Frame 1.
            // Result is Weighted Mean of 0 and 1.
            // Then Frame 2?
            // Ref is still Frame 0. A is Frame 2.
            // Out = Out + (1-w)A + wR.
            // So Out accumulates weighted sums.
            // This is "Temporal Merging".
            
            // In my HLSL:
            // I mapped RefTexture -> t0 (Ref, Frame 0).
            // OutputTexture -> u0 (Accumulator).
            // So logic matches Metal exactly.
            
            float4 outRe = OutputTexture[int2(m+0, n)];
            float4 outIm = OutputTexture[int2(m+1, n)];
            
            float4 mergedRe = outRe + (1.0f - w)*algRe2 + w*refRe;
            float4 mergedIm = outIm + (1.0f - w)*algIm2 + w*refIm;
            
            OutputTexture[int2(m+0, n)] = mergedRe;
            OutputTexture[int2(m+1, n)] = mergedIm;
        }
    }
}
