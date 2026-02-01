using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

/// <summary>
/// Defines a writer for saving processed raw images to disk.
/// </summary>
/// <remarks>
/// Implementations handle writing to various output formats (DNG, TIFF, etc.)
/// while preserving metadata from the original raw files.
/// </remarks>
public interface IRawImageWriter
{
    /// <summary>
    /// Writes a raw image to disk synchronously.
    /// </summary>
    /// <param name="image">The raw image to write.</param>
    /// <param name="outputPath">The destination file path.</param>
    /// <exception cref="IOException">
    /// Thrown when the file cannot be written (permissions, disk full, etc.).
    /// </exception>
    void Write(RawImage image, string outputPath);

    /// <summary>
    /// Writes a raw image to disk asynchronously.
    /// </summary>
    /// <param name="image">The raw image to write.</param>
    /// <param name="outputPath">The destination file path.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    /// <exception cref="IOException">
    /// Thrown when the file cannot be written (permissions, disk full, etc.).
    /// </exception>
    Task WriteAsync(RawImage image, string outputPath);
}
