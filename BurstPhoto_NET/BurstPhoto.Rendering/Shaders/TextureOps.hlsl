/*
 * TextureOps.hlsl
 * Port of texture.metal
 */

#include "Constants.hlsli"

// -------------------------------------------------------------------------
// Constant Buffers
// -------------------------------------------------------------------------

// Explicit Vulkan binding: Binding 0, Set 0
[[vk::binding(0, 0)]]
cbuffer TextureParams : register(b0)
{
    float WhiteLevel;
    float BlackLevel;
    float BlackLevelMean;
    float ScaleFactor;
    int CfaPattern;
    int Width;
    int Height;
    int OffsetX;
    int OffsetY;
    int InputWidth;
    int InputHeight;

    // Preparation Params
    int PadLeft;
    int PadTop;
    int ExposureDiff;
    float HotPixelThreshold;
    float HotPixelMultiplicator;
    float CorrectionStrength;

    // Blur Params
    int KernelSize;
    int MosaicPatternWidth;
    int TextureSize; // Width or Height depending on direction?
    int Direction; // 0=X, 1=Y

    // Add Texture Params
    int NumTextures;
};

// -------------------------------------------------------------------------
// Resources
// -------------------------------------------------------------------------
// NOTE: Using explicit [[vk::binding]] attributes to match C# descriptor layout.
// C# binds resources at: Binding 0 (b0), Binding 1 (t0), Binding 3 (t2), Binding 10 (u10), Binding 12 (u12).
// register(tN) syntax is kept for HLSL compatibility but overridden by [[vk::binding]] for Vulkan.

// Generic IO - Sampled Images (read-only textures)
[[vk::binding(1, 0)]]
Texture2D<float> InTextureFloat  : register(t0);

[[vk::binding(2, 0)]]
Texture2D<uint> InTextureUint    : register(t1);

[[vk::binding(3, 0)]]
Texture2D<float4> InTextureRGBA  : register(t2);

[[vk::binding(4, 0)]]
Texture2D<float> AuxTextureFloat : register(t3); // Weight map etc.

[[vk::binding(5, 0)]]
StructuredBuffer<float> MeanTextureBuffer : register(t4);

[[vk::binding(6, 0)]]
StructuredBuffer<float> BlackLevels : register(t5);

// Storage Images (read-write textures)
[[vk::binding(10, 0)]]
RWTexture2D<float> OutTextureFloat : register(u10);

[[vk::binding(11, 0)]]
RWTexture2D<uint> OutTextureUint   : register(u11);

[[vk::binding(12, 0)]]
// NOTE: image_format attribute removed - requires ShaderStorageImageWriteWithoutFormat feature instead
// [[vk::image_format("rgba32f")]]  // This causes DXC compilation errors on some systems
RWTexture2D<float4> OutTextureRGBA : register(u12);

// -------------------------------------------------------------------------
// Basics
// -------------------------------------------------------------------------

[numthreads(16, 16, 1)]
void fill_with_zeros(uint3 DTid : SV_DispatchThreadID)
{
    OutTextureFloat[DTid.xy] = 0.0f;
}

[numthreads(16, 16, 1)]
void copy_texture(uint3 DTid : SV_DispatchThreadID)
{
    float val = InTextureFloat.Load(int3(DTid.xy, 0));
    OutTextureFloat[DTid.xy] = val;
}

[numthreads(16, 16, 1)]
void crop_texture(uint3 DTid : SV_DispatchThreadID)
{
    uint2 inPos = DTid.xy + uint2(OffsetX, OffsetY);
    float val = InTextureFloat.Load(int3(inPos, 0));
    OutTextureFloat[DTid.xy] = val;
}

// -------------------------------------------------------------------------
// Conversions
// -------------------------------------------------------------------------

[numthreads(16, 16, 1)]
void convert_to_rgba(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;
    
    // Apply padding offset to skip the padded border and read from actual image data
    // Swift uses: x = gid.x*2 + crop_x, y = gid.y*2 + crop_y
    uint2 inPos = uint2(gid.x * 2 + PadLeft, gid.y * 2 + PadTop);

    // Direct pack: Read 4 adjacent Bayer pixels into RGBA channels
    // No averaging, no demosaicing - just pack the raw values
    float p0 = InTextureFloat.Load(int3(inPos.x,   inPos.y,   0));
    float p1 = InTextureFloat.Load(int3(inPos.x+1, inPos.y,   0));
    float p2 = InTextureFloat.Load(int3(inPos.x,   inPos.y+1, 0));
    float p3 = InTextureFloat.Load(int3(inPos.x+1, inPos.y+1, 0));

    // Pack all 4 values directly (no averaging, no CFA interpretation)
    // This preserves all raw Bayer data for FFT processing
    OutTextureRGBA[gid] = float4(p0, p1, p2, p3);
}

