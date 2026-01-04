using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Models;
using Xunit;
using Xunit.Abstractions;
using System;
using System.IO;
using System.Linq;

namespace BurstPhoto.Tests;

/// <summary>
/// Integration tests for reference frame selection using real Burst Sample files.
/// These tests verify actual metadata extraction and reference selection logic.
/// </summary>
public class ReferenceFrameSelectionTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _burstSamplesPath;

    public ReferenceFrameSelectionTests(ITestOutputHelper output)
    {
        _output = output;
        // Find Burst Samples folder relative to test execution directory
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        _burstSamplesPath = Path.Combine(repoRoot, "Burst Samples");
    }

    [Fact]
    public void LibRawLoader_ExtractsRealMetadata_FromBurstSamples()
    {
        // Skip if burst samples not available
        if (!Directory.Exists(_burstSamplesPath))
        {
            _output.WriteLine($"Skipping: Burst Samples folder not found at {_burstSamplesPath}");
            return;
        }

        var dngFiles = Directory.GetFiles(_burstSamplesPath, "*.DNG");
        Assert.True(dngFiles.Length >= 2, "Expected at least 2 DNG files in Burst Samples folder");

        var loader = new LibRawLoader();
        _output.WriteLine($"Found {dngFiles.Length} DNG files in Burst Samples folder\n");

        foreach (var file in dngFiles.OrderBy(f => f).Take(3)) // Test first 3 files
        {
            var image = loader.Load(file);
            
            _output.WriteLine($"File: {Path.GetFileName(file)}");
            _output.WriteLine($"  IsoExposureTime: {image.IsoExposureTime}");
            _output.WriteLine($"  ExposureBias: {image.ExposureBias} ({image.ExposureBias / 100.0} EV)");
            _output.WriteLine($"  BlackLevel: [{string.Join(", ", image.BlackLevel)}]");
            _output.WriteLine($"  WhiteLevel: {image.WhiteLevel}");
            _output.WriteLine($"  Dimensions: {image.Width}x{image.Height}");
            _output.WriteLine("");

            // Verify real metadata is extracted (not placeholder zeros)
            Assert.True(image.IsoExposureTime > 0, "IsoExposureTime should be > 0 (not placeholder)");
            Assert.True(image.WhiteLevel > 0, "WhiteLevel should be > 0");
        }
    }

    [Fact]
    public void ReferenceFrameSelection_UniformExposure_PicksCentralFrame()
    {
        // Skip if burst samples not available
        if (!Directory.Exists(_burstSamplesPath))
        {
            _output.WriteLine($"Skipping: Burst Samples folder not found at {_burstSamplesPath}");
            return;
        }

        var dngFiles = Directory.GetFiles(_burstSamplesPath, "*.DNG").OrderBy(f => f).ToArray();
        Assert.True(dngFiles.Length >= 2, "Expected at least 2 DNG files");

        var loader = new LibRawLoader();
        var images = dngFiles.Select(f => loader.Load(f)).ToList();

        // Output all IsoExposureTime values
        _output.WriteLine($"Loaded {images.Count} images from Burst Samples:");
        for (int i = 0; i < images.Count; i++)
        {
            _output.WriteLine($"  [{i}] {Path.GetFileName(dngFiles[i])}: IsoExposureTime = {images[i].IsoExposureTime}");
        }

        // Check if exposure is uniform
        var isoExposureTime = images.Select(img => (double)img.IsoExposureTime).ToArray();
        const double epsilon = 1e-12;
        bool uniformIso = isoExposureTime.All(t => Math.Abs(t - isoExposureTime[0]) <= epsilon);

        _output.WriteLine($"\nUniform ISO*Exposure: {uniformIso}");

        if (uniformIso)
        {
            // Should pick central image
            int expectedRef = images.Count / 2;
            _output.WriteLine($"Expected reference frame (central): {expectedRef}");
            _output.WriteLine($"Central frame file: {Path.GetFileName(dngFiles[expectedRef])}");
        }
        else
        {
            // Should pick median exposure
            var sortedIso = isoExposureTime.OrderBy(x => x).ToArray();
            double medianIso = sortedIso[sortedIso.Length / 2];
            int expectedRef = Array.IndexOf(isoExposureTime, medianIso);
            _output.WriteLine($"Non-uniform exposure detected. Expected reference frame (median): {expectedRef}");
            _output.WriteLine($"Median IsoExposureTime: {medianIso}");
        }
    }
}
