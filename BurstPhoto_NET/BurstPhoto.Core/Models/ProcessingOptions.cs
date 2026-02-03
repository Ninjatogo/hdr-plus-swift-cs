namespace BurstPhoto.Core.Models;

/// <summary>
/// Specifies the algorithm used to merge aligned frames.
/// </summary>
public enum MergingAlgorithm
{
    /// <summary>
    /// Fast spatial domain merging using weighted averaging.
    /// Works with all sensor types and is suitable for most use cases.
    /// </summary>
    Fast,

    /// <summary>
    /// Higher quality frequency domain merging using FFT-based processing.
    /// Produces better results but is only supported for Bayer sensors (2x2 CFA pattern).
    /// </summary>
    HigherQuality
}

/// <summary>
/// Specifies the tile size used for alignment.
/// </summary>
/// <remarks>
/// Smaller tiles capture finer motion detail but are more sensitive to noise.
/// Larger tiles are more robust but may miss small-scale motion.
/// </remarks>
public enum TileSizeOption
{
    /// <summary>Small tiles (16 pixels). Best for fine detail but sensitive to noise.</summary>
    Small,

    /// <summary>Medium tiles (32 pixels). Balanced choice for most images.</summary>
    Medium,

    /// <summary>Large tiles (64 pixels). Most robust but may miss fine motion.</summary>
    Large
}

/// <summary>
/// Specifies the maximum search distance when finding tile alignments.
/// </summary>
/// <remarks>
/// <para>
/// The naming reflects computational cost, not the actual pixel distance:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Small"/> = 128 pixels (fast computation, can find larger motion)</description></item>
///   <item><description><see cref="Medium"/> = 64 pixels (balanced)</description></item>
///   <item><description><see cref="Large"/> = 32 pixels (more search positions, smaller range)</description></item>
/// </list>
/// </remarks>
public enum SearchDistanceOption
{
    /// <summary>Search up to 128 pixels. Finds larger motion with fewer search positions.</summary>
    Small,

    /// <summary>Search up to 64 pixels. Balanced between range and precision.</summary>
    Medium,

    /// <summary>Search up to 32 pixels. More search positions but smaller motion range.</summary>
    Large
}

/// <summary>
/// Specifies the exposure control method applied to the merged output.
/// </summary>
/// <remarks>
/// <para>Exposure control adjusts the output brightness and dynamic range:</para>
/// <list type="bullet">
///   <item><description><c>Linear</c> options apply linear scaling</description></item>
///   <item><description><c>Curve</c> options apply a tone curve for better highlight handling</description></item>
///   <item><description><c>0Ev</c> maintains original exposure</description></item>
///   <item><description><c>1Ev</c> boosts exposure by 1 stop</description></item>
/// </list>
/// </remarks>
public enum ExposureControlOption
{
    /// <summary>No exposure adjustment. Output uses native sensor values.</summary>
    Off,

    /// <summary>Linear scaling to use the full output range. No exposure boost.</summary>
    LinearFullRange,

    /// <summary>Linear scaling with +1 EV exposure boost.</summary>
    Linear1Ev,

    /// <summary>Tone curve applied with no exposure boost. Better highlight handling.</summary>
    Curve0Ev,

    /// <summary>Tone curve applied with +1 EV exposure boost.</summary>
    Curve1Ev
}

/// <summary>
/// Specifies the bit depth of the output image.
/// </summary>
public enum OutputBitDepthOption
{
    /// <summary>
    /// Uses the native bit depth from the camera sensor (typically 12-14 bits).
    /// </summary>
    Native,

    /// <summary>
    /// Upscales output to 16-bit depth. Only available for Bayer sensors when exposure control is enabled.
    /// Provides more headroom for post-processing.
    /// </summary>
    Bit16
}

