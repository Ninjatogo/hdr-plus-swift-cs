/*
 * merge_frequency_domain.hlsl
 * Merges frequency domain representations with sub-pixel alignment and weighting
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
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
