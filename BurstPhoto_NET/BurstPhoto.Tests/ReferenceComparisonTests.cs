using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Interfaces;
using BurstPhoto.Core.Models;
using BurstPhoto.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace BurstPhoto.Tests;

/// <summary>
/// Tests that compare C# pipeline output against Swift reference output.
/// These tests help track progress toward matching the original implementation.
/// </summary>
public class ReferenceComparisonTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testOutputDir;
    
    public ReferenceComparisonTests(ITestOutputHelper output)
    {
        _output = output;
        _testOutputDir = Path.Combine(Path.GetTempPath(), $"BurstPhotoTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testOutputDir);
    }
    
    public void Dispose()
    {
        // Cleanup test output directory
        try
        {
            if (Directory.Exists(_testOutputDir))
                Directory.Delete(_testOutputDir, true);
        }
        catch { /* Ignore cleanup errors */ }
    }

    #region Test Data Validation

    [Fact]
    public void TestData_BracketedExposureInputsExist()
    {
        var inputFiles = TestDataPaths.BracketedExposure.InputFiles;
        
        _output.WriteLine($"Found {inputFiles.Length} input files in Bracketed Exposure folder:");
        foreach (var file in inputFiles)
        {
            _output.WriteLine($"  - {Path.GetFileName(file)}");
        }
        
        Assert.NotEmpty(inputFiles);
        Assert.Equal(7, inputFiles.Length); // Expected 7 DNG files
    }

    [Fact]
    public void TestData_BracketedExposureReferenceExists()
    {
        var refOutput = TestDataPaths.BracketedExposure.ReferenceOutput;
        
        _output.WriteLine($"Reference output path: {refOutput}");
        Assert.True(File.Exists(refOutput), $"Reference output file not found: {refOutput}");
    }

    [Fact]
    public void TestData_ExiftoolExists()
    {
        var exiftoolPath = TestDataPaths.ExiftoolPath;
        
        _output.WriteLine($"Exiftool path: {exiftoolPath}");
        Assert.True(File.Exists(exiftoolPath), $"Exiftool not found: {exiftoolPath}");
    }

    #endregion

    #region Reference Output Metadata Tests

    [Fact]
    public void ReferenceMetadata_CanExtract()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        
        var metadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        
        _output.WriteLine("Reference Output Metadata:");
        _output.WriteLine($"  Dimensions: {metadata.Width} x {metadata.Height}");
        _output.WriteLine($"  Photometric: {metadata.Photometric}");
        _output.WriteLine($"  CFA Pattern: [{string.Join(", ", metadata.CfaPattern)}]");
        _output.WriteLine($"  Black Level: [{string.Join(", ", metadata.BlackLevel)}]");
        _output.WriteLine($"  White Level: {metadata.WhiteLevel}");
        _output.WriteLine($"  ColorMatrix1: [{string.Join(", ", metadata.ColorMatrix1.Select(d => d.ToString("F4")))}]");
        _output.WriteLine($"  AsShotNeutral: [{string.Join(", ", metadata.AsShotNeutral.Select(d => d.ToString("F6")))}]");
        _output.WriteLine($"  Bits Per Sample: {metadata.BitsPerSample}");
        _output.WriteLine($"  Samples Per Pixel: {metadata.SamplesPerPixel}");
        _output.WriteLine($"  Compression: {metadata.Compression}");
        
        // Basic sanity checks for reference
        Assert.Equal(4096, metadata.Width);
        Assert.Equal(3072, metadata.Height);
        Assert.Contains("Color Filter Array", metadata.Photometric);
    }

    #endregion

    #region Full Pipeline Comparison Tests

    [Fact]
    public async Task Pipeline_BracketedExposure_DimensionsMatch()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        // Run pipeline
        var outputPath = await RunPipelineAsync();
        
        // Compare dimensions
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        _output.WriteLine($"Reference: {refMetadata.Width} x {refMetadata.Height}");
        _output.WriteLine($"Actual:    {actualMetadata.Width} x {actualMetadata.Height}");
        
        Assert.Equal(refMetadata.Width, actualMetadata.Width);
        Assert.Equal(refMetadata.Height, actualMetadata.Height);
    }

    [Fact]
    public async Task Pipeline_BracketedExposure_PhotometricMatch()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        var outputPath = await RunPipelineAsync();
        
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        _output.WriteLine($"Reference Photometric: {refMetadata.Photometric}");
        _output.WriteLine($"Actual Photometric:    {actualMetadata.Photometric}");
        
        // This test is expected to FAIL initially because C# outputs RGB, not CFA
        Assert.Equal(refMetadata.Photometric, actualMetadata.Photometric);
    }

    [Fact]
    public async Task Pipeline_BracketedExposure_CfaPatternMatch()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        var outputPath = await RunPipelineAsync();
        
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        _output.WriteLine($"Reference CFA Pattern: [{string.Join(", ", refMetadata.CfaPattern)}]");
        _output.WriteLine($"Actual CFA Pattern:    [{string.Join(", ", actualMetadata.CfaPattern)}]");
        
        // Reference uses BGGR [2,1,1,0], C# hardcodes RGGB [0,1,1,2]
        Assert.Equal(refMetadata.CfaPattern, actualMetadata.CfaPattern);
    }

    [Fact]
    public async Task Pipeline_BracketedExposure_WhiteLevelMatch()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        var outputPath = await RunPipelineAsync();
        
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        _output.WriteLine($"Reference WhiteLevel: {refMetadata.WhiteLevel}");
        _output.WriteLine($"Actual WhiteLevel:    {actualMetadata.WhiteLevel}");
        
        // Reference has 65472, C# currently disables WhiteLevel tag
        Assert.Equal(refMetadata.WhiteLevel, actualMetadata.WhiteLevel);
    }

    [Fact]
    public async Task Pipeline_BracketedExposure_BlackLevelMatch()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        var outputPath = await RunPipelineAsync();
        
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        _output.WriteLine($"Reference BlackLevel: [{string.Join(", ", refMetadata.BlackLevel)}]");
        _output.WriteLine($"Actual BlackLevel:    [{string.Join(", ", actualMetadata.BlackLevel)}]");
        
        Assert.Equal(refMetadata.BlackLevel, actualMetadata.BlackLevel);
    }

    [Fact]
    public async Task Pipeline_BracketedExposure_ColorMatrixMatch()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        var outputPath = await RunPipelineAsync();
        
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        _output.WriteLine($"Reference ColorMatrix1 ({refMetadata.ColorMatrix1.Length} values):");
        _output.WriteLine($"  [{string.Join(", ", refMetadata.ColorMatrix1.Select(d => d.ToString("F4")))}]");
        _output.WriteLine($"Actual ColorMatrix1 ({actualMetadata.ColorMatrix1.Length} values):");
        _output.WriteLine($"  [{string.Join(", ", actualMetadata.ColorMatrix1.Select(d => d.ToString("F4")))}]");
        
        // Reference has real camera matrix, C# uses identity
        Assert.Equal(refMetadata.ColorMatrix1.Length, actualMetadata.ColorMatrix1.Length);
        for (int i = 0; i < refMetadata.ColorMatrix1.Length; i++)
        {
            Assert.Equal(refMetadata.ColorMatrix1[i], actualMetadata.ColorMatrix1[i], precision: 4);
        }
    }

    [Fact]
    public async Task Pipeline_BracketedExposure_AsShotNeutralMatch()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        var outputPath = await RunPipelineAsync();
        
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        _output.WriteLine($"Reference AsShotNeutral: [{string.Join(", ", refMetadata.AsShotNeutral.Select(d => d.ToString("F6")))}]");
        _output.WriteLine($"Actual AsShotNeutral:    [{string.Join(", ", actualMetadata.AsShotNeutral.Select(d => d.ToString("F6")))}]");
        
        Assert.Equal(refMetadata.AsShotNeutral.Length, actualMetadata.AsShotNeutral.Length);
        for (int i = 0; i < refMetadata.AsShotNeutral.Length; i++)
        {
            Assert.Equal(refMetadata.AsShotNeutral[i], actualMetadata.AsShotNeutral[i], precision: 4);
        }
    }

    #endregion

    #region Summary Test

    [Fact]
    public async Task Pipeline_BracketedExposure_MetadataSummary()
    {
        Skip.IfNot(File.Exists(TestDataPaths.BracketedExposure.ReferenceOutput), 
            "Reference output not found");
        Skip.If(TestDataPaths.BracketedExposure.InputFiles.Length == 0,
            "Input files not found");
        
        var outputPath = await RunPipelineAsync();
        
        var refMetadata = DngComparisonHelper.ExtractMetadata(TestDataPaths.BracketedExposure.ReferenceOutput);
        var actualMetadata = DngComparisonHelper.ExtractMetadata(outputPath);
        
        // Generate comparison report
        _output.WriteLine("=== METADATA COMPARISON SUMMARY ===");
        _output.WriteLine("");
        
        var dimensionsMatch = refMetadata.Width == actualMetadata.Width && refMetadata.Height == actualMetadata.Height;
        var photometricMatch = refMetadata.Photometric == actualMetadata.Photometric;
        var cfaMatch = refMetadata.CfaPattern.SequenceEqual(actualMetadata.CfaPattern);
        var whiteLevelMatch = refMetadata.WhiteLevel == actualMetadata.WhiteLevel;
        var blackLevelMatch = refMetadata.BlackLevel.SequenceEqual(actualMetadata.BlackLevel);
        
        _output.WriteLine($"Dimensions:      {(dimensionsMatch ? "✓ MATCH" : "✗ MISMATCH")}");
        _output.WriteLine($"  Reference: {refMetadata.Width}x{refMetadata.Height}");
        _output.WriteLine($"  Actual:    {actualMetadata.Width}x{actualMetadata.Height}");
        _output.WriteLine("");
        
        _output.WriteLine($"Photometric:     {(photometricMatch ? "✓ MATCH" : "✗ MISMATCH")}");
        _output.WriteLine($"  Reference: {refMetadata.Photometric}");
        _output.WriteLine($"  Actual:    {actualMetadata.Photometric}");
        _output.WriteLine("");
        
        _output.WriteLine($"CFA Pattern:     {(cfaMatch ? "✓ MATCH" : "✗ MISMATCH")}");
        _output.WriteLine($"  Reference: [{string.Join(",", refMetadata.CfaPattern)}]");
        _output.WriteLine($"  Actual:    [{string.Join(",", actualMetadata.CfaPattern)}]");
        _output.WriteLine("");
        
        _output.WriteLine($"White Level:     {(whiteLevelMatch ? "✓ MATCH" : "✗ MISMATCH")}");
        _output.WriteLine($"  Reference: {refMetadata.WhiteLevel}");
        _output.WriteLine($"  Actual:    {actualMetadata.WhiteLevel}");
        _output.WriteLine("");
        
        _output.WriteLine($"Black Level:     {(blackLevelMatch ? "✓ MATCH" : "✗ MISMATCH")}");
        _output.WriteLine($"  Reference: [{string.Join(",", refMetadata.BlackLevel)}]");
        _output.WriteLine($"  Actual:    [{string.Join(",", actualMetadata.BlackLevel)}]");
        _output.WriteLine("");
        
        int passed = (dimensionsMatch ? 1 : 0) + (photometricMatch ? 1 : 0) + (cfaMatch ? 1 : 0) +
                     (whiteLevelMatch ? 1 : 0) + (blackLevelMatch ? 1 : 0);
        _output.WriteLine($"=== {passed}/5 checks passed ===");
        
        // This test always passes - it's for reporting purposes
        // Individual tests above will fail on specific mismatches
    }

    #endregion

    #region Helper Methods

    private async Task<string> RunPipelineAsync()
    {
        // Create pipeline components
        var loader = new LibRawLoader();
        var writer = new DngSdkWriter();
        
        // Use PassthroughComputePipeline for testing - we're focused on metadata comparison
        // and don't need full GPU processing for these tests
        IComputePipeline compute = new PassthroughComputePipeline();
        
        var pipeline = new DenoisePipeline(loader, writer, compute);
        
        // Configure options to match Swift reference settings:
        // Noise Reduction: 5, Tile Size: Medium, Search distance: Medium, 
        // Merging: Fast, Exposure: Linear (full bit range), Output: Native
        var options = new ProcessingOptions
        {
            NoiseReduction = 5.0,
            TileSize = TileSizeOption.Medium,
            SearchDistance = SearchDistanceOption.Medium,
            Merging = MergingAlgorithm.Fast,
            ExposureControl = ExposureControlOption.LinearFullRange,
            OutputBitDepth = OutputBitDepthOption.Native
        };
        
        var progress = new ProcessingProgress();
        var inputFiles = TestDataPaths.BracketedExposure.InputFiles;
        
        _output.WriteLine($"Processing {inputFiles.Length} input files...");
        _output.WriteLine($"Output directory: {_testOutputDir}");
        
        var outputPath = await pipeline.ProcessAsync(
            inputFiles,
            options,
            progress,
            _testOutputDir,
            CancellationToken.None);
        
        _output.WriteLine($"Pipeline output: {outputPath}");
        Assert.True(File.Exists(outputPath), $"Pipeline did not create output file: {outputPath}");
        
        return outputPath;
    }

    #endregion
}
