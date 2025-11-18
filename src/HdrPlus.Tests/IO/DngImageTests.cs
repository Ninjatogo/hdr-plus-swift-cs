using FluentAssertions;
using HdrPlus.IO;
using Xunit;

namespace HdrPlus.Tests.IO;

/// <summary>
/// Tests for DNG image data structure and metadata handling.
/// </summary>
public class DngImageTests
{
    [Fact]
    public void DngImage_WithValidBayerData_ShouldCreateSuccessfully()
    {
        // Arrange & Act
        var image = new DngImage
        {
            RawData = new ushort[1024 * 768],
            Width = 1024,
            Height = 768,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };

        // Assert
        image.Should().NotBeNull();
        image.Width.Should().Be(1024);
        image.Height.Should().Be(768);
        image.MosaicPattern.Should().Be("RGGB");
        image.RawData.Length.Should().Be(1024 * 768);
    }

    [Fact]
    public void DngImage_WithXTransPattern_ShouldSupportNonBayerSensors()
    {
        // Arrange & Act
        var image = new DngImage
        {
            RawData = new ushort[2048 * 1536],
            Width = 2048,
            Height = 1536,
            MosaicPatternWidth = 6,
            MosaicPattern = "RBGBRG_GBRGBR_BGBRGR_GRGRBG_RGRBGB_BRGBGR",
            BlackLevels = new[] { 1024, 1024, 1024, 1024 },
            WhiteLevel = 16383
        };

        // Assert
        image.MosaicPatternWidth.Should().Be(6);
        image.MosaicPattern.Should().Contain("X-Trans pattern", Because = "X-Trans uses 6x6 pattern");
    }

    [Fact]
    public void DngImage_WithBlackLevels_ShouldStorePerChannelValues()
    {
        // Arrange
        var blackLevels = new[] { 500, 510, 520, 530 };

        // Act
        var image = new DngImage
        {
            RawData = new ushort[100],
            Width = 10,
            Height = 10,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = blackLevels,
            WhiteLevel = 65535
        };

        // Assert
        image.BlackLevels.Should().BeEquivalentTo(blackLevels);
    }

    [Fact]
    public void DngImage_WithExposureBias_ShouldSupportBracketedBursts()
    {
        // Arrange & Act
        var image = new DngImage
        {
            RawData = new ushort[100],
            Width = 10,
            Height = 10,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535,
            ExposureBias = -2
        };

        // Assert
        image.ExposureBias.Should().Be(-2);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(256, 256)]
    [InlineData(1920, 1080)]
    [InlineData(4000, 3000)]
    [InlineData(8192, 6144)]
    public void DngImage_WithVariousResolutions_ShouldCreateSuccessfully(int width, int height)
    {
        // Arrange & Act
        var image = new DngImage
        {
            RawData = new ushort[width * height],
            Width = width,
            Height = height,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };

        // Assert
        image.Width.Should().Be(width);
        image.Height.Should().Be(height);
        image.RawData.Length.Should().Be(width * height);
    }

    [Theory]
    [InlineData(2, "RGGB")]    // Standard Bayer
    [InlineData(2, "BGGR")]    // Bayer variant
    [InlineData(2, "GRBG")]    // Bayer variant
    [InlineData(2, "GBRG")]    // Bayer variant
    [InlineData(6, "XTRANS")] // X-Trans
    public void DngImage_WithDifferentMosaicPatterns_ShouldSupportMultipleSensorTypes(int patternWidth, string pattern)
    {
        // Arrange & Act
        var image = new DngImage
        {
            RawData = new ushort[100],
            Width = 10,
            Height = 10,
            MosaicPatternWidth = patternWidth,
            MosaicPattern = pattern,
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };

        // Assert
        image.MosaicPatternWidth.Should().Be(patternWidth);
        image.MosaicPattern.Should().Be(pattern);
    }
}
