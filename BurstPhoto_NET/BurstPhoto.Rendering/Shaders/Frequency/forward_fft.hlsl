/*
 * forward_fft.hlsl
 * Forward FFT transform for frequency domain processing
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    // Bounds check: Ensure thread ID corresponds to a valid tile
    // RefTexture is the input RGBA texture (width × height)
    // OutputTexture is 2x width for complex number storage (2*width × height)
    uint inputWidth, inputHeight;
    RefTexture.GetDimensions(inputWidth, inputHeight);

    // Calculate tile grid based on INPUT dimensions (RGBA texture)
    int nTilesX = inputWidth / TileSize;
    int nTilesY = inputHeight / TileSize;

    if (DTid.x >= (uint)nTilesX || DTid.y >= (uint)nTilesY)
        return;

    // Call the FFT implementation from FrequencyCommon.hlsli
    forward_fft_impl(TileSize, DTid, OutputTexture, RefTexture);
}
