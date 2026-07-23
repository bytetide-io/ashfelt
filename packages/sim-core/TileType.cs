namespace Ashfall.SimCore;

public enum TileType : byte
{
    DeepWater = 0,
    Water = 1,
    Sand = 2,
    Grass = 3,
    Forest = 4,
    Rock = 5,
    /// <summary>Low plants scattered in grassland; walkable, gathered for fiber.</summary>
    Shrub = 6,
}
