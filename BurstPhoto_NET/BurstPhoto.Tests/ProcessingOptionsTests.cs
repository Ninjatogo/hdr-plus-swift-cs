using BurstPhoto.Core.Errors;
using BurstPhoto.Core.Models;

namespace BurstPhoto.Tests;

/// <summary>
/// Tests for ProcessingOptions enum mappings and helper methods.
/// </summary>
public class ProcessingOptionsTests
{
    [Theory]
    [InlineData(TileSizeOption.Small, 16)]
    [InlineData(TileSizeOption.Medium, 32)]
    [InlineData(TileSizeOption.Large, 64)]
    public void GetTileSizePixels_ReturnsCorrectValues(TileSizeOption option, int expected)
    {
        Assert.Equal(expected, ProcessingOptions.GetTileSizePixels(option));
    }

    [Theory]
    [InlineData(SearchDistanceOption.Small, 128)]
    [InlineData(SearchDistanceOption.Medium, 64)]
    [InlineData(SearchDistanceOption.Large, 32)]
    public void GetSearchDistancePixels_ReturnsCorrectValues(SearchDistanceOption option, int expected)
    {
        Assert.Equal(expected, ProcessingOptions.GetSearchDistancePixels(option));
    }

    [Theory]
    [InlineData(ExposureControlOption.Off, "")]
    [InlineData(ExposureControlOption.LinearFullRange, "_l0")]
    [InlineData(ExposureControlOption.Linear1Ev, "_l1")]
    [InlineData(ExposureControlOption.Curve0Ev, "_nl0")]
    [InlineData(ExposureControlOption.Curve1Ev, "_nl1")]
    public void GetExposureControlSuffix_ReturnsCorrectSuffix(ExposureControlOption option, string expected)
    {
        Assert.Equal(expected, ProcessingOptions.GetExposureControlSuffix(option));
    }
}

/// <summary>
/// Tests for AlignmentException error messages.
/// </summary>
public class AlignmentExceptionTests
{
    [Fact]
    public void Constructor_SetsErrorType()
    {
        var ex = new AlignmentException(AlignmentErrorType.LessThanTwoImages);
        Assert.Equal(AlignmentErrorType.LessThanTwoImages, ex.ErrorType);
    }

    [Theory]
    [InlineData(AlignmentErrorType.LessThanTwoImages, "At least two images")]
    [InlineData(AlignmentErrorType.InconsistentExtensions, "same file extension")]
    [InlineData(AlignmentErrorType.InconsistentResolutions, "same resolution")]
    public void Constructor_SetsAppropriateMessage(AlignmentErrorType errorType, string messageContains)
    {
        var ex = new AlignmentException(errorType);
        Assert.Contains(messageContains, ex.Message);
    }
}
