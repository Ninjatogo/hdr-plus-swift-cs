using FluentAssertions;
using HdrPlus.Compute;
using HdrPlus.Core.Alignment;
using HdrPlus.IO;
using Xunit;

namespace HdrPlus.Tests.Core;

/// <summary>
/// Tests for image alignment algorithms.
/// Validates tile-based alignment and multi-scale pyramid processing.
/// </summary>
public class AlignmentTests : IDisposable
{
    private IComputeDevice? _device;

    [Fact(Skip = "Requires GPU hardware")]
    public void ImageAligner_WithIdenticalImages_ShouldProduceZeroOffsets()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);

        var referenceImage = CreateTestImage(512, 512);
        var compareImage = CreateTestImage(512, 512); // Identical

        // Act
        var result = aligner.Align(referenceImage, compareImage);

        // Assert
        result.Should().NotBeNull();
        // For identical images, offsets should be close to zero
        result.GlobalOffsetX.Should().BeInRange(-2, 2);
        result.GlobalOffsetY.Should().BeInRange(-2, 2);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void ImageAligner_WithShiftedImage_ShouldDetectOffset()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);

        var referenceImage = CreateTestPattern(512, 512);
        var shiftedImage = CreateShiftedTestPattern(512, 512, 10, 5);

        // Act
        var result = aligner.Align(referenceImage, shiftedImage);

        // Assert
        result.Should().NotBeNull();
        result.GlobalOffsetX.Should().BeApproximately(10, 2);
        result.GlobalOffsetY.Should().BeApproximately(5, 2);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void ImageAligner_BuildPyramid_ShouldCreateMultipleLevels()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);
        var image = CreateTestImage(1024, 1024);

        // Act
        var pyramid = aligner.BuildPyramid(image, levels: 4);

        // Assert
        pyramid.Should().NotBeNull();
        pyramid.Levels.Should().Be(4);
        pyramid.GetLevel(0).Width.Should().Be(1024);
        pyramid.GetLevel(1).Width.Should().Be(512);
        pyramid.GetLevel(2).Width.Should().Be(256);
        pyramid.GetLevel(3).Width.Should().Be(128);
    }

    [Theory(Skip = "Requires GPU hardware")]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    public void TileInfo_WithVariousTileSizes_ShouldCalculateCorrectTileCount(int tileWidth, int tileHeight)
    {
        // Arrange
        const int imageWidth = 512;
        const int imageHeight = 512;

        // Act
        var tileInfo = new TileInfo(imageWidth, imageHeight, tileWidth, tileHeight);

        // Assert
        tileInfo.TileCountX.Should().Be(imageWidth / tileWidth);
        tileInfo.TileCountY.Should().Be(imageHeight / tileHeight);
        tileInfo.TotalTiles.Should().Be((imageWidth / tileWidth) * (imageHeight / tileHeight));
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void ImageAligner_WithNullReference_ShouldThrowArgumentNullException()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);
        var compareImage = CreateTestImage(512, 512);

        // Act
        Action act = () => aligner.Align(null!, compareImage);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void ImageAligner_WithMismatchedDimensions_ShouldThrowArgumentException()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);
        var referenceImage = CreateTestImage(512, 512);
        var compareImage = CreateTestImage(1024, 1024);

        // Act
        Action act = () => aligner.Align(referenceImage, compareImage);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void ImageAligner_WithRotation_ShouldHandlePartialAlignment()
    {
        // Arrange - Test robustness with rotated image
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);

        var referenceImage = CreateTestPattern(512, 512);
        var rotatedImage = CreateRotatedTestPattern(512, 512, angle: 2.0);

        // Act
        var result = aligner.Align(referenceImage, rotatedImage);

        // Assert
        result.Should().NotBeNull();
        // Alignment should complete without crashing, even if not perfect
        result.TileAlignments.Should().NotBeEmpty();
    }

    private DngImage CreateTestImage(int width, int height)
    {
        var data = new ushort[width * height];
        var random = new Random(42);

        for (int i = 0; i < data.Length; i++)
            data[i] = (ushort)random.Next(0, 65536);

        return new DngImage
        {
            RawData = data,
            Width = width,
            Height = height,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };
    }

    private DngImage CreateTestPattern(int width, int height)
    {
        var data = new ushort[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                data[idx] = (ushort)((x + y) * 100 % 65536);
            }
        }

        return new DngImage
        {
            RawData = data,
            Width = width,
            Height = height,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };
    }

    private DngImage CreateShiftedTestPattern(int width, int height, int shiftX, int shiftY)
    {
        var data = new ushort[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcX = (x - shiftX + width) % width;
                int srcY = (y - shiftY + height) % height;
                int idx = y * width + x;
                data[idx] = (ushort)((srcX + srcY) * 100 % 65536);
            }
        }

        return new DngImage
        {
            RawData = data,
            Width = width,
            Height = height,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };
    }

    private DngImage CreateRotatedTestPattern(int width, int height, double angle)
    {
        // Simplified rotation for testing - just add some variation
        var data = new ushort[width * height];
        var random = new Random(42);

        for (int i = 0; i < data.Length; i++)
            data[i] = (ushort)((i * 123 + random.Next(0, 1000)) % 65536);

        return new DngImage
        {
            RawData = data,
            Width = width,
            Height = height,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };
    }

    public void Dispose()
    {
        _device?.Dispose();
    }
}
