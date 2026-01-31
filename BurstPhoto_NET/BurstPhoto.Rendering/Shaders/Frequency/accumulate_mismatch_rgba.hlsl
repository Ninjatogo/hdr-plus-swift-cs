/*
 * accumulate_mismatch_rgba.hlsl
 * GPU kernel to accumulate normalized mismatch texture into a total mismatch accumulator.
 * This replaces the CPU GetData -> loop -> SetData pattern in FrequencyMergePipeline.
 *
 * Operation: accumulator += source / divisor
 * Where divisor is totalImageCount (passed via NumTextures param)
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint width, height;
    RefTexture.GetDimensions(width, height);

    // Bounds check
    if (DTid.x >= width || DTid.y >= height)
    {
        return;
    }

    // Read source mismatch value (RefTexture is bound to texMismatch)
    float4 srcVal = RefTexture.Load(int3(DTid.xy, 0));

    // Read current accumulator value (OutputTexture is bound to totalMismatchTexture)
    float4 accVal = OutputTexture[DTid.xy];

    // Divide by total image count and accumulate
    float divisor = (float)NumTextures;
    OutputTexture[DTid.xy] = accVal + srcVal / divisor;
}
