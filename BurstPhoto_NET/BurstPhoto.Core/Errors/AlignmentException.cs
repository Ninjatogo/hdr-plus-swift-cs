namespace BurstPhoto.Core.Errors;

/// <summary>
/// Types of alignment errors that can occur during burst photo processing.
/// </summary>
public enum AlignmentErrorType
{
    LessThanTwoImages,
    InconsistentExtensions,
    InconsistentResolutions,
    ConversionFailed,
    MissingDngConverter,
    NonBayerExposureBracketing
}

/// <summary>
/// Exception thrown when alignment validation or processing fails.
/// </summary>
public class AlignmentException : Exception
{
    public AlignmentErrorType ErrorType { get; }

    public AlignmentException(AlignmentErrorType errorType)
        : base(GetMessage(errorType))
    {
        ErrorType = errorType;
    }

    public AlignmentException(AlignmentErrorType errorType, string message)
        : base(message)
    {
        ErrorType = errorType;
    }

    public AlignmentException(AlignmentErrorType errorType, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorType = errorType;
    }

    private static string GetMessage(AlignmentErrorType errorType) => errorType switch
    {
        AlignmentErrorType.LessThanTwoImages => "At least two images are required for burst processing.",
        AlignmentErrorType.InconsistentExtensions => "All images must have the same file extension.",
        AlignmentErrorType.InconsistentResolutions => "All images must have the same resolution.",
        AlignmentErrorType.ConversionFailed => "Failed to convert raw files to DNG format.",
        AlignmentErrorType.MissingDngConverter => "Adobe DNG Converter is required but not installed.",
        AlignmentErrorType.NonBayerExposureBracketing => "Exposure bracketing is only supported for Bayer sensors.",
        _ => "An alignment error occurred."
    };
}

/// <summary>
/// Types of I/O errors that can occur during image loading or saving.
/// </summary>
public enum ImageIoErrorType
{
    LoadError,
    MetalError,
    SaveError
}

/// <summary>
/// Exception thrown when image I/O operations fail.
/// </summary>
public class ImageIoException : Exception
{
    public ImageIoErrorType ErrorType { get; }

    public ImageIoException(ImageIoErrorType errorType)
        : base(GetMessage(errorType))
    {
        ErrorType = errorType;
    }

    public ImageIoException(ImageIoErrorType errorType, string message)
        : base(message)
    {
        ErrorType = errorType;
    }

    public ImageIoException(ImageIoErrorType errorType, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorType = errorType;
    }

    private static string GetMessage(ImageIoErrorType errorType) => errorType switch
    {
        ImageIoErrorType.LoadError => "Failed to load image.",
        ImageIoErrorType.MetalError => "GPU/compute error occurred.",
        ImageIoErrorType.SaveError => "Failed to save image.",
        _ => "An I/O error occurred."
    };
}
