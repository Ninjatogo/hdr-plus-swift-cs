using BurstPhoto.Core.Models;

namespace BurstPhoto.Tests;

/// <summary>
/// Tests for TileInfo calculation logic.
/// These tests verify the tile grid calculation matches expected values.
/// </summary>
public class TileInfoTests
{
    [Theory]
    // Formula: nTilesX = (width - tileSize) / (tileSize / 2) + 1
    [InlineData(4000, 3000, 32, 64, 249, 186)] // (4000-32)/16+1=249, (3000-32)/16+1=186
    [InlineData(4000, 3000, 16, 128, 499, 374)] // (4000-16)/8+1=499, (3000-16)/8+1=374
    [InlineData(4000, 3000, 64, 32, 124, 92)]   // (4000-64)/32+1=124, (3000-64)/32+1=92
    public void Calculate_ReturnsCorrectTileCounts(
        int width, int height, int tileSize, int searchDist, 
        int expectedNTilesX, int expectedNTilesY)
    {
        // Act
        var result = TileInfo.Calculate(width, height, tileSize, searchDist);

        // Assert
        Assert.Equal(expectedNTilesX, result.NTilesX);
        Assert.Equal(expectedNTilesY, result.NTilesY);
    }

    [Fact]
    public void Calculate_TileSizeMerge_IsDoubleTileSize()
    {
        // Act
        var result = TileInfo.Calculate(1000, 1000, 32, 64);

        // Assert
        Assert.Equal(64, result.TileSizeMerge);
    }

    [Fact]
    public void Calculate_SearchDistStored()
    {
        // Act
        var result = TileInfo.Calculate(1000, 1000, 32, 64);

        // Assert
        Assert.Equal(64, result.SearchDist);
        Assert.Equal(32, result.TileSize);
    }

    [Theory]
    [InlineData(32, 64, 17)]   // 64 / (32/4) * 2 + 1 = 64/8*2+1 = 17
    [InlineData(16, 128, 65)]  // 128 / (16/4) * 2 + 1 = 128/4*2+1 = 65
    [InlineData(64, 32, 5)]    // 32 / (64/4) * 2 + 1 = 32/16*2+1 = 5
    public void Calculate_NPos1D_MatchesFormula(int tileSize, int searchDist, int expectedNPos1D)
    {
        // Act
        var result = TileInfo.Calculate(1000, 1000, tileSize, searchDist);

        // Assert
        Assert.Equal(expectedNPos1D, result.NPos1D);
        Assert.Equal(expectedNPos1D * expectedNPos1D, result.NPos2D);
    }

    [Fact]
    public void Calculate_SmallImage_ReturnsMinimumOneTile()
    {
        // Very small image that might result in zero tiles
        var result = TileInfo.Calculate(16, 16, 32, 64);

        // Should still have at least 1 tile
        Assert.True(result.NTilesX >= 1);
        Assert.True(result.NTilesY >= 1);
    }
}
