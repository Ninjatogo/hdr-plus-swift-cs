namespace HdrPlus.Core.Alignment;

/// <summary>
/// Information about tile-based alignment at a pyramid level.
/// Corresponds to TileInfo struct in align.swift:24
/// </summary>
public record struct TileInfo(
    int TileSize,
    int TileSizeMerge,
    int SearchDist,
    int NTilesX,
    int NTilesY,
    int NPos1D,
    int NPos2D
);
