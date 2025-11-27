using BurstPhoto.Core.Implementations;
using BurstPhoto.Core.Models;
using Xunit;
using System;
using System.IO;

namespace BurstPhoto.Tests;

public class LibRawLoaderTests
{
    [Fact]
    public void Load_Dji0011_ReturnsValidRawImage()
    {
        // Arrange
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var dngPath = Path.Combine(repoRoot, "DJI_0011.DNG");

        if (!File.Exists(dngPath))
        {
             // Try lowercase
             dngPath = Path.Combine(repoRoot, "DJI_0011.dng");
             if (!File.Exists(dngPath))
             {
                 // Try relative to current directory if running via dotnet test?
                 // But in this environment, I know where it SHOULD be.
                 throw new FileNotFoundException($"Test file not found at {dngPath}.");
             }
        }

        var loader = new LibRawLoader();

        // Act
        RawImage result = loader.Load(dngPath);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
        Assert.NotNull(result.Data);
        Assert.NotEmpty(result.Data);

        // Current implementation returns RGB (3 channels) 16-bit
        Assert.Equal(result.Width * result.Height * 3, result.Data.Length);

        Assert.True(result.WhiteLevel > 0);
        Assert.NotNull(result.ColorFactors);
        Assert.Equal(4, result.ColorFactors.Length);
    }
}
