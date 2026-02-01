namespace BurstPhoto.Core.Models;

/// <summary>
/// Contains configuration and computed layout information for image tiles used during alignment.
/// </summary>
/// <remarks>
/// The alignment algorithm divides the image into overlapping tiles to find local motion vectors.
/// Each tile is independently aligned, then the results are merged to produce the final aligned image.
/// Tiles overlap by 50% (stride = TileSize/2) to ensure smooth blending at boundaries.
/// </remarks>
public struct TileInfo
{
    /// <summary>
    /// The size of each alignment tile in pixels (width and height are equal).
    /// </summary>
    /// <remarks>
    /// Smaller tiles capture finer motion detail but are more sensitive to noise.
    /// Typical values: 16 (fine detail), 32 (balanced), 64 (coarse/fast).
    /// </remarks>
    public int TileSize { get; set; }

    /// <summary>
    /// The size of the merge region in pixels, which is twice the tile size.
    /// </summary>
    /// <remarks>
    /// The merge region is larger than the alignment tile to accommodate the blending
    /// overlap between adjacent tiles. This ensures seamless transitions without visible seams.
    /// </remarks>
    public int TileSizeForMerging { get; set; }

    /// <summary>
    /// The maximum search distance in pixels when finding tile alignments.
    /// </summary>
    /// <remarks>
    /// Larger values can find bigger motions but increase computation time.
    /// This defines the radius of the search area around each tile's original position.
    /// </remarks>
    public int SearchDistance { get; set; }

    /// <summary>
    /// The number of tiles that fit horizontally across the image.
    /// </summary>
    /// <remarks>
    /// Calculated based on image width, tile size, and 50% overlap (stride = TileSize/2).
    /// </remarks>
    public int TileCountX { get; set; }

    /// <summary>
    /// The number of tiles that fit vertically across the image.
    /// </summary>
    /// <remarks>
    /// Calculated based on image height, tile size, and 50% overlap (stride = TileSize/2).
    /// </remarks>
    public int TileCountY { get; set; }

    /// <summary>
    /// The number of search positions per dimension (horizontal or vertical).
    /// </summary>
    /// <remarks>
    /// This is the count of discrete positions searched in one direction.
    /// For example, if this is 5, the search covers positions: -2, -1, 0, +1, +2.
    /// </remarks>
    public int SearchPositionsPerDimension { get; set; }

    /// <summary>
    /// The total number of search positions (2D grid of positions to evaluate).
    /// </summary>
    /// <remarks>
    /// Equals <see cref="SearchPositionsPerDimension"/> squared. Each tile is compared
    /// at this many offset positions to find the best alignment.
    /// </remarks>
    public int TotalSearchPositions { get; set; }

    /// <summary>
    /// Calculates tile layout information for the given image dimensions and processing parameters.
    /// </summary>
    /// <param name="imageWidth">Image width in pixels.</param>
    /// <param name="imageHeight">Image height in pixels.</param>
    /// <param name="tileSize">Tile size in pixels (width and height).</param>
    /// <param name="searchDistance">Maximum search distance in pixels.</param>
    /// <returns>A <see cref="TileInfo"/> struct with all computed layout values.</returns>
    public static TileInfo Calculate(int imageWidth, int imageHeight, int tileSize, int searchDistance)
    {
        // Merge region is 2x tile size to accommodate blending overlap
        var tileSizeForMerging = tileSize * 2;

        // Calculate tile count with 50% overlap (stride = tileSize/2)
        // This ensures neighboring tiles share half their area for smooth blending
        var tileStride = tileSize / 2;
        var tileCountX = Math.Max(1, (imageWidth - tileSize) / tileStride + 1);
        var tileCountY = Math.Max(1, (imageHeight - tileSize) / tileStride + 1);

        // Calculate search positions: we sample at intervals of tileSize/4
        // The +1 accounts for the center position (zero offset)
        var searchStepSize = tileSize / 4;
        var searchPositionsPerDimension = (searchDistance / searchStepSize) * 2 + 1;
        var totalSearchPositions = searchPositionsPerDimension * searchPositionsPerDimension;

        return new TileInfo
        {
            TileSize = tileSize,
            TileSizeForMerging = tileSizeForMerging,
            SearchDistance = searchDistance,
            TileCountX = tileCountX,
            TileCountY = tileCountY,
            SearchPositionsPerDimension = searchPositionsPerDimension,
            TotalSearchPositions = totalSearchPositions
        };
    }
}
