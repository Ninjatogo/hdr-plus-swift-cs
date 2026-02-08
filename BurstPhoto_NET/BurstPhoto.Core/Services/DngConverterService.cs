using System.Diagnostics;
using System.IO;

namespace BurstPhoto.Core.Services;

/// <summary>
/// Service to convert non-DNG RAW files to DNG format using Adobe DNG Converter.
/// This enables processing of Sony ARW, Canon CR2/CR3, Nikon NEF, and other proprietary RAW formats.
/// </summary>
public class DngConverterService
{
    private static readonly string[] DefaultConverterPaths =
    [
        @"C:\Program Files\Adobe\Adobe DNG Converter\Adobe DNG Converter.exe",
        @"C:\Program Files (x86)\Adobe\Adobe DNG Converter\Adobe DNG Converter.exe"
    ];

    private static readonly HashSet<string> NonDngRawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw",  // Sony
        ".cr2",  // Canon (older)
        ".cr3",  // Canon (newer)
        ".nef",  // Nikon
        ".nrw",  // Nikon (compact)
        ".orf",  // Olympus
        ".raf",  // Fujifilm
        ".rw2",  // Panasonic
        ".pef",  // Pentax
        ".srw",  // Samsung
        ".x3f",  // Sigma
        ".3fr",  // Hasselblad
        ".fff",  // Hasselblad
        ".iiq",  // Phase One
        ".rwl",  // Leica
        ".raw",  // Generic
        ".kdc",  // Kodak
        ".dcr",  // Kodak
        ".erf",  // Epson
        ".mef",  // Mamiya
        ".mos",  // Leaf
    };

    private readonly string? _converterPath;

    public DngConverterService()
    {
        _converterPath = FindConverterPath();
    }

    /// <summary>
    /// Gets whether Adobe DNG Converter is available on this system.
    /// </summary>
    public bool IsAvailable => _converterPath != null;

    /// <summary>
    /// Gets the path to the Adobe DNG Converter executable, or null if not found.
    /// </summary>
    public string? ConverterPath => _converterPath;

    /// <summary>
    /// Checks if a file is a non-DNG RAW format that needs conversion.
    /// </summary>
    public static bool IsNonDngRawFile(string path)
    {
        var ext = Path.GetExtension(path);
        return NonDngRawExtensions.Contains(ext);
    }

    /// <summary>
    /// Converts a RAW file to DNG format using Adobe DNG Converter.
    /// </summary>
    /// <param name="sourcePath">Path to the source RAW file</param>
    /// <param name="outputDirectory">Directory to write the converted DNG file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the converted DNG file</returns>
    /// <exception cref="InvalidOperationException">If converter is not available or conversion fails</exception>
    public async Task<string> ConvertToDngAsync(
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (_converterPath == null)
        {
            throw new InvalidOperationException(
                "Adobe DNG Converter is not installed. Please download and install it from Adobe's website.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source RAW file not found", sourcePath);
        }

        // Ensure output directory exists
        Directory.CreateDirectory(outputDirectory);

        // Build output filename (DNG Converter uses %f for original filename without extension)
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var expectedOutputPath = Path.Combine(outputDirectory, $"{baseName}.dng");

        // Delete existing file if present (to ensure we get a fresh conversion)
        if (File.Exists(expectedOutputPath))
        {
            File.Delete(expectedOutputPath);
        }

        Console.WriteLine($"[DngConverter] Converting: {Path.GetFileName(sourcePath)}");

        // Build arguments
        // -d: output directory
        // -p1: medium-size preview (faster than full)
        // -fl: fast-load data (optimizes for quick loading in Adobe apps)
        // Note: Don't use -o flag - the %f pattern doesn't work via CLI. Without -o,
        // the converter automatically uses the original filename with .dng extension.
        var arguments = $"-d \"{outputDirectory}\" -p1 -fl \"{sourcePath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _converterPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read output asynchronously to prevent deadlocks
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"DNG conversion failed with exit code {process.ExitCode}. Error: {error}");
        }

        // Verify the output file was created
        if (!File.Exists(expectedOutputPath))
        {
            throw new InvalidOperationException(
                $"DNG conversion completed but output file not found: {expectedOutputPath}");
        }

        Console.WriteLine($"[DngConverter] Created: {Path.GetFileName(expectedOutputPath)}");
        return expectedOutputPath;
    }

    /// <summary>
    /// Converts multiple RAW files to DNG format in parallel.
    /// </summary>
    /// <param name="sourcePaths">Paths to the source RAW files</param>
    /// <param name="outputDirectory">Directory to write the converted DNG files</param>
    /// <param name="maxConcurrency">Maximum number of concurrent conversions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of paths to the converted DNG files (in same order as input)</returns>
    public async Task<IReadOnlyList<string>> ConvertMultipleToDngAsync(
        IReadOnlyList<string> sourcePaths,
        string outputDirectory,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
    {
        if (_converterPath == null)
        {
            throw new InvalidOperationException(
                "Adobe DNG Converter is not installed. Please download and install it from Adobe's website.");
        }

        Directory.CreateDirectory(outputDirectory);

        var results = new string[sourcePaths.Count];
        var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = sourcePaths.Select(async (sourcePath, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                if (IsNonDngRawFile(sourcePath))
                {
                    results[index] = await ConvertToDngAsync(sourcePath, outputDirectory, cancellationToken);
                }
                else
                {
                    // Already a DNG or other format, use as-is
                    results[index] = sourcePath;
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return results;
    }

    private static string? FindConverterPath()
    {
        foreach (var path in DefaultConverterPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }
}
