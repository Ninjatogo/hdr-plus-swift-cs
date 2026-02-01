/*
 * forward_fft_debug.hlsl
 * Minimal test: just copy input to output to verify read/write works
 */

#include "FrequencyCommon.hlsli"

[numthreads(16, 16, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint inputWidth, inputHeight;
    RefTexture.GetDimensions(inputWidth, inputHeight);

    uint outputWidth, outputHeight;
    OutputTexture.GetDimensions(outputWidth, outputHeight);

    int nTilesX = inputWidth / TileSize;
    int nTilesY = inputHeight / TileSize;

    if (DTid.x >= (uint)nTilesX || DTid.y >= (uint)nTilesY)
        return;

    if (outputWidth != inputWidth * 2 || outputHeight != inputHeight)
        return;

    // Simple test: Read a pixel from input and write it to output
    // This bypasses all FFT logic to verify texture access works
    int m0 = DTid.x * TileSize;
    int n0 = DTid.y * TileSize;

    // Read first pixel of this tile
    float4 testPixel = RefTexture.Load(int3(m0, n0, 0));

    // Write it to output at corresponding position (doubled x-coordinate)
    int outX = 2 * m0;
    OutputTexture[int2(outX, n0)] = testPixel;
    OutputTexture[int2(outX + 1, n0)] = testPixel; // Write to adjacent pixel too
}
