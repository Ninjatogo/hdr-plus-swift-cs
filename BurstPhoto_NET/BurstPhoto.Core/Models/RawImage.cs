namespace BurstPhoto.Core.Models;

/// <summary>
/// Represents a raw image with its pixel data and associated metadata.
/// </summary>
/// <remarks>
/// This class holds both the raw sensor data and all metadata required for processing
/// and writing valid DNG output files. The metadata is typically populated by an
/// <see cref="BurstPhoto.Core.Interfaces.IRawImageLoader"/> when reading raw files.
/// </remarks>
public class RawImage
{
    #region Image Dimensions and Data

    /// <summary>
    /// Gets or sets the source file path from which this image was loaded.
    /// </summary>
    /// <remarks>
    /// Used for caching, output filename generation, and metadata cloning when writing DNG files.
    /// Must be set before calling certain writer implementations.
    /// </remarks>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the image width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the image height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the raw pixel data as 16-bit unsigned values.
    /// </summary>
    /// <remarks>
    /// For Bayer/CFA images, this contains one value per sensor photosite.
    /// Length should equal Width × Height for Bayer data.
    /// Values range from 0 to <see cref="WhiteLevel"/>.
    /// </remarks>
    public ushort[] Data { get; set; } = [];

    #endregion

    #region Sensor Metadata

    /// <summary>
    /// Gets or sets the width of the color filter array (CFA) mosaic pattern.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>2 = Bayer pattern (RGGB, BGGR, GRBG, GBRG)</description></item>
    ///   <item><description>6 = X-Trans pattern (Fujifilm)</description></item>
    /// </list>
    /// This determines which processing algorithms are available.
    /// </remarks>
    public int MosaicPatternWidth { get; set; }

    /// <summary>
    /// Gets or sets the maximum valid pixel value from the sensor.
    /// </summary>
    /// <remarks>
    /// Values above this level are clipped/saturated. Common values are 4095 (12-bit),
    /// 16383 (14-bit), or 65535 (16-bit). This value is per-channel and global.
    /// </remarks>
    public int WhiteLevel { get; set; }

    /// <summary>
    /// Gets or sets the black level values for each color channel.
    /// </summary>
    /// <remarks>
    /// Typically 4 values for RGGB Bayer patterns. Black level represents the sensor's
    /// zero-light output and must be subtracted before processing.
    /// </remarks>
    public int[] BlackLevels { get; set; } = [];

    /// <summary>
    /// Gets or sets the exposure bias (exposure compensation) in hundredths of an EV.
    /// </summary>
    /// <remarks>
    /// Value of 100 = +1 EV, -100 = -1 EV, 0 = no compensation.
    /// Used to detect exposure bracketing in burst sequences.
    /// </remarks>
    public int ExposureBias { get; set; }

    /// <summary>
    /// Gets or sets the product of ISO speed and exposure time.
    /// </summary>
    /// <remarks>
    /// Calculated as: ISO × ShutterSpeed (in seconds).
    /// Used to detect manual exposure variations within a burst sequence
    /// when exposure bias is not set. Higher values indicate more light captured.
    /// </remarks>
    public float IsoSpeedExposureTimeProduct { get; set; }

    /// <summary>
    /// Gets or sets the color channel multipliers for white balance.
    /// </summary>
    /// <remarks>
    /// Typically 4 values (RGBG or RGGB) representing the camera's as-shot white balance.
    /// These are used to normalize the color channels during processing.
    /// </remarks>
    public float[] ColorChannelMultipliers { get; set; } = [];

    #endregion

    #region DNG Color Metadata

    /// <summary>
    /// Gets or sets the color filter array pattern.
    /// </summary>
    /// <remarks>
    /// Array of color indices defining the CFA pattern:
    /// <list type="bullet">
    ///   <item><description>0 = Red</description></item>
    ///   <item><description>1 = Green</description></item>
    ///   <item><description>2 = Blue</description></item>
    /// </list>
    /// For example, RGGB = [0, 1, 1, 2], BGGR = [2, 1, 1, 0].
    /// </remarks>
    public int[] CfaPattern { get; set; } = [];

    /// <summary>
    /// Gets or sets the first color matrix (3×3, stored as 9 values row-major).
    /// </summary>
    /// <remarks>
    /// Converts from camera RGB to XYZ color space under the first calibration illuminant.
    /// Required for proper color rendering in DNG-compatible software.
    /// </remarks>
    public double[] ColorMatrix1 { get; set; } = [];

    /// <summary>
    /// Gets or sets the second color matrix (3×3, stored as 9 values row-major).
    /// </summary>
    /// <remarks>
    /// Converts from camera RGB to XYZ color space under the second calibration illuminant.
    /// Optional but improves color accuracy across different lighting conditions.
    /// </remarks>
    public double[] ColorMatrix2 { get; set; } = [];

    /// <summary>
    /// Gets or sets the first calibration illuminant type.
    /// </summary>
    /// <remarks>
    /// DNG illuminant codes: 17 = Standard Light A (tungsten, ~2856K), 21 = D65 (daylight, ~6500K).
    /// This indicates which lighting condition <see cref="ColorMatrix1"/> was calibrated for.
    /// </remarks>
    public int CalibrationIlluminant1 { get; set; }

    /// <summary>
    /// Gets or sets the second calibration illuminant type.
    /// </summary>
    /// <remarks>
    /// Typically 21 (D65/daylight). Indicates which lighting condition <see cref="ColorMatrix2"/>
    /// was calibrated for. Used with ColorMatrix2 for interpolated color correction.
    /// </remarks>
    public int CalibrationIlluminant2 { get; set; }

    /// <summary>
    /// Gets or sets the as-shot neutral white balance values.
    /// </summary>
    /// <remarks>
    /// Array of 3 RGB values representing the camera's measured neutral point.
    /// Values are typically in the range 0.0-1.0, with neutral gray having equal values.
    /// </remarks>
    public double[] AsShotNeutral { get; set; } = [];

    #endregion

    #region Camera Identification

    /// <summary>
    /// Gets or sets the camera manufacturer name (e.g., "Canon", "Nikon", "Sony").
    /// </summary>
    public string CameraMake { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the camera model name (e.g., "EOS R5", "Z8", "A7R V").
    /// </summary>
    public string CameraModel { get; set; } = string.Empty;

    #endregion

    #region Data Format

    /// <summary>
    /// Gets or sets whether the <see cref="Data"/> contains raw Bayer/CFA mosaic data.
    /// </summary>
    /// <remarks>
    /// When <c>true</c>, the data is still in raw sensor format and needs demosaicing.
    /// When <c>false</c>, the data has been demosaiced into RGB.
    /// </remarks>
    public bool IsBayerData { get; set; }

    #endregion
}