// DEBUG_CONVERT_TO_BAYER modes:
// 0 = Normal operation
// 1 = Output X coordinate as gradient (to detect X-axis mirroring)
// 2 = Output Y coordinate as gradient (to detect Y-axis mirroring)
// 3 = Output block ID to show tile boundaries
#define DEBUG_CONVERT_TO_BAYER 0

[numthreads(16, 16, 1)]
void convert_to_bayer(uint3 DTid : SV_DispatchThreadID)
{
    uint2 gid = DTid.xy;

#if DEBUG_CONVERT_TO_BAYER == 1
    // X gradient: value increases linearly with X coordinate
    // If mirrored, you'll see the gradient reverse within blocks
    OutTextureFloat[gid] = (float)gid.x;
    return;
#elif DEBUG_CONVERT_TO_BAYER == 2
    // Y gradient
    OutTextureFloat[gid] = (float)gid.y;
    return;
#elif DEBUG_CONVERT_TO_BAYER == 3
    // Block ID pattern - shows which 16x16 workgroup each pixel belongs to
    uint blockX = gid.x / 16;
    uint blockY = gid.y / 16;
    OutTextureFloat[gid] = (float)(blockX * 1000 + blockY);
    return;
#endif

    uint2 inPos = gid / 2;
    float4 rgba = InTextureRGBA.Load(int3(inPos, 0));

    // Determine which pixel in the 2x2 block we're unpacking
    uint x = gid.x % 2;
    uint y = gid.y % 2;

    // Unpack: RGBA channels back to 2x2 Bayer positions
    // This is the exact inverse of convert_to_rgba
    float val = 0.0f;
    if (x == 0 && y == 0) val = rgba.r;      // Top-left
    else if (x == 1 && y == 0) val = rgba.g; // Top-right
    else if (x == 0 && y == 1) val = rgba.b; // Bottom-left
    else if (x == 1 && y == 1) val = rgba.a; // Bottom-right

    OutTextureFloat[gid] = val;
}

[numthreads(16, 16, 1)]
void convert_float_to_uint16(uint3 DTid : SV_DispatchThreadID)
{
    float val = InTextureFloat.Load(int3(DTid.xy, 0));
    val = clamp(val, 0.0f, (float)UINT16_MAX_VAL);
    OutTextureUint[DTid.xy] = (uint)(val); 
}

[numthreads(16, 16, 1)]
void upsample_nearest_int(uint3 DTid : SV_DispatchThreadID)
{
    uint val = InTextureUint.Load(int3(DTid.xy / 2, 0));
    OutTextureUint[DTid.xy] = val;
}

[numthreads(16, 16, 1)]
void upsample_bilinear_float(uint3 DTid : SV_DispatchThreadID)
{
    float2 uv = (float2(DTid.xy) + 0.5f) / 2.0f; 
    float u = uv.x - 0.5f;
    float v = uv.y - 0.5f;
    int x = floor(u);
    int y = floor(v);
    float wx = u - x;
    float wy = v - y;
    
    float v00 = InTextureFloat.Load(int3(x, y, 0));
    float v10 = InTextureFloat.Load(int3(x+1, y, 0));
    float v01 = InTextureFloat.Load(int3(x, y+1, 0));
    float v11 = InTextureFloat.Load(int3(x+1, y+1, 0));
    
    float top = lerp(v00, v10, wx);
    float bot = lerp(v01, v11, wx);
    float val = lerp(top, bot, wy);
    
    OutTextureFloat[DTid.xy] = val;
}

// -------------------------------------------------------------------------
// Accumulation
// -------------------------------------------------------------------------

[numthreads(16, 16, 1)]
void add_texture(uint3 DTid : SV_DispatchThreadID)
{
    // t0=Input, u0=Accumulator (RW)
    float val = InTextureFloat.Load(int3(DTid.xy, 0));
    float acc = OutTextureFloat[DTid.xy]; // Read RW
    // Metal: out_texture += in_texture / n_textures
    float n = (float)NumTextures;
    OutTextureFloat[DTid.xy] = acc + val / n;
}

