/*
 * backward_fft.hlsl
 * Backward (inverse) FFT transform for frequency domain processing
 */

#include "FrequencyCommon.hlsli"

// DEBUG MODE:
// 0 = Normal FFT operation
// 1 = Output gradient test pattern (bypasses FFT)
// 2 = Output input FT data directly (tests if input data exists)
// 3 = Output DC component only (sum of all input values / normalization)
// 4 = Copy DC bin (freq 0,0) directly to all output pixels (tests if DC is correct after forward FFT)
#define DEBUG_GRADIENT_MODE 0

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    // Bounds check: Ensure thread ID corresponds to a valid tile
    // For backward FFT: RefTexture has DOUBLE width (complex storage), OutputTexture is single width
    uint inputWidth, inputHeight;
    RefTexture.GetDimensions(inputWidth, inputHeight);

    uint outputWidth, outputHeight;
    OutputTexture.GetDimensions(outputWidth, outputHeight);

    // Calculate tile grid based on OUTPUT dimensions (single-width RGBA)
    int nTilesX = outputWidth / TileSize;
    int nTilesY = outputHeight / TileSize;

    // Bounds check: verify thread is within tile grid
    if (DTid.x >= (uint)nTilesX || DTid.y >= (uint)nTilesY)
        return;

    // Additional safety check: verify input texture is correctly sized (2x output width)
    if (inputWidth != outputWidth * 2 || inputHeight != outputHeight)
        return;

    uint2 gid = DTid.xy;
    int ts = TileSize;
    int m0 = gid.x * ts;
    int n0 = gid.y * ts;

#if DEBUG_GRADIENT_MODE == 1
    // DEBUG: Write a gradient pattern to ALL pixels in this tile
    for (int dy = 0; dy < ts; dy++) {
        for (int dx = 0; dx < ts; dx++) {
            int px = m0 + dx;
            int py = n0 + dy;
            float4 debugColor = float4(
                (float)px / (float)outputWidth,
                (float)py / (float)outputHeight,
                (float)gid.x / (float)nTilesX,
                (float)gid.y / (float)nTilesY
            );
            OutputTexture[int2(px, py)] = debugColor * 16000.0f;
        }
    }
    return;
#elif DEBUG_GRADIENT_MODE == 2
    // DEBUG: Output the input FT data directly (real parts only, scaled)
    // This tests whether the input frequency domain data exists
    for (int dy = 0; dy < ts; dy++) {
        for (int dx = 0; dx < ts; dx++) {
            // Read real part of complex FT data
            float4 realPart = RefTexture.Load(int3(2*(m0+dx), n0+dy, 0));
            OutputTexture[int2(m0+dx, n0+dy)] = abs(realPart);  // Use abs to see magnitude
        }
    }
    return;
#elif DEBUG_GRADIENT_MODE == 3
    // DEBUG: Output DC component - sum all input values and normalize
    // This is effectively what the FFT should produce for uniform input
    float4 dcSum = (float4)0.0f;
    for (int dy = 0; dy < ts; dy++) {
        for (int dx = 0; dx < ts; dx++) {
            dcSum += RefTexture.Load(int3(2*(m0+dx), n0+dy, 0));
        }
    }
    float4 dcNorm = dcSum / (float)(NumTextures * ts * ts);
    for (int dy2 = 0; dy2 < ts; dy2++) {
        for (int dx2 = 0; dx2 < ts; dx2++) {
            OutputTexture[int2(m0+dx2, n0+dy2)] = dcNorm;
        }
    }
    return;
#elif DEBUG_GRADIENT_MODE == 4
    // DEBUG: Copy DC bin (frequency 0,0) from the frequency domain input
    // The DC bin is at position (2*m0, n0) in the FT texture (real part)
    // After forward FFT, DC should equal sum of all spatial values (with cosine window)
    // After inverse, dividing by N gives the average
    float4 dcReal = RefTexture.Load(int3(2*m0, n0, 0));  // Real part of DC
    float4 dcNormalized = dcReal / (float)(NumTextures * ts * ts);
    for (int dy = 0; dy < ts; dy++) {
        for (int dx = 0; dx < ts; dx++) {
            OutputTexture[int2(m0+dx, n0+dy)] = dcNormalized;
        }
    }
    return;
#endif
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
