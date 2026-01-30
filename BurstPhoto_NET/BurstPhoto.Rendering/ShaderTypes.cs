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

    // Warp clamping params (to prevent reads into zero-padding region)
    public int PadLeft;
    public int PadTop;
    public int ImageWidth;   // Total image width (including padding)
    public int ImageHeight;  // Total image height (including padding)
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
    public int Padding3; // Added extra padding for alignment safety if needed
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

[StructLayout(LayoutKind.Sequential)]
public struct FrequencyParams
{
    public float RobustnessNorm;
    public float ReadNoise;
    public float MaxMotionNorm;
    public int TileSize;
    public int UniformExposure;

    // Additional params
    public int NumTextures;
    public float ExposureFactor;
    public float WhiteLevel;
    public float BlackLevelMean;
    public float MeanMismatch;

    // Per-channel black levels for reduce_artifacts_tile_border
    public int BlackLevel0;
    public int BlackLevel1;
    public int BlackLevel2;
    public int BlackLevel3;
}