[numthreads(16, 16, 1)]
void add_texture_rgba(uint3 DTid : SV_DispatchThreadID)
{
    // t2=Input RGBA, u12=Accumulator RGBA (RW)
    // Metal: out_texture += in_texture.r / n_textures (single channel)
    // Swift mismatch textures use only .r channel
    float4 inVal = InTextureRGBA.Load(int3(DTid.xy, 0));
    float4 acc = OutTextureRGBA[DTid.xy]; // Read RW
    float n = (float)NumTextures;
    // Only the .r channel matters for mismatch, but accumulate all for consistency
    OutTextureRGBA[DTid.xy] = acc + inVal / n;
}

[numthreads(16, 16, 1)]
void add_texture_weighted(uint3 DTid : SV_DispatchThreadID)
{
    // t0=Input, t3=Weight, u0=Accumulator
    // Metal: out += in * weight / n? No, just weighted?
    // Metal: 'pixel_value = in_texture.read(gid).r; float const weight = weight_texture.read(gid).r; ... out_texture.write(out + weight*pixel_value, gid)'
    
    float val = InTextureFloat.Load(int3(DTid.xy, 0));
    float weight = AuxTextureFloat.Load(int3(DTid.xy, 0));
    float acc = OutTextureFloat[DTid.xy];
    OutTextureFloat[DTid.xy] = acc + val * weight;
}

// Need a second float accumulator for weights. 
// u10 is PixelAccum (OutTextureFloat)
// u11 is OutTextureUint (uint) -> cannot use.
// u12 is OutTextureRGBA (float4) -> cannot use.
// Let's add u13
[[vk::binding(13, 0)]]
RWTexture2D<float> OutWeightAccum : register(u13);

[numthreads(16, 16, 1)]
void add_weight_only(uint3 DTid : SV_DispatchThreadID)
{
    // t3=Weight, u10=WeightAccumulator (RW)
    float weight = AuxTextureFloat.Load(int3(DTid.xy, 0));
    float acc = OutTextureFloat[DTid.xy];
    OutTextureFloat[DTid.xy] = acc + weight;
}

[numthreads(16, 16, 1)]
void add_texture_exposure(uint3 DTid : SV_DispatchThreadID)
{
    // t0=Input (Warped Frame), t3=Weight, u10=PixelAccumulator
    // Params: ScaleFactor, WhiteLevel, BlackLevel, BlackLevelMean
    
    // Original Logic (Swift):
    // float factor = params.scale_factor;
    // float white_level = params.white_level;
    // float black_level = params.black_level;
    // ...
    // pixel_value = (pixel_value - black_level) * factor + black_level;
    // pixel_value = min(pixel_value, white_level);
    // pixel_value = max(pixel_value, black_level_mean);
    // out_texture += pixel_value * weight;
    
    float val = InTextureFloat.Load(int3(DTid.xy, 0));
    float weight = AuxTextureFloat.Load(int3(DTid.xy, 0));
    float acc = OutTextureFloat[DTid.xy];
    
    // Scale value based on exposure difference
    val = (val - BlackLevel) * ScaleFactor + BlackLevel;
    
    // Clamp to valid range (clipping highlights)
    val = min(val, WhiteLevel);
    val = max(val, BlackLevelMean);
    
    OutTextureFloat[DTid.xy] = acc + val * weight;
}

float calculate_weight_highlights(float val, float white_level, float black_level)
{
    // Swift:
    // float weight_highlights = pow(max(pixel_value - black_level, 0.0) / (white_level - black_level), 4.0);
    // weight_highlights = smoothstep(0.7, 0.9, weight_highlights);
    
    float normalized = max(val - black_level, 0.0f) / max(white_level - black_level, 1e-6f);
    float w = pow(normalized, 4.0f);
    
    // HLSL smoothstep(min, max, x)
    return smoothstep(0.7f, 0.9f, w);
}

