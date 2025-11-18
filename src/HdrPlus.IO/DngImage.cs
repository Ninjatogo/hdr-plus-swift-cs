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

    // ==================== PHASE 5 ENHANCEMENTS ====================

    /// <summary>
    /// Color calibration matrix 1 (3x3 matrix, stored row-major).
    /// Maps reference camera RGB to XYZ under illuminant 1.
    /// </summary>
    public double[]? ColorMatrix1 { get; init; }

    /// <summary>
    /// Color calibration matrix 2 (3x3 matrix, stored row-major).
    /// Maps reference camera RGB to XYZ under illuminant 2.
    /// </summary>
    public double[]? ColorMatrix2 { get; init; }

    /// <summary>
    /// Camera calibration matrix 1 (3x3 matrix, stored row-major).
    /// Maps camera color space to reference camera color space.
    /// </summary>
    public double[]? CameraCalibration1 { get; init; }

    /// <summary>
    /// Camera calibration matrix 2 (3x3 matrix, stored row-major).
    /// </summary>
    public double[]? CameraCalibration2 { get; init; }

    /// <summary>
    /// As-shot neutral color coordinates [R, G, B].
    /// White balance multipliers normalized so green = 1.0.
    /// </summary>
    public double[]? AsShotNeutral { get; init; }

    /// <summary>
    /// Analog balance values [R, G, B] for white balance.
    /// </summary>
    public double[]? AnalogBalance { get; init; }

    /// <summary>
    /// CFA (Color Filter Array) pattern as byte array.
    /// For Bayer: [0,1,1,2] = [Red, Green, Green, Blue]
    /// </summary>
    public byte[]? CfaPattern { get; init; }

    /// <summary>
    /// CFA plane color (channels): 0=Red, 1=Green, 2=Blue
    /// </summary>
    public byte[]? CfaPlaneColor { get; init; }

    /// <summary>
    /// CFA layout: 1=Rectangular, 2=Staggered A, 3=Staggered B
    /// </summary>
    public ushort CfaLayout { get; init; } = 1;

    /// <summary>
    /// Baseline exposure compensation value.
    /// </summary>
    public double BaselineExposure { get; init; }

    /// <summary>
    /// Baseline noise (sensor read noise).
    /// </summary>
    public double BaselineNoise { get; init; } = 1.0;

    /// <summary>
    /// Baseline sharpness value.
    /// </summary>
    public double BaselineSharpness { get; init; } = 1.0;

    /// <summary>
    /// Linear response limit (maximum useful sensor value).
    /// </summary>
    public double LinearResponseLimit { get; init; } = 1.0;

    /// <summary>
    /// EXIF metadata dictionary (tag -> value).
    /// Preserves all EXIF tags for lossless round-trip.
    /// </summary>
    public Dictionary<string, string>? ExifData { get; init; }

    /// <summary>
    /// XMP metadata as XML string.
    /// </summary>
    public string? XmpMetadata { get; init; }

    /// <summary>
    /// Camera serial number.
    /// </summary>
    public string? CameraSerialNumber { get; init; }

    /// <summary>
    /// Lens make and model.
    /// </summary>
    public string? LensMake { get; init; }
    public string? LensModel { get; init; }

    /// <summary>
    /// Exposure time in seconds.
    /// </summary>
    public double? ExposureTime { get; init; }

    /// <summary>
    /// F-number (aperture).
    /// </summary>
    public double? FNumber { get; init; }

    /// <summary>
    /// ISO sensitivity.
    /// </summary>
    public int? IsoSpeed { get; init; }

    /// <summary>
    /// Focal length in mm.
    /// </summary>
    public double? FocalLength { get; init; }

    /// <summary>
    /// Date/time original (when photo was taken).
    /// </summary>
    public DateTime? DateTimeOriginal { get; init; }

    /// <summary>
    /// Unique camera ID for burst matching.
    /// </summary>
    public string? UniqueCameraModel { get; init; }
}
