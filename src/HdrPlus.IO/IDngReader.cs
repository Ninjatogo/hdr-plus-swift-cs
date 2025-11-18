namespace HdrPlus.IO;

/// <summary>
/// Interface for reading DNG/RAW image files.
/// </summary>
public interface IDngReader
{
    /// <summary>
    /// Reads a DNG/RAW file from disk.
    /// </summary>
    /// <param name="filePath">Path to the DNG/RAW file.</param>
    /// <returns>Parsed DNG image with metadata.</returns>
    DngImage ReadDng(string filePath);

    /// <summary>
    /// Checks if a file is a supported RAW format.
    /// </summary>
    bool IsSupported(string filePath);

    /// <summary>
    /// Gets a list of supported file extensions.
    /// </summary>
    string[] SupportedExtensions { get; }
}
