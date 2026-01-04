using System.Runtime.InteropServices;

namespace BurstPhoto.Rendering;

/// <summary>
/// shared structures matching HLSL Constant Buffers.
/// </summary>

[StructLayout(LayoutKind.Sequential)]
public struct AlignParams
{
    public int Scale;
    public float BlackLevel;
    public float FactorRed;
    public float FactorGreen;
    public float FactorBlue;
    
    // compute_tile_differences
    public int DownscaleFactor;
    public int TileSize;
    public int SearchDist;
    public int WeightSSD;
    
    // warp
    public int HalfTileSize;
    public int NumTilesX;
    public int NumTilesY;
    
    // correct_upsampling_error
    public int UniformExposure;
    
    // Padding to ensure 16-byte alignment if needed, but HLSL CB padding rules apply.
    // Ints and floats are 4 bytes. 
    // Metal/HLSL CBuffers often align to 16 bytes.
    // Let's assume tight packing unless we see issues, or add explicit padding.
    // Current total: 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 = 52 bytes.
    // 52 is not 16-aligned (48, 64). Padding might be needed at end.
    public int Padding0;
    public int Padding1;
    public int Padding2;
}

[StructLayout(LayoutKind.Sequential)]
public struct ExposureParams
{
    public float WhiteLevel;
    public float LinearGain;
    public float ColorFactorMean;
    public float BlackLevelMean;
    public float BlackLevelMin;
    public int ExposureBias;
    public int TargetExposure;
    public int MosaicPatternWidth;
    public int TextureWidth;
    
    public int Padding0;
    public int Padding1;
    public int Padding2;
}

[StructLayout(LayoutKind.Sequential)]
public struct TextureParams
{
    public float WhiteLevel;
    public float BlackLevel;
    public float BlackLevelMean;
    public float ScaleFactor; 
    public int CfaPattern; 
    public int Width;
    public int Height;
    public int OffsetX;
    public int OffsetY;
    public int InputWidth;
    public int InputHeight;
    
    // Preparation
    public int PadLeft;
    public int PadTop;
    public int ExposureDiff;
    public float HotPixelThreshold;
    public float HotPixelMultiplicator;
    public float CorrectionStrength;
    
    // Blur
    public int KernelSize;
    public int MosaicPatternWidth;
    public int TextureSize; 
    public int Direction; 
    
    // Add
    public int NumTextures;
    
    public int Padding0;
    public int Padding1; 
    public int Padding2;
}

[StructLayout(LayoutKind.Sequential)]
public struct SpatialParams
{
    public float WhiteLevel;
    public float BlackLevel;
    public float Robustness;   // Single robustness parameter (was RobustnessParam1/2)
    public float NoiseSd;      // Noise standard deviation
}