[numthreads(16, 16, 1)]
void add_texture_highlights(uint3 DTid : SV_DispatchThreadID)
{
    // t0=Input (Warped Frame)
    // t3=Weight (Alignment Weight)
    // u10=PixelAccumulator (RW)
    // u13=WeightAccumulator (RW) - NEW BINDING
    
    float val = InTextureFloat.Load(int3(DTid.xy, 0));       
    float weight = AuxTextureFloat.Load(int3(DTid.xy, 0));   
    
    // Scale the dark pixel up to match reference exposure
    float scaledVal = (val - BlackLevel) * ScaleFactor + BlackLevel;
    
    // Calculate highlight weight based on the SCALED value
    float w_h = calculate_weight_highlights(scaledVal, WhiteLevel, BlackLevel);
    
    // Combine alignment weight and highlight weight
    float finalWeight = weight * w_h;
    
    if (finalWeight > 0)
    {
         float pAcc = OutTextureFloat[DTid.xy];
         OutTextureFloat[DTid.xy] = pAcc + scaledVal * finalWeight;
         
         float wAcc = OutWeightAccum[DTid.xy];
         OutWeightAccum[DTid.xy] = wAcc + finalWeight;
    }
}


// -------------------------------------------------------------------------
// Preparation
// -------------------------------------------------------------------------

[numthreads(16, 16, 1)]
void find_hotpixels_bayer(uint3 DTid : SV_DispatchThreadID)
{
    // Hot pixel detection for Bayer sensors.
    // Input: t0 = AverageTexture (average of all frames)
    // Output: u10 = HotPixelWeightTexture
    // Buffers: t4 = MeanTextureBuffer (per-channel mean), t5 = BlackLevels
    // Params: HotPixelThreshold, HotPixelMultiplicator, CorrectionStrength
    
    // +2 offset from top-left edge (2-pixel border not analyzed for simplicity)
    int x = DTid.x + 2;
    int y = DTid.y + 2;
    
    // Extract color channel-dependent black level and mean texture value
    // For Bayer 2x2 pattern: index = (x%2) + 2*(y%2)
    int ix = x % 2;
    int iy = y % 2;
    float black_level = BlackLevels[ix + 2 * iy];
    float mean_texture = MeanTextureBuffer[ix + 2 * iy] - black_level;
    
    // Calculate weighted sum of 8 surrounding same-color Bayer pixels
    // For Bayer, same-color neighbors are at distance 2 in x/y
    // Corner pixels (distance 2*sqrt(2)): weight = 1
    // Horizontal/vertical neighbors (distance 2): weight = 2
    float sum = 0.0f;
    sum +=     InTextureFloat.Load(int3(x - 2, y - 2, 0)); // top-left corner
    sum +=     InTextureFloat.Load(int3(x + 2, y - 2, 0)); // top-right corner
    sum +=     InTextureFloat.Load(int3(x - 2, y + 2, 0)); // bottom-left corner
    sum +=     InTextureFloat.Load(int3(x + 2, y + 2, 0)); // bottom-right corner
    sum += 2 * InTextureFloat.Load(int3(x - 2, y,     0)); // left
    sum += 2 * InTextureFloat.Load(int3(x + 2, y,     0)); // right
    sum += 2 * InTextureFloat.Load(int3(x,     y - 2, 0)); // top
    sum += 2 * InTextureFloat.Load(int3(x,     y + 2, 0)); // bottom
    
    sum /= 12.0f; // Total weight = 4*1 + 4*2 = 12
    
    // Extract value of potential hot pixel from the average texture
    float pixel_value = InTextureFloat.Load(int3(x, y, 0));
    
    // Calculate ratio: how much brighter is this pixel vs its neighbors?
    float pixel_ratio = max(1.0f, pixel_value - black_level) / max(1.0f, sum - black_level);
    
    // Hot pixel detected if:
    // 1. pixel_ratio >= threshold (pixel is significantly brighter than neighbors)
    // 2. pixel_value >= 2 * mean_texture (pixel is bright enough to matter)
    if (pixel_ratio >= HotPixelThreshold && pixel_value >= 2.0f * mean_texture)
    {
        // Calculate blending weight for smooth transition on borderline hot pixels
        float weight = 0.5f * CorrectionStrength * min(2.0f, 
            HotPixelMultiplicator * (pixel_ratio - HotPixelThreshold));
        OutTextureFloat[uint2(x, y)] = weight;
    }
    // Otherwise, weight stays at 0 (texture should be pre-zeroed)
}

