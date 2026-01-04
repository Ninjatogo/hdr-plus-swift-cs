namespace BurstPhoto.Core.Models;

/// <summary>
/// Contains all relevant information about image tiles for alignment.
/// </summary>
public struct TileInfo
{
    public int TileSize { get; set; }
    public int TileSizeMerge { get; set; }
    public int SearchDist { get; set; }
    public int NTilesX { get; set; }
    public int NTilesY { get; set; }
    public int NPos1D { get; set; }
    public int NPos2D { get; set; }

    /// <summary>
    /// Calculates tile information for the given image dimensions and processing parameters.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="tileSize">Tile size in pixels.</param>
    /// <param name="searchDist">Search distance in pixels.</param>
    /// <returns>A TileInfo struct with calculated values.</returns>
    public static TileInfo Calculate(int width, int height, int tileSize, int searchDist)
    {
        // Tile overlap is half tile size (tile_size / 2 stride)
        int tileSizeMerge = tileSize * 2;
        
        // Number of tiles = tiles that fit with 50% overlap
        // Formula from Swift: tiles fit with stride of tileSize/2
        int nTilesX = Math.Max(1, (width - tileSize) / (tileSize / 2) + 1);
        int nTilesY = Math.Max(1, (height - tileSize) / (tileSize / 2) + 1);
        
        // Search positions: search_dist / (tile_size / 4) * 2 + 1
        // This gives us positions in each direction plus center
        int nPos1D = searchDist / (tileSize / 4) * 2 + 1;
        int nPos2D = nPos1D * nPos1D;

        return new TileInfo
        {
            TileSize = tileSize,
            TileSizeMerge = tileSizeMerge,
            SearchDist = searchDist,
            NTilesX = nTilesX,
            NTilesY = nTilesY,
            NPos1D = nPos1D,
            NPos2D = nPos2D
        };
    }
}
