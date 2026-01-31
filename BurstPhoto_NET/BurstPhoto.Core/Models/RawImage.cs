namespace BurstPhoto.Core.Models;

/// <summary>
/// Represents a raw image with its pixel data and metadata.
/// </summary>
public class RawImage
{
    /// <summary>Source file path for caching and output filename generation.</summary>
    public string SourcePath { get; set; } = string.Empty;
    
    public int Width { get; set; }
    public int Height { get; set; }
    public ushort[] Data { get; set; } = [];

    // Basic Metadata
    public int MosaicPatternWidth { get; set; }
    public int WhiteLevel { get; set; }
    public int[] BlackLevel { get; set; } = [];
    public int ExposureBias { get; set; }
    public float IsoExposureTime { get; set; }
    public float[] ColorFactors { get; set; } = [];
    
    // DNG-specific Metadata (for proper DNG output)
    /// <summary>CFA pattern array (e.g., RGGB=[0,1,1,2], BGGR=[2,1,1,0])</summary>
    public int[] CfaPattern { get; set; } = [];
    
    /// <summary>Color matrix for D65 illuminant (3x3 = 9 values)</summary>
    public double[] ColorMatrix1 { get; set; } = [];
    
    /// <summary>Color matrix for Standard Light A illuminant (3x3 = 9 values)</summary>
    public double[] ColorMatrix2 { get; set; } = [];
    
    /// <summary>Calibration illuminant 1 (typically 17=StdA or 21=D65)</summary>
    public int CalibrationIlluminant1 { get; set; }
    
    /// <summary>Calibration illuminant 2 (typically 21=D65)</summary>
    public int CalibrationIlluminant2 { get; set; }
    
    /// <summary>As-shot neutral white balance (RGB values)</summary>
    public double[] AsShotNeutral { get; set; } = [];
    
    /// <summary>Camera make/manufacturer</summary>
    public string CameraMake { get; set; } = string.Empty;
    
    /// <summary>Camera model name</summary>
    public string CameraModel { get; set; } = string.Empty;
    
    /// <summary>Indicates if Data contains raw Bayer/CFA data (true) or demosaiced RGB (false)</summary>
    public bool IsBayerData { get; set; }
}
