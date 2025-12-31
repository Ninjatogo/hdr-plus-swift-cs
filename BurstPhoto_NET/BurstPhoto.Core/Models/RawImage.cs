namespace BurstPhoto.Core.Models;

public class RawImage
{
    public int Width { get; set; }
    public int Height { get; set; }
    public ushort[] Data { get; set; } = Array.Empty<ushort>();

    // Metadata
    public int MosaicPatternWidth { get; set; }
    public int WhiteLevel { get; set; }
    public int[] BlackLevel { get; set; } = Array.Empty<int>();
    public int ExposureBias { get; set; }
    public float IsoExposureTime { get; set; }
    public float[] ColorFactors { get; set; } = Array.Empty<float>();
}
