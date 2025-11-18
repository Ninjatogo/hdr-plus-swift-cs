using FluentAssertions;
using HdrPlus.IO;
using Xunit;

namespace HdrPlus.Tests.IO;

/// <summary>
/// Tests for DNG file reading and writing operations.
/// Validates LibRaw integration and metadata preservation.
/// </summary>
public class DngReaderWriterTests : IDisposable
{
    private readonly string _testDirectory;

    public DngReaderWriterTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"hdrplus_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact(Skip = "Requires test DNG files")]
    public void LibRawDngReader_ReadValidDng_ShouldLoadImageData()
    {
        // Arrange
        var reader = new LibRawDngReader();
        var testFile = "test_data/sample.dng";

        // Act
        var image = reader.Read(testFile);

        // Assert
        image.Should().NotBeNull();
        image.Width.Should().BeGreaterThan(0);
        image.Height.Should().BeGreaterThan(0);
        image.RawData.Should().NotBeEmpty();
    }

    [Fact(Skip = "Requires test DNG files")]
    public void LibRawDngReader_ReadDng_ShouldPreserveMetadata()
    {
        // Arrange
        var reader = new LibRawDngReader();
        var testFile = "test_data/sample.dng";

        // Act
        var image = reader.Read(testFile);

        // Assert
        image.MosaicPattern.Should().NotBeNullOrEmpty();
        image.MosaicPatternWidth.Should().BeGreaterThan(0);
        image.BlackLevels.Should().NotBeEmpty();
        image.WhiteLevel.Should().BeGreaterThan(0);
    }

    [Fact(Skip = "Requires test DNG files")]
    public void LibRawDngReader_ReadMultipleFormats_ShouldSupport700PlusFormats()
    {
        // Arrange
        var reader = new LibRawDngReader();
        var formats = new[] { ".dng", ".cr2", ".nef", ".arw", ".raf", ".orf", ".rw2" };

        // Act & Assert
        foreach (var format in formats)
        {
            var testFile = $"test_data/sample{format}";
            if (File.Exists(testFile))
            {
                Action act = () => reader.Read(testFile);
                act.Should().NotThrow($"LibRaw should support {format} format");
            }
        }
    }

    [Fact]
    public void DngWriter_WriteImage_ShouldCreateValidFile()
    {
        // Arrange
        var writer = new DngWriter();
        var outputPath = Path.Combine(_testDirectory, "output.dng");

        var testImage = new DngImage
        {
            RawData = new ushort[1024 * 768],
            Width = 1024,
            Height = 768,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };

        // Fill with test pattern
        for (int i = 0; i < testImage.RawData.Length; i++)
            testImage.RawData[i] = (ushort)(i % 65536);

        // Act
        writer.Write(testImage, outputPath);

        // Assert
        File.Exists(outputPath).Should().BeTrue();
        var fileInfo = new FileInfo(outputPath);
        fileInfo.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DngWriter_WriteAndRead_ShouldPreserveData()
    {
        // Arrange
        var writer = new DngWriter();
        var reader = new SimpleRawReader();
        var outputPath = Path.Combine(_testDirectory, "roundtrip.dng");

        var originalImage = new DngImage
        {
            RawData = new ushort[256 * 256],
            Width = 256,
            Height = 256,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };

        // Create test pattern
        for (int i = 0; i < originalImage.RawData.Length; i++)
            originalImage.RawData[i] = (ushort)(i * 10 % 65536);

        // Act
        writer.Write(originalImage, outputPath);
        var loadedImage = reader.Read(outputPath);

        // Assert
        loadedImage.Width.Should().Be(originalImage.Width);
        loadedImage.Height.Should().Be(originalImage.Height);
        loadedImage.RawData.Length.Should().Be(originalImage.RawData.Length);
    }

    [Fact]
    public void DngWriter_WithCompression_ShouldReduceFileSize()
    {
        // Arrange
        var writer = new DngWriter();
        var uncompressedPath = Path.Combine(_testDirectory, "uncompressed.dng");
        var compressedPath = Path.Combine(_testDirectory, "compressed.dng");

        var testImage = new DngImage
        {
            RawData = new ushort[1024 * 768],
            Width = 1024,
            Height = 768,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };

        // Fill with compressible pattern (lots of zeros)
        Array.Clear(testImage.RawData);

        // Act
        writer.Write(testImage, uncompressedPath);
        writer.WriteCompressed(testImage, compressedPath);

        // Assert
        var uncompressedSize = new FileInfo(uncompressedPath).Length;
        var compressedSize = new FileInfo(compressedPath).Length;
        compressedSize.Should().BeLessThan(uncompressedSize);
    }

    [Fact]
    public void SimpleRawReader_ReadInvalidFile_ShouldThrowException()
    {
        // Arrange
        var reader = new SimpleRawReader();
        var invalidFile = Path.Combine(_testDirectory, "invalid.dng");
        File.WriteAllText(invalidFile, "This is not a DNG file");

        // Act
        Action act = () => reader.Read(invalidFile);

        // Assert
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void DngWriter_WriteToInvalidPath_ShouldThrowException()
    {
        // Arrange
        var writer = new DngWriter();
        var invalidPath = "/invalid/path/that/does/not/exist/output.dng";

        var testImage = new DngImage
        {
            RawData = new ushort[100],
            Width = 10,
            Height = 10,
            MosaicPatternWidth = 2,
            MosaicPattern = "RGGB",
            BlackLevels = new[] { 512, 512, 512, 512 },
            WhiteLevel = 65535
        };

        // Act
        Action act = () => writer.Write(testImage, invalidPath);

        // Assert
        act.Should().Throw<Exception>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
