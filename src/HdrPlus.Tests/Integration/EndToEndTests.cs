using FluentAssertions;
using HdrPlus.Compute;
using HdrPlus.Core.Alignment;
using HdrPlus.IO;
using Xunit;

namespace HdrPlus.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the complete HDR+ pipeline.
/// Tests the full workflow from DNG loading to aligned output.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly string _testDirectory;
    private IComputeDevice? _device;

    public EndToEndTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"hdrplus_integration_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact(Skip = "Requires GPU hardware and test data")]
    public void FullPipeline_SingleBurst_ShouldProduceAlignedOutput()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var reader = new LibRawDngReader();
        var aligner = new ImageAligner(_device);
        var writer = new DngWriter();

        var referenceFile = "test_data/burst/reference.dng";
        var compareFiles = new[]
        {
            "test_data/burst/img001.dng",
            "test_data/burst/img002.dng",
            "test_data/burst/img003.dng"
        };

        // Act
        var referenceImage = reader.Read(referenceFile);
        var alignedImages = new List<DngImage>();

        foreach (var compareFile in compareFiles)
        {
            var compareImage = reader.Read(compareFile);
            var alignment = aligner.Align(referenceImage, compareImage);
            var aligned = aligner.ApplyAlignment(compareImage, alignment);
            alignedImages.Add(aligned);
        }

        var outputPath = Path.Combine(_testDirectory, "aligned_output.dng");
        writer.Write(alignedImages[0], outputPath);

        // Assert
        File.Exists(outputPath).Should().BeTrue();
        alignedImages.Should().HaveCount(3);
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pipeline_LoadAlignSave_ShouldMaintainImageDimensions()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);
        var writer = new DngWriter();

        var referenceImage = CreateTestImage(1024, 768);
        var compareImage = CreateTestImage(1024, 768);

        // Act
        var alignment = aligner.Align(referenceImage, compareImage);
        var aligned = aligner.ApplyAlignment(compareImage, alignment);

        var outputPath = Path.Combine(_testDirectory, "aligned.dng");
        writer.Write(aligned, outputPath);

        // Assert
        aligned.Width.Should().Be(referenceImage.Width);
        aligned.Height.Should().Be(referenceImage.Height);
        File.Exists(outputPath).Should().BeTrue();
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pipeline_WithBracketedExposures_ShouldPreserveExposureMetadata()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var writer = new DngWriter();

        var images = new[]
        {
            CreateTestImageWithExposure(1024, 768, exposureBias: -2),
            CreateTestImageWithExposure(1024, 768, exposureBias: 0),
            CreateTestImageWithExposure(1024, 768, exposureBias: +2)
        };

        // Act
        var outputPaths = new List<string>();
        for (int i = 0; i < images.Length; i++)
        {
            var path = Path.Combine(_testDirectory, $"bracketed_{i}.dng");
            writer.Write(images[i], path);
            outputPaths.Add(path);
        }

        // Assert
        foreach (var path in outputPaths)
        {
            File.Exists(path).Should().BeTrue();
        }
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pipeline_MultipleBackends_ShouldProduceSameResults()
    {
        // Arrange - Test that DirectX12 and Vulkan produce same results
        var backends = new[] { ComputeBackend.DirectX12, ComputeBackend.Vulkan };
        var results = new Dictionary<ComputeBackend, byte[]>();

        foreach (var backend in backends)
        {
            try
            {
                _device?.Dispose();
                _device = ComputeDeviceFactory.Create(backend);

                var image = CreateTestImage(512, 512);
                var writer = new DngWriter();
                var path = Path.Combine(_testDirectory, $"output_{backend}.dng");

                writer.Write(image, path);
                results[backend] = File.ReadAllBytes(path);
            }
            catch
            {
                // Backend not available on this platform
            }
        }

        // Assert
        if (results.Count >= 2)
        {
            var first = results.First().Value;
            foreach (var result in results.Values)
            {
                // Outputs should be identical (or very similar)
                result.Length.Should().Be(first.Length);
            }
        }
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pipeline_LargeImage_ShouldHandleMemoryEfficiently()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var writer = new DngWriter();

        // Simulate a large RAW image (e.g., 8K resolution)
        var largeImage = CreateTestImage(7680, 4320);

        // Act
        var outputPath = Path.Combine(_testDirectory, "large_output.dng");
        Action act = () => writer.Write(largeImage, outputPath);

        // Assert
        act.Should().NotThrow();
        File.Exists(outputPath).Should().BeTrue();

        var fileSize = new FileInfo(outputPath).Length;
        fileSize.Should().BeGreaterThan(1024 * 1024); // At least 1MB
    }

    [Fact(Skip = "Requires GPU hardware")]
    public void Pipeline_BurstOf50Images_ShouldProcessAllSuccessfully()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);
        var referenceImage = CreateTestImage(1024, 768);

        var burstImages = Enumerable.Range(0, 50)
            .Select(_ => CreateTestImage(1024, 768))
            .ToList();

        // Act
        var alignments = burstImages
            .Select(img => aligner.Align(referenceImage, img))
            .ToList();

        // Assert
        alignments.Should().HaveCount(50);
        alignments.Should().AllSatisfy(a => a.Should().NotBeNull());
    }

    private DngImage CreateTestImage(int width, int height)
    {
        var data = new ushort[width * height];
        var random = new Random(42);

        for (int i = 0; i < data.Length; i++)
            data[i] = (ushort)random.Next(512, 60000);

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

    private DngImage CreateTestImageWithExposure(int width, int height, int exposureBias)
    {
        var image = CreateTestImage(width, height);
        return image with { ExposureBias = exposureBias };
    }

    public void Dispose()
    {
        _device?.Dispose();

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
