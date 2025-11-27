namespace BurstPhoto.Core.Models;

public struct TileInfo
{
    public int TileSize { get; set; }
    public int TileSizeMerge { get; set; }
    public int SearchDist { get; set; }
    public int NTilesX { get; set; }
    public int NTilesY { get; set; }
    public int NPos1D { get; set; }
    public int NPos2D { get; set; }
}
