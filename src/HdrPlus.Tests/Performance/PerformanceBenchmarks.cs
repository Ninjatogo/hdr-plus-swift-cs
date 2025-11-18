using FluentAssertions;
using HdrPlus.Compute;
using HdrPlus.Core.Alignment;
using HdrPlus.IO;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace HdrPlus.Tests.Performance;

/// <summary>
/// Performance benchmarks comparing C# implementation to Swift/Metal baseline.
/// Target: Within 20% of Swift/Metal performance.
/// </summary>
public class PerformanceBenchmarks : IDisposable
{
    private readonly ITestOutputHelper _output;
    private IComputeDevice? _device;

    public PerformanceBenchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_DeviceInitialization_ShouldComplete()
    {
        // Arrange
        var sw = Stopwatch.StartNew();

        // Act
        _device = ComputeDeviceFactory.CreateDefault();
        sw.Stop();

        // Assert
        _output.WriteLine($"Device Initialization: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Backend: {_device.Backend}");
        _output.WriteLine($"Device: {_device.DeviceName}");

        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "Device initialization should take less than 5 seconds");
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_BufferCreation_1000Buffers_ShouldBeFast()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int bufferCount = 1000;
        const int bufferSize = 1024 * 1024; // 1MB each

        var sw = Stopwatch.StartNew();

        // Act
        var buffers = new List<IComputeBuffer>();
        for (int i = 0; i < bufferCount; i++)
        {
            buffers.Add(_device.CreateBuffer(bufferSize));
        }
        sw.Stop();

        // Assert
        _output.WriteLine($"Created {bufferCount} buffers ({bufferSize} bytes each) in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {sw.ElapsedMilliseconds / (double)bufferCount:F3}ms per buffer");

        foreach (var buffer in buffers)
            buffer.Dispose();

        sw.ElapsedMilliseconds.Should().BeLessThan(10000);
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_TextureCreation_100Textures_ShouldBeFast()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        const int textureCount = 100;

        var sw = Stopwatch.StartNew();

        // Act
        var textures = new List<IComputeTexture>();
        for (int i = 0; i < textureCount; i++)
        {
            textures.Add(_device.CreateTexture2D(1024, 1024, TextureFormat.R16_Float));
        }
        sw.Stop();

        // Assert
        _output.WriteLine($"Created {textureCount} 1024x1024 textures in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {sw.ElapsedMilliseconds / (double)textureCount:F3}ms per texture");

