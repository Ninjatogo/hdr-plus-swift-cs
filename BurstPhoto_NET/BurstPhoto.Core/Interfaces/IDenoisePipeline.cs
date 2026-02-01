using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

/// <summary>
/// Interface for the main denoising pipeline that orchestrates burst photo processing.
/// </summary>
public interface IDenoisePipeline : IDisposable
{
    /// <summary>
    /// Processes a burst of images to produce a single denoised output.
    /// </summary>
    /// <param name="imagePaths">Paths to the input images (minimum 2).</param>
    /// <param name="options">Processing options.</param>
    /// <param name="progress">Progress reporting object.</param>
    /// <param name="outputDirectory">Directory to save the output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path to the output file.</returns>
    Task<string> ProcessAsync(
        IReadOnlyList<string> imagePaths,
        ProcessingOptions options,
        ProcessingProgress progress,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears any cached results to free memory.
    /// </summary>
    void ClearCache();
}