[numthreads(16, 16, 1)]
void prepare_texture_bayer(uint3 DTid : SV_DispatchThreadID)
{
    // Port of Metal prepare_texture_bayer
    // DTid is INPUT coordinate (0 to input_width-1, 0 to input_height-1)
    // Metal: gid is thread position in input texture
    // Read from input at gid, write to output at gid + padding

    uint2 gid = DTid.xy;
    int x = gid.x;
    int y = gid.y;

    uint width, height;
    InTextureUint.GetDimensions(width, height);

    // Read pixel value from input texture
    float pixel_value = (float)InTextureUint.Load(int3(gid, 0));

    // Read hotpixel weight
    float hotpixel_weight = AuxTextureFloat.Load(int3(gid, 0));

    // Apply hotpixel correction if needed
    if (hotpixel_weight > 0.001f && x >= 2 && x < width-2 && y >= 2 && y < height-2)
    {
        // Calculate mean value of 4 surrounding same-color pixels (Bayer distance=2)
        float sum = 0.0f;
        sum += (float)InTextureUint.Load(int3(x-2, y, 0));
        sum += (float)InTextureUint.Load(int3(x+2, y, 0));
        sum += (float)InTextureUint.Load(int3(x, y-2, 0));
        sum += (float)InTextureUint.Load(int3(x, y+2, 0));

        // Blend values and replace hot pixel
        pixel_value = hotpixel_weight * 0.25f * sum + (1.0f - hotpixel_weight) * pixel_value;
    }

    // Calculate exposure correction factor
    float corr_factor = pow(2.0f, (float)ExposureDiff / 100.0f);
    float black_level = BlackLevels[(gid.y % 2) * 2 + (gid.x % 2)];

    // Correct exposure
    pixel_value = (pixel_value - black_level) * corr_factor + black_level;
    pixel_value = max(pixel_value, 0.0f);

    // Write to output texture with padding offset (matches Metal: gid.x+pad_left, gid.y+pad_top)
    OutTextureFloat[uint2(gid.x + PadLeft, gid.y + PadTop)] = pixel_value;
}

[numthreads(16, 16, 1)]
void blur_mosaic_texture(uint3 DTid : SV_DispatchThreadID)
{
    // Binomial filter weights - identity for kernel_size=0
    float bw[9] = {1, 0, 0, 0, 0, 0, 0, 0, 0};
    int kernel_size_trunc = KernelSize;
    
    // Hardcoded binomial weights (matching Swift texture.metal)
    // Truncated such that removed tail contributes < 0.25%
    if (KernelSize == 1)       { bw[0] = 2;     bw[1] = 1; }
    else if (KernelSize == 2)  { bw[0] = 6;     bw[1] = 4;     bw[2] = 1; }
    else if (KernelSize == 3)  { bw[0] = 20;    bw[1] = 15;    bw[2] = 6;    bw[3] = 1; }
    else if (KernelSize == 4)  { bw[0] = 70;    bw[1] = 56;    bw[2] = 28;   bw[3] = 8;    bw[4] = 1; }
    else if (KernelSize == 5)  { bw[0] = 252;   bw[1] = 210;   bw[2] = 120;  bw[3] = 45;   bw[4] = 10;  kernel_size_trunc = 4; }
    else if (KernelSize == 6)  { bw[0] = 924;   bw[1] = 792;   bw[2] = 495;  bw[3] = 220;  bw[4] = 66;  bw[5] = 12; kernel_size_trunc = 5; }
    else if (KernelSize == 7)  { bw[0] = 3432;  bw[1] = 3003;  bw[2] = 2002; bw[3] = 1001; bw[4] = 364; bw[5] = 91; kernel_size_trunc = 5; }
    else if (KernelSize == 8)  { bw[0] = 12870; bw[1] = 11440; bw[2] = 8008; bw[3] = 4368; bw[4] = 1820;bw[5] = 560;bw[6] = 120; kernel_size_trunc = 6; }
    else if (KernelSize == 16) { bw[0] = 601080390; bw[1] = 565722720; bw[2] = 471435600; bw[3] = 347373600; bw[4] = 225792840; bw[5] = 129024480; bw[6] = 64512240; bw[7] = 28048800; bw[8] = 10518300; kernel_size_trunc = 8; }
    
    float total_intensity = 0.0f;
    float total_weight = 0.0f;
    
    // Direction = 0: blur in X, Direction = 1: blur in Y
    int i0 = (Direction == 0) ? (int)DTid.x : (int)DTid.y;
    int fixed_coord = (Direction == 0) ? (int)DTid.y : (int)DTid.x;
    
    for (int di = -kernel_size_trunc; di <= kernel_size_trunc; di++)
    {
        int i = i0 + MosaicPatternWidth * di;
        if (i >= 0 && i < TextureSize)
        {
            int2 xy = (Direction == 0) ? int2(i, fixed_coord) : int2(fixed_coord, i);
            float weight = bw[abs(di)];
            total_intensity += weight * InTextureFloat.Load(int3(xy, 0));
            total_weight += weight;
        }
    }
    
    OutTextureFloat[DTid.xy] = total_intensity / total_weight;
}