/// <summary>
/// Configuration options for the burst photo denoising pipeline.
/// </summary>
/// <remarks>
/// These options control how images are aligned, merged, and output.
/// Some combinations are constrained (e.g., 16-bit output requires exposure control).
/// </remarks>
public class ProcessingOptions
{
    /// <summary>
    /// Gets or sets the merging algorithm to use.
    /// </summary>
    /// <remarks>
    /// Default is <see cref="MergingAlgorithm.Fast"/> which works with all sensors.
    /// <see cref="MergingAlgorithm.HigherQuality"/> is automatically downgraded to Fast for non-Bayer sensors.
    /// </remarks>
    public MergingAlgorithm Merging { get; set; } = MergingAlgorithm.Fast;

    /// <summary>
    /// Gets or sets the tile size for alignment.
    /// </summary>
    public TileSizeOption TileSize { get; set; } = TileSizeOption.Medium;

    /// <summary>
    /// Gets or sets the search distance for alignment.
    /// </summary>
    public SearchDistanceOption SearchDistance { get; set; } = SearchDistanceOption.Medium;

    /// <summary>
    /// Gets or sets the noise reduction strength (0-25, higher = stronger reduction).
    /// </summary>
    /// <remarks>
    /// Value of 23 enables "static averaging" mode which averages all frames equally.
    /// </remarks>
    public double NoiseReduction { get; set; } = 13.0;

    /// <summary>
    /// Gets or sets the exposure control method for the output.
    /// </summary>
    public ExposureControlOption ExposureControl { get; set; } = ExposureControlOption.LinearFullRange;

    /// <summary>
    /// Gets or sets the output bit depth.
    /// </summary>
    public OutputBitDepthOption OutputBitDepth { get; set; } = OutputBitDepthOption.Native;

    /// <summary>
    /// Gets or sets whether to save intermediate processing stages as DNG files for debugging.
    /// </summary>
    public bool EnableDebugDump { get; set; }

    /// <summary>
    /// Gets or sets whether to validate FFT operations using mathematical tests.
    /// </summary>
    /// <remarks>
    /// When enabled, runs Parseval's theorem verification, round-trip tests, and DC component checks
    /// at each FFT stage. Processing stops early if validation fails.
    /// </remarks>
    public bool EnableFftValidation { get; set; }

    /// <summary>
    /// Gets or sets whether to skip the tile border artifact reduction pass.
    /// </summary>
    /// <remarks>
    /// Debug option to diagnose grid artifacts. When true, tile boundary blending is disabled.
    /// </remarks>
    public bool SkipReduceArtifacts { get; set; }

    /// <summary>
    /// Gets or sets whether to output detailed timing information for each pipeline stage.
    /// </summary>
    public bool EnableProfiling { get; set; }

    /// <summary>
    /// Gets or sets whether to output verbose diagnostic information.
    /// When enabled, performs expensive GPU->CPU transfers for validation logging.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Converts a <see cref="TileSizeOption"/> to its pixel value.
    /// </summary>
    /// <param name="option">The tile size option.</param>
    /// <returns>The tile size in pixels.</returns>
    public static int GetTileSizePixels(TileSizeOption option) => option switch
    {
        TileSizeOption.Small => 16,
        TileSizeOption.Medium => 32,
        TileSizeOption.Large => 64,
        _ => 32
    };

    /// <summary>
    /// Converts a <see cref="SearchDistanceOption"/> to its pixel value.
    /// </summary>
    /// <param name="option">The search distance option.</param>
    /// <returns>The maximum search distance in pixels.</returns>
    public static int GetSearchDistancePixels(SearchDistanceOption option) => option switch
    {
        SearchDistanceOption.Small => 128,
        SearchDistanceOption.Medium => 64,
        SearchDistanceOption.Large => 32,
        _ => 64
    };

    /// <summary>
    /// Gets the filename suffix corresponding to an exposure control option.
    /// </summary>
    /// <param name="option">The exposure control option.</param>
    /// <returns>A suffix string for the output filename (e.g., "_l0", "_nl1").</returns>
    public static string GetExposureControlSuffix(ExposureControlOption option) => option switch
    {
        ExposureControlOption.Off => "",
        ExposureControlOption.LinearFullRange => "_l0",
        ExposureControlOption.Linear1Ev => "_l1",
        ExposureControlOption.Curve0Ev => "_nl0",
        ExposureControlOption.Curve1Ev => "_nl1",
        _ => ""
    };
}
