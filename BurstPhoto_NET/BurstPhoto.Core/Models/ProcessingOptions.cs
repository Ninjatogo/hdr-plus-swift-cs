namespace BurstPhoto.Core.Models;

/// <summary>
/// Merging algorithm options.
/// </summary>
public enum MergingAlgorithm
{
    /// <summary>Fast spatial domain merging.</summary>
    Fast,
    /// <summary>Higher quality frequency domain merging (Bayer sensors only).</summary>
    HigherQuality
}

/// <summary>
/// Tile size options for alignment.
/// </summary>
public enum TileSizeOption
{
    /// <summary>16 pixels</summary>
    Small,
    /// <summary>32 pixels</summary>
    Medium,
    /// <summary>64 pixels</summary>
    Large
}

/// <summary>
/// Search distance options for alignment.
/// </summary>
public enum SearchDistanceOption
{
    /// <summary>128 pixels</summary>
    Small,
    /// <summary>64 pixels</summary>
    Medium,
    /// <summary>32 pixels</summary>
    Large
}

/// <summary>
/// Exposure control options for output.
/// </summary>
public enum ExposureControlOption
{
    Off,
    LinearFullRange,
    Linear1Ev,
    Curve0Ev,
    Curve1Ev
}

/// <summary>
/// Output bit depth options.
/// </summary>
public enum OutputBitDepthOption
{
    /// <summary>Native bit depth from the sensor.</summary>
    Native,
    /// <summary>16-bit output (Bayer sensors only when exposure control is enabled).</summary>
    Bit16
}

/// <summary>
/// Processing options for the denoise pipeline.
/// </summary>
public class ProcessingOptions
{
    public MergingAlgorithm Merging { get; set; } = MergingAlgorithm.Fast;
    public TileSizeOption TileSize { get; set; } = TileSizeOption.Medium;
    public SearchDistanceOption SearchDistance { get; set; } = SearchDistanceOption.Medium;
    public double NoiseReduction { get; set; } = 13.0;
    public ExposureControlOption ExposureControl { get; set; } = ExposureControlOption.LinearFullRange;
    public OutputBitDepthOption OutputBitDepth { get; set; } = OutputBitDepthOption.Native;
    
    /// <summary>
    /// Enable debug output: saves intermediate DNGs to DebugOutput folder.
    /// </summary>
    public bool EnableDebugDump { get; set; }
    
    /// <summary>
    /// Enable FFT validation: runs mathematical tests (Parseval's theorem, round-trip, DC component)
    /// to verify each FFT pipeline stage is working correctly. Stops early if validation fails.
    /// </summary>
    public bool EnableFftValidation { get; set; }

    /// <summary>
    /// Debug option: Skip the reduce_artifacts_tile_border pass to test if it's causing the 8x8 grid pattern.
    /// When true, the tile boundary blending is disabled.
    /// </summary>
    public bool SkipReduceArtifacts { get; set; }

    /// <summary>
    /// Enable performance profiling: outputs detailed timing for each pipeline stage.
    /// Use with --profile CLI flag for benchmarking.
    /// </summary>
    public bool EnableProfiling { get; set; }

    /// <summary>
    /// Gets the tile size in pixels for the given option.
    /// </summary>
    public static int GetTileSizePixels(TileSizeOption option) => option switch
    {
        TileSizeOption.Small => 16,
        TileSizeOption.Medium => 32,
        TileSizeOption.Large => 64,
        _ => 32
    };

    /// <summary>
    /// Gets the search distance in pixels for the given option.
    /// </summary>
    public static int GetSearchDistancePixels(SearchDistanceOption option) => option switch
    {
        SearchDistanceOption.Small => 128,
        SearchDistanceOption.Medium => 64,
        SearchDistanceOption.Large => 32,
        _ => 64
    };

    /// <summary>
    /// Gets the exposure control suffix for output filename.
    /// </summary>
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