// -------------------------------------------------------------------------
// X-Trans Sensor Support
// -------------------------------------------------------------------------

// 6x6 offset lookup table for X-Trans mosaic pattern
// Maps each position to its 4 closest same-color neighbors [4][2] = {dx, dy}
// Ported from Swift texture.metal find_hotpixels_xtrans
static const int XTRANS_OFFSET[6][6][4][2] = {
    { // Row 0
        {{ 0, -1}, { 1, -1}, { 1,  0}, {-1,  1}}, // G
        {{ 0, -1}, { 1,  1}, {-1,  0}, {-1, -1}}, // G
        {{ 1, -2}, { 2,  1}, { 0,  2}, {-2,  1}}, // B
        {{ 0, -1}, { 1, -1}, { 1,  0}, {-1,  1}}, // G
        {{ 0, -1}, { 1,  1}, {-1,  0}, {-1, -1}}, // G
        {{ 1, -2}, { 2,  1}, { 0,  2}, {-2,  1}}  // R
    },
    { // Row 1
        {{-1,  2}, {-2,  0}, {-1, -2}, { 2, -1}}, // B
        {{ 1, -2}, { 2,  0}, { 1,  2}, {-2,  1}}, // R
        {{ 1, -1}, { 1,  1}, {-1,  1}, {-1, -1}}, // G
        {{ 2, -1}, { 2,  1}, {-1,  2}, {-2,  0}}, // R
        {{ 1, -2}, { 2,  0}, { 1,  2}, {-2,  1}}, // B
        {{ 1, -1}, { 1,  1}, {-1,  1}, {-1, -1}}  // G
    },
    { // Row 2
        {{ 1,  0}, { 1,  1}, { 0,  1}, {-1,  1}}, // G
        {{ 1, -1}, { 0,  1}, {-1,  1}, {-1,  0}}, // G
        {{-2, -1}, { 0, -2}, { 2, -1}, { 1, -2}}, // B
        {{ 1,  0}, { 1,  1}, { 0,  1}, {-1,  1}}, // G
        {{ 1, -1}, { 0,  1}, {-1,  1}, {-1,  0}}, // G
        {{-2, -1}, { 0, -2}, { 2, -1}, { 1, -2}}  // R
    },
    { // Row 3
        {{ 0, -1}, { 1, -1}, { 1,  0}, {-1,  1}}, // G
        {{ 0, -1}, { 1,  1}, {-1,  0}, {-1, -1}}, // G
        {{ 1, -2}, { 2,  1}, { 0,  2}, {-2,  1}}, // R
        {{ 0, -1}, { 1, -1}, { 1,  0}, {-1,  1}}, // G
        {{ 0, -1}, { 1,  1}, {-1,  0}, {-1, -1}}, // G
        {{ 1, -2}, { 2,  1}, { 0,  2}, {-2,  1}}  // B
    },
    { // Row 4
        {{-1,  2}, {-2,  0}, {-1, -2}, { 2, -1}}, // R
        {{ 1, -2}, { 2,  0}, { 1,  2}, {-2,  1}}, // B
        {{ 1, -1}, { 1,  1}, {-1,  1}, {-1, -1}}, // G
        {{ 2, -1}, { 2,  1}, {-1,  2}, {-2,  0}}, // B
        {{ 1, -2}, { 2,  0}, { 1,  2}, {-2,  1}}, // R
        {{ 1, -1}, { 1,  1}, {-1,  1}, {-1, -1}}  // G
    },
    { // Row 5
        {{ 1,  0}, { 1,  1}, { 0,  1}, {-1,  1}}, // G
        {{ 1, -1}, { 0,  1}, {-1,  1}, {-1,  0}}, // G
        {{-2, -1}, { 0, -2}, { 2, -1}, { 1, -2}}, // R
        {{ 1,  0}, { 1,  1}, { 0,  1}, {-1,  1}}, // G
        {{ 1, -1}, { 0,  1}, {-1,  1}, {-1,  0}}, // G
        {{-2, -1}, { 0, -2}, { 2, -1}, { 1, -2}}  // B
    }
};

