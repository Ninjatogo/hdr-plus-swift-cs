using BurstPhoto.Core.Errors;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using System.Collections.Concurrent;
using System.IO;

namespace BurstPhoto.Core.Implementations;

/// <summary>
/// Main orchestration pipeline for burst photo denoising.
/// Ported from Swift's denoise.swift perform_denoising function.
/// </summary>
public class DenoisePipeline : IDenoisePipeline
{
    private readonly IRawImageLoader _loader;
    private readonly IRawImageWriter _writer;
    private readonly IComputePipeline _compute;

    // Cache for skipping repeated processing with same settings
    private string _lastSettingsHash = string.Empty;
    private RawImage? _lastResult;

    public DenoisePipeline(IRawImageLoader loader, IRawImageWriter writer, IComputePipeline compute)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
        Console.WriteLine($"DenoisePipeline initialized with Writer: {_writer.GetType().Name}");
    }

    public async Task<string> ProcessAsync(
        IReadOnlyList<string> imagePaths,
        ProcessingOptions options,
        ProcessingProgress progress,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Validate inputs
        ValidateInputs(imagePaths);

        // Load all images in parallel
        Console.WriteLine("Loading images...");
        var images = await LoadImagesParallelAsync(imagePaths, cancellationToken);
        Console.WriteLine($"Time to load all images: {stopwatch.Elapsed.TotalSeconds:F3}s");

        progress.ProgressInt += 20_000_000;

        // Check resolution consistency
        ValidateResolutions(images);

        // Analyze exposure
        var (uniformExposure, exposureBias, isoExposureTime) = AnalyzeExposure(images);

        // Select reference frame
        int refIdx = SelectReferenceFrame(images, uniformExposure, exposureBias, isoExposureTime);
        var refImage = images[refIdx];
        Console.WriteLine($"Selected reference frame: {refIdx} ({Path.GetFileName(refImage.SourcePath)})");

        // Handle non-Bayer sensor constraints
        int mosaicPatternWidth = refImage.MosaicPatternWidth;
        var effectiveOptions = ApplySensorConstraints(options, mosaicPatternWidth, uniformExposure, progress);

        // Check for non-Bayer exposure bracketing
        if (!uniformExposure && mosaicPatternWidth != 2)
        {
            throw new AlignmentException(AlignmentErrorType.NonBayerExposureBracketing);
        }

        // Calculate tile info
        int tileSize = ProcessingOptions.GetTileSizePixels(effectiveOptions.TileSize);
        int searchDist = ProcessingOptions.GetSearchDistancePixels(effectiveOptions.SearchDistance);
        var tileInfo = TileInfo.Calculate(refImage.Width, refImage.Height, tileSize, searchDist);
        Console.WriteLine($"Tile grid: {tileInfo.NTilesX}x{tileInfo.NTilesY}, search positions: {tileInfo.NPos2D}");

        // Check cache
        string settingsHash = GenerateSettingsHash(imagePaths, effectiveOptions);
        RawImage result;

        if (_lastResult != null && _lastSettingsHash == settingsHash)
        {
            Console.WriteLine("Using cached result (settings unchanged).");
            result = _lastResult;
            progress.ProgressInt += 80_000_000;
        }
        else
        {
            // Process images through compute pipeline
            // For Phase 2, we do a simple passthrough - actual compute comes in Phase 3
            Console.WriteLine("Processing images...");
            var renderingInput = new RenderingInput
            {
                Images = images,
                ReferenceFrameIndex = refIdx
            };
            result = await _compute.ProcessAsync(renderingInput, effectiveOptions, progress);
            
            // Cache result
            _lastResult = result;
            _lastSettingsHash = settingsHash;
            progress.ProgressInt += 80_000_000;
        }

        // Generate output filename
        string outputPath = GenerateOutputPath(refImage.SourcePath, outputDirectory, effectiveOptions, images.Count);

        // Write output
        Console.WriteLine($"Writing output to: {outputPath}");
        if (result == null) throw new InvalidOperationException("Compute pipeline result is null!");
        Console.WriteLine($"Result image: {result.Width}x{result.Height}, Data len: {result.Data?.Length}");
        
        await _writer.WriteAsync(result, outputPath);
        progress.ProgressInt += 10_000_000;

        Console.WriteLine($"Total processing time for {images.Count} images: {stopwatch.Elapsed.TotalSeconds:F3}s");

        return outputPath;
    }

    /// <summary>
    /// Validates input image paths.
    /// </summary>
    private void ValidateInputs(IReadOnlyList<string> imagePaths)
    {
        if (imagePaths.Count < 2)
        {
            throw new AlignmentException(AlignmentErrorType.LessThanTwoImages);
        }

        // Check that all images have the same extension
        string firstExtension = Path.GetExtension(imagePaths[0]).ToLowerInvariant();
        foreach (var path in imagePaths)
        {
            if (!Path.GetExtension(path).Equals(firstExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new AlignmentException(AlignmentErrorType.InconsistentExtensions);
            }
        }
    }

    /// <summary>
    /// Validates that all images have the same resolution.
    /// </summary>
    private void ValidateResolutions(IReadOnlyList<RawImage> images)
    {
        int width = images[0].Width;
        int height = images[0].Height;

        for (int i = 1; i < images.Count; i++)
        {
            if (images[i].Width != width || images[i].Height != height)
            {
                throw new AlignmentException(AlignmentErrorType.InconsistentResolutions);
            }
        }
    }

    /// <summary>
    /// Loads images in parallel using multiple threads.
    /// </summary>
    private async Task<IReadOnlyList<RawImage>> LoadImagesParallelAsync(
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        var images = new ConcurrentDictionary<int, RawImage>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, imagePaths.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount * 3 / 4),
                CancellationToken = cancellationToken
            },
            async (i, ct) =>
            {
                var path = imagePaths[i];
                Console.WriteLine($"Loading image {Path.GetFileName(path)} from disk.");
                
                // Load synchronously on thread pool (LibRaw is not async)
                var image = await Task.Run(() => _loader.Load(path), ct);
                image.SourcePath = path;
                
                images[i] = image;
            });

        // Convert to ordered list
        return Enumerable.Range(0, imagePaths.Count)
            .Select(i => images[i])
            .ToList();
    }

    /// <summary>
    /// Analyzes exposure across images to determine if it's uniform.
    /// </summary>
    private (bool uniformExposure, int[] exposureBias, double[] isoExposureTime) AnalyzeExposure(
        IReadOnlyList<RawImage> images)
    {
        var exposureBias = images.Select(img => img.ExposureBias).ToArray();
        var isoExposureTime = images.Select(img => (double)img.IsoExposureTime).ToArray();

        // Check if exposure bias is uniform
        bool uniformExposure = exposureBias.All(e => e == exposureBias[0]);

        if (uniformExposure)
        {
            // Check if ISO*exposureTime varies (manual exposure changes)
            const double epsilon = 1e-12;
            uniformExposure = isoExposureTime.All(t => Math.Abs(t - isoExposureTime[0]) <= epsilon);

            if (!uniformExposure)
            {
                // Non-uniform via manual ISO/exposure changes
                // Recalculate exposure bias relative to darkest frame
                int refIdx = Array.IndexOf(isoExposureTime, isoExposureTime.Min());
                for (int i = 0; i < images.Count; i++)
                {
                    exposureBias[i] = (int)Math.Round(
                        (Math.Log2(isoExposureTime[i] / isoExposureTime[refIdx]) - 2.0) * 100);
                }
            }
        }

        return (uniformExposure, exposureBias, isoExposureTime);
    }

    /// <summary>
    /// Selects the reference frame based on exposure analysis.
    /// </summary>
    private int SelectReferenceFrame(
        IReadOnlyList<RawImage> images,
        bool uniformExposure,
        int[] exposureBias,
        double[] isoExposureTime)
    {
        if (!uniformExposure)
        {
            // Use image with median exposure value
            var sortedExposures = exposureBias.OrderBy(x => x).ToArray();
            int medianExposure = sortedExposures[sortedExposures.Length / 2];
            return Array.IndexOf(exposureBias, medianExposure);
        }
        else
        {
            // Check if ISO*exposureTime was non-uniform
            const double epsilon = 1e-12;
            bool isoUniform = isoExposureTime.All(t => Math.Abs(t - isoExposureTime[0]) <= epsilon);

            if (isoUniform)
            {
                // Truly uniform exposure - use central image
                // Central image is assumed closest to all others in a burst
                return images.Count / 2;
            }
            else
            {
                // ISO/exposure varied manually - pick median
                var sortedIso = isoExposureTime.OrderBy(x => x).ToArray();
                double medianIso = sortedIso[sortedIso.Length / 2];
                return Array.IndexOf(isoExposureTime, medianIso);
            }
        }
    }

    /// <summary>
    /// Applies sensor-specific constraints to processing options.
    /// </summary>
    private ProcessingOptions ApplySensorConstraints(
        ProcessingOptions options,
        int mosaicPatternWidth,
        bool uniformExposure,
        ProcessingProgress progress)
    {
        // Create a copy to avoid modifying the original
        var result = new ProcessingOptions
        {
            Merging = options.Merging,
            TileSize = options.TileSize,
            SearchDistance = options.SearchDistance,
            NoiseReduction = options.NoiseReduction,
            ExposureControl = options.ExposureControl,
            OutputBitDepth = options.OutputBitDepth
        };

        // Non-Bayer sensors have restrictions
        if (mosaicPatternWidth != 2)
        {
            if (result.Merging == MergingAlgorithm.HigherQuality)
            {
                progress.ShowNonBayerHqAlert = true;
                result.Merging = MergingAlgorithm.Fast;
            }

            if (uniformExposure && result.ExposureControl != ExposureControlOption.Off)
            {
                progress.ShowNonBayerExposureAlert = true;
                result.ExposureControl = ExposureControlOption.Off;
            }

            if (result.OutputBitDepth == OutputBitDepthOption.Bit16)
            {
                progress.ShowNonBayerBitDepthAlert = true;
                result.OutputBitDepth = OutputBitDepthOption.Native;
            }
        }

        // 16-bit output requires exposure control
        if (result.OutputBitDepth == OutputBitDepthOption.Bit16 && 
            result.ExposureControl == ExposureControlOption.Off)
        {
            progress.ShowExposureBitDepthAlert = true;
            result.OutputBitDepth = OutputBitDepthOption.Native;
        }

        return result;
    }

    /// <summary>
    /// Generates a hash of current settings for caching.
    /// </summary>
    private string GenerateSettingsHash(IReadOnlyList<string> imagePaths, ProcessingOptions options)
    {
        var parts = new List<string>
        {
            options.Merging.ToString(),
            options.NoiseReduction.ToString("F1"),
            options.TileSize.ToString(),
            options.SearchDistance.ToString(),
            string.Join("|", imagePaths)
        };
        return string.Join(":", parts);
    }

    /// <summary>
    /// Generates the output filename based on reference image and options.
    /// </summary>
    private string GenerateOutputPath(string refPath, string outputDirectory, ProcessingOptions options, int frameCount)
    {
        string baseName = Path.GetFileNameWithoutExtension(refPath);

        // Merging suffix
        string mergingSuffix = options.Merging == MergingAlgorithm.HigherQuality ? "q" : "f";
        int noiseInt = (int)(options.NoiseReduction + 0.5);

        string suffix = $"_n{frameCount}";

        if (Math.Abs(options.NoiseReduction - 23.0) < 0.1)
        {
            suffix += "_static_avg";
        }
        else
        {
            suffix += $"_hdr_{mergingSuffix}{noiseInt}";
        }

        // Exposure control suffix
        suffix += ProcessingOptions.GetExposureControlSuffix(options.ExposureControl);

        string outputName = baseName + suffix + ".dng";
        return Path.Combine(outputDirectory, outputName);
    }
}
