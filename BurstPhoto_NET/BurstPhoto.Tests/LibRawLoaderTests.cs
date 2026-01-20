using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Models;
using Xunit;
using Xunit.Abstractions;
using System;
using System.IO;

namespace BurstPhoto.Tests;

public class LibRawLoaderTests
{
    private readonly ITestOutputHelper _output;

    public LibRawLoaderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public void Load_Dji0011_ReturnsValidRawImage()
    {
        // Arrange
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var dngPath = Path.Combine(repoRoot, "DJI_0011.DNG");

        // Try lowercase if uppercase not found
        if (!File.Exists(dngPath))
        {
            dngPath = Path.Combine(repoRoot, "DJI_0011.dng");
        }

        // Skip test if file doesn't exist
        Skip.If(!File.Exists(dngPath), $"Test file not found at {dngPath}");

        var loader = new LibRawLoader();

        // Act
        RawImage result = loader.Load(dngPath);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
        Assert.NotNull(result.Data);
        Assert.NotEmpty(result.Data);

        // Current implementation returns single-channel Bayer data (not RGB)
        _output.WriteLine($"Dimensions: {result.Width}x{result.Height}");
        _output.WriteLine($"Data length: {result.Data.Length}");
        _output.WriteLine($"Expected: {result.Width * result.Height}");
        _output.WriteLine($"IsBayerData: {result.IsBayerData}");
        _output.WriteLine($"CfaPattern: [{string.Join(", ", result.CfaPattern)}]");
        
        Assert.True(result.IsBayerData, "Expected Bayer data flag to be true");
        Assert.Equal(result.Width * result.Height, result.Data.Length);

        Assert.True(result.WhiteLevel > 0);
        Assert.NotNull(result.ColorFactors);
        Assert.Equal(4, result.ColorFactors.Length);
        
        // Verify CFA pattern is valid
        Assert.NotNull(result.CfaPattern);
        Assert.Equal(4, result.CfaPattern.Length);
    }
}