[numthreads(16, 16, 1)]
void find_hotpixels_xtrans(uint3 DTid : SV_DispatchThreadID)
{
    // Hot pixel detection for X-Trans sensors.
    // Uses 6x6 pattern lookup to find same-color neighbors.
    // Input: t0 = AverageTexture (average of all frames)
    // Output: u10 = HotPixelWeightTexture
    // Buffers: t4 = MeanTextureBuffer (per-channel mean, 36 entries), t5 = BlackLevels (36 entries)
    
    // +2 offset from top-left edge (2-pixel border not analyzed)
    int x = DTid.x + 2;
    int y = DTid.y + 2;
    
    // X-Trans pattern indices (6x6 repeat)
    int ix = x % 6;
    int iy = y % 6;
    
    float black_level = BlackLevels[ix + 6 * iy];
    float mean_texture = MeanTextureBuffer[ix + 6 * iy] - black_level;
    
    // Weighted average of 4 nearest same-color neighbors
    float sum = 0.0f;
    float total = 0.0f;
    
    for (int off = 0; off < 4; off++)
    {
        int dx = XTRANS_OFFSET[iy][ix][off][0];
        int dy = XTRANS_OFFSET[iy][ix][off][1];
        float dist = sqrt((float)(dx * dx + dy * dy));
        float weight = 1.0f / dist;
        
        total += weight;
        sum += weight * InTextureFloat.Load(int3(x + dx, y + dy, 0));
    }
    sum /= total;
    
    // Extract value of potential hot pixel
    float pixel_value = InTextureFloat.Load(int3(x, y, 0));
    float pixel_ratio = max(1.0f, pixel_value - black_level) / max(1.0f, sum - black_level);
    
    // Hot pixel detected if ratio >= threshold AND pixel is bright enough
    if (pixel_ratio >= HotPixelThreshold && pixel_value >= 2.0f * mean_texture)
    {
        float weight = 0.5f * CorrectionStrength * min(2.0f,
            HotPixelMultiplicator * (pixel_ratio - HotPixelThreshold));
        OutTextureFloat[uint2(x, y)] = weight;
    }
}

[numthreads(16, 16, 1)]
void prepare_texture_xtrans(uint3 DTid : SV_DispatchThreadID)
{
    // Prepare texture for X-Trans sensors with hot pixel correction and exposure normalization.
    // t1=InTextureUint, t3=AuxTextureFloat (hot pixel weight), u10=OutTextureFloat
    
    uint2 gid = DTid.xy;
    int x = gid.x;
    int y = gid.y;
    int ix = x % 6;
    int iy = y % 6;
    
    uint width, height;
    InTextureUint.GetDimensions(width, height);
    
    float pixel_value = (float)InTextureUint.Load(int3(gid, 0));
    float hp_weight = AuxTextureFloat.Load(int3(gid, 0));
    
    // Hot pixel correction using distance-weighted neighbors
    if (hp_weight > 0.001f && x >= 2 && x < (int)width - 2 && y >= 2 && y < (int)height - 2)
    {
        float sum = 0.0f;
        float total = 0.0f;
        
        for (int off = 0; off < 4; off++)
        {
            int dx = XTRANS_OFFSET[iy][ix][off][0];
            int dy = XTRANS_OFFSET[iy][ix][off][1];
            float dist = sqrt((float)(dx * dx + dy * dy));
            float weight = 1.0f / dist;
            
            total += weight;
            sum += weight * (float)InTextureUint.Load(int3(x + dx, y + dy, 0));
        }
        sum /= total;
        
        // Blend hot pixel with interpolated value
        pixel_value = hp_weight * sum + (1.0f - hp_weight) * pixel_value;
    }
    
    // Exposure correction
    float corr = pow(2.0f, (float)ExposureDiff / 100.0f);
    float bl = BlackLevels[ix + 6 * iy];
    
    pixel_value = (pixel_value - bl) * corr + bl;
    pixel_value = max(pixel_value, 0.0f);
    
    OutTextureFloat[uint2(x + PadLeft, y + PadTop)] = pixel_value;
}

// -------------------------------------------------------------------------
// Noise Estimation Shaders
// -------------------------------------------------------------------------

