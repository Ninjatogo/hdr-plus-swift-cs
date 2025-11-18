namespace HdrPlus.IO;

/// <summary>
/// Represents a DNG (Digital Negative) RAW image with metadata.
/// </summary>
public class DngImage
{
    /// <summary>
    /// Raw pixel data (16-bit per channel, single channel for Bayer/X-Trans).
    /// </summary>
    public required ushort[] RawData { get; init; }

    /// <summary>
    /// Image width in pixels.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Image height in pixels.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// Mosaic pattern width (2 for Bayer RGGB, 6 for X-Trans).
    /// </summary>
    public required int MosaicPatternWidth { get; init; }

    /// <summary>
    /// Mosaic pattern (e.g., "RGGB" for Bayer, more complex for X-Trans).
    /// </summary>
    public required string MosaicPattern { get; init; }

    /// <summary>
    /// Black level values per color channel.
    /// </summary>
    public required int[] BlackLevels { get; init; }

    /// <summary>
    /// White level (maximum sensor value before clipping).
    /// </summary>
    public required int WhiteLevel { get; init; }

    /// <summary>
    /// Exposure bias value (for bracketed exposures).
    /// </summary>
    public int ExposureBias { get; init; }

    /// <summary>
    /// ISO × Exposure Time (for noise estimation).
    /// </summary>
    public double IsoExposureTime { get; init; }

    /// <summary>
    /// Color correction factors [R, G, B] (for white balance normalization).
    /// </summary>
    public double[] ColorFactors { get; init; } = new double[] { -1, -1, -1 };

    /// <summary>
    /// Camera make and model.
    /// </summary>
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }

    /// <summary>
    /// File path this image was loaded from.
    /// </summary>
    public string? FilePath { get; init; }
}