        foreach (var texture in textures)
            texture.Dispose();

        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    [Theory(Skip = "Benchmark - run manually")]
    [InlineData(512, 512)]
    [InlineData(1024, 1024)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 4096)]
    public void Benchmark_ImageAlignment_VariousSizes(int width, int height)
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);

        var referenceImage = CreateTestImage(width, height);
        var compareImage = CreateTestImage(width, height);

        var sw = Stopwatch.StartNew();

        // Act
        var result = aligner.Align(referenceImage, compareImage);

        sw.Stop();

        // Assert
        _output.WriteLine($"Aligned {width}x{height} image in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Megapixels: {width * height / 1_000_000.0:F2}MP");
        _output.WriteLine($"Throughput: {width * height / 1000.0 / sw.ElapsedMilliseconds:F2} megapixels/sec");

        result.Should().NotBeNull();
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_BurstAlignment_50Images_ShouldMeetPerformanceTarget()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);

        var referenceImage = CreateTestImage(2048, 1536);
        var burstImages = Enumerable.Range(0, 50)
            .Select(_ => CreateTestImage(2048, 1536))
            .ToList();

        var sw = Stopwatch.StartNew();

        // Act
        var results = burstImages
            .Select(img => aligner.Align(referenceImage, img))
            .ToList();

        sw.Stop();

        // Assert
        var avgTimePerImage = sw.ElapsedMilliseconds / (double)burstImages.Count;
        _output.WriteLine($"Processed {burstImages.Count} images (2048x1536) in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {avgTimePerImage:F2}ms per image");
        _output.WriteLine($"Total megapixels: {burstImages.Count * 2048 * 1536 / 1_000_000.0:F2}MP");

        // Target: Should process a typical burst (50x 3MP images) in reasonable time
        sw.ElapsedMilliseconds.Should().BeLessThan(60000, "50-image burst should take less than 60 seconds");
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_DngWrite_ShouldBeFast()
    {
        // Arrange
        var writer = new DngWriter();
        var testImage = CreateTestImage(2048, 1536);
        var tempPath = Path.GetTempFileName() + ".dng";

        var sw = Stopwatch.StartNew();

        // Act
        writer.Write(testImage, tempPath);

        sw.Stop();

        // Assert
        _output.WriteLine($"Wrote DNG (2048x1536) in {sw.ElapsedMilliseconds}ms");
        var fileSize = new FileInfo(tempPath).Length;
        _output.WriteLine($"File size: {fileSize / 1024.0 / 1024.0:F2}MB");
        _output.WriteLine($"Write speed: {fileSize / 1024.0 / 1024.0 / (sw.ElapsedMilliseconds / 1000.0):F2}MB/s");

        File.Delete(tempPath);

        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "DNG write should take less than 5 seconds");
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_DngRead_ShouldBeFast()
    {
        // Arrange
        var writer = new DngWriter();
        var reader = new SimpleRawReader();
        var testImage = CreateTestImage(2048, 1536);
        var tempPath = Path.GetTempFileName() + ".dng";

        writer.Write(testImage, tempPath);

        var sw = Stopwatch.StartNew();

        // Act
        var loadedImage = reader.Read(tempPath);

        sw.Stop();

        // Assert
        _output.WriteLine($"Read DNG (2048x1536) in {sw.ElapsedMilliseconds}ms");
        var fileSize = new FileInfo(tempPath).Length;
        _output.WriteLine($"Read speed: {fileSize / 1024.0 / 1024.0 / (sw.ElapsedMilliseconds / 1000.0):F2}MB/s");

        File.Delete(tempPath);

        sw.ElapsedMilliseconds.Should().BeLessThan(3000, "DNG read should take less than 3 seconds");
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_MemoryUsage_During50ImageBurst()
    {
        // Arrange
        _device = ComputeDeviceFactory.CreateDefault();
        var aligner = new ImageAligner(_device);

        var initialMemory = GC.GetTotalMemory(true);
        _output.WriteLine($"Initial memory: {initialMemory / 1024.0 / 1024.0:F2}MB");

        // Act
        var referenceImage = CreateTestImage(2048, 1536);

        for (int i = 0; i < 50; i++)
        {
            var compareImage = CreateTestImage(2048, 1536);
            var result = aligner.Align(referenceImage, compareImage);

            if (i % 10 == 0)
            {
                var currentMemory = GC.GetTotalMemory(false);
                _output.WriteLine($"After {i} images: {currentMemory / 1024.0 / 1024.0:F2}MB");
            }
        }

        var finalMemory = GC.GetTotalMemory(false);
        _output.WriteLine($"Final memory: {finalMemory / 1024.0 / 1024.0:F2}MB");

        // Assert
        var memoryIncrease = finalMemory - initialMemory;
        _output.WriteLine($"Memory increase: {memoryIncrease / 1024.0 / 1024.0:F2}MB");

        // Should not leak excessive memory
        memoryIncrease.Should().BeLessThan(500 * 1024 * 1024, "Should not leak more than 500MB");
    }

    [Fact(Skip = "Benchmark - run manually")]
    public void Benchmark_CompareBackends_DirectX12VsVulkan()
    {
        // Arrange
        var backends = new[] { ComputeBackend.DirectX12, ComputeBackend.Vulkan };
        var results = new Dictionary<ComputeBackend, long>();

        foreach (var backend in backends)
        {
            try
            {
                _device?.Dispose();
                _device = ComputeDeviceFactory.Create(backend);
                var aligner = new ImageAligner(_device);

                var referenceImage = CreateTestImage(1024, 1024);
                var compareImage = CreateTestImage(1024, 1024);

                var sw = Stopwatch.StartNew();
                var result = aligner.Align(referenceImage, compareImage);
                sw.Stop();

                results[backend] = sw.ElapsedMilliseconds;
                _output.WriteLine($"{backend}: {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"{backend}: Not available ({ex.Message})");
            }
        }

        // Assert
        if (results.Count >= 2)
        {
            var difference = Math.Abs(results.Values.First() - results.Values.Last());
            var slower = results.Values.Max();
            var percentDiff = (difference / (double)slower) * 100;

            _output.WriteLine($"Performance difference: {percentDiff:F1}%");
            percentDiff.Should().BeLessThan(30, "Backend performance should be within 30% of each other");
        }
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

    public void Dispose()
    {
        _device?.Dispose();
    }
}