// Color difference for noise estimation
// Computes the sum of absolute differences per superpixel (mosaic pattern block)
// This calculates the difference between original and blurred texture
[numthreads(16, 16, 1)]
void color_difference_superpixel(uint3 DTid : SV_DispatchThreadID)
{
    // Each thread processes one superpixel (e.g., 2x2 for Bayer)
    // Input: t0 = Texture1 (original), t1 (via t2) = Texture2 (blurred)
    // Output: u10 = difference per superpixel
    
    float total_diff = 0.0f;
    int x0 = DTid.x * MosaicPatternWidth;
    int y0 = DTid.y * MosaicPatternWidth;
    
    for (int dy = 0; dy < MosaicPatternWidth; dy++)
    {
        for (int dx = 0; dx < MosaicPatternWidth; dx++)
        {
            int x = x0 + dx;
            int y = y0 + dy;
            float val1 = InTextureFloat.Load(int3(x, y, 0));
            float val2 = AuxTextureFloat.Load(int3(x, y, 0)); // Blurred texture via AuxTextureFloat
            total_diff += abs(val1 - val2);
        }
    }
    
    OutTextureFloat[DTid.xy] = total_diff;
}

// Sum columns for texture mean calculation
// Reduces texture in Y direction, grouping by mosaic pattern
// Output is (width, mosaic_pattern_width)
[numthreads(16, 16, 1)]
void sum_rect_columns_float(uint3 DTid : SV_DispatchThreadID)
{
    // DTid.x = column index, DTid.y = mosaic row index (0 to MosaicPatternWidth-1)
    // We sum all pixels at column DTid.x where (y % MosaicPatternWidth) == DTid.y
    
    uint inWidth, inHeight;
    InTextureFloat.GetDimensions(inWidth, inHeight);
    
    float total = 0.0f;
    for (uint y = DTid.y; y < inHeight; y += MosaicPatternWidth)
    {
        total += InTextureFloat.Load(int3(DTid.x, y, 0));
    }
    
    OutTextureFloat[DTid.xy] = total;
}

// Sum rows for texture mean calculation
// Reduces from (width, mosaic_pattern_width) to (mosaic_pattern_width, mosaic_pattern_width) buffer
// Output is stored to a buffer
[numthreads(16, 16, 1)]
void sum_row_to_buffer(uint3 DTid : SV_DispatchThreadID)
{
    // DTid.x = mosaic column index (0 to MosaicPatternWidth-1)
    // DTid.y = mosaic row index
    // We sum all values at row DTid.y where (x % MosaicPatternWidth) == DTid.x

    uint inWidth, inHeight;
    InTextureFloat.GetDimensions(inWidth, inHeight);

    float total = 0.0f;
    for (uint x = DTid.x; x < inWidth; x += MosaicPatternWidth)
    {
        total += InTextureFloat.Load(int3(x, DTid.y, 0));
    }

    // Write to a single output pixel (which will be read back to CPU)
    // Store at (DTid.x, DTid.y) position in output
    OutTextureFloat[DTid.xy] = total;
}

// -------------------------------------------------------------------------
// GPU-side Accumulator Blit
// -------------------------------------------------------------------------
// Copies a cropped region from source texture and adds it to accumulator.
// This replaces the CPU-based accumulation loop, eliminating GPU round-trips.
//
// Dispatch: (croppedWidth, croppedHeight, 1) threads
// Params used: OffsetX, OffsetY (source crop offset), PadLeft, PadTop (dest offset), InputWidth (source stride)

[numthreads(16, 16, 1)]
void accumulate_cropped_region(uint3 DTid : SV_DispatchThreadID)
{
    // DTid.xy is the position within the cropped region (0 to croppedWidth-1, 0 to croppedHeight-1)
    uint2 croppedPos = DTid.xy;

    // Calculate source position (in source texture with crop offset)
    // OffsetX, OffsetY = cropLeft, cropTop from the source
    uint2 srcPos = uint2(croppedPos.x + OffsetX, croppedPos.y + OffsetY);

    // Calculate destination position (in accumulator with padding offset)
    // PadLeft, PadTop = destination padding offset
    uint2 dstPos = uint2(croppedPos.x + PadLeft, croppedPos.y + PadTop);

    // Read from source (InTextureFloat bound to source Bayer texture)
    float srcVal = InTextureFloat.Load(int3(srcPos, 0));

    // Read current accumulator value and add
    float accVal = OutTextureFloat[dstPos];
    OutTextureFloat[dstPos] = accVal + srcVal;
}

