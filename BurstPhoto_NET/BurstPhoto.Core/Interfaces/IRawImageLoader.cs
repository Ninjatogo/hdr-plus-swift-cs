using BurstPhoto.Core.Models;

namespace BurstPhoto.Core.Interfaces;

/// <summary>
/// Defines a loader for reading raw image files into memory.
/// </summary>
/// <remarks>
/// Implementations handle various raw file formats (DNG, CR2, NEF, ARW, RAF, etc.)
/// and extract both pixel data and metadata required for processing.
/// </remarks>
public interface IRawImageLoader
{
    /// <summary>
    /// Loads a raw image file from disk.
    /// </summary>
    /// <param name="filePath">The absolute or relative path to the raw image file.</param>
    /// <returns>
    /// A <see cref="RawImage"/> containing the pixel data and metadata.
    /// The <see cref="RawImage.SourcePath"/> will be set to the provided path.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the specified file does not exist.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the file format is not supported or the file is corrupted.
    /// </exception>
    RawImage Load(string filePath);
}
