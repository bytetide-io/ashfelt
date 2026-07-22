using System.Collections.Generic;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Placeholder flat-colour tile rendering. Phase 5 replaces this with a real
/// pixel-art TileSet; the chunk data it consumes does not change.
/// </summary>
public partial class ChunkRenderer : Node2D
{
    public const int TilePixels = 16;

    private static readonly Dictionary<TileType, Color> Palette = new()
    {
        [TileType.DeepWater] = new Color("1b3b66"),
        [TileType.Water] = new Color("2f6690"),
        [TileType.Sand] = new Color("d9c27e"),
        [TileType.Grass] = new Color("4a7c40"),
        [TileType.Forest] = new Color("2c5230"),
        [TileType.Rock] = new Color("6e6a63"),
    };

    private readonly Dictionary<ChunkCoord, TileType[]> _chunks = new();

    public void SetChunk(ChunkCoord coord, TileType[] tiles)
    {
        _chunks[coord] = tiles;
        QueueRedraw();
    }

    public override void _Draw()
    {
        int size = TerrainGenerator.ChunkSize;
        foreach (var (coord, tiles) in _chunks)
        {
            var origin = new Vector2(coord.X * size * TilePixels, coord.Y * size * TilePixels);
            for (int ly = 0; ly < size; ly++)
            {
                for (int lx = 0; lx < size; lx++)
                {
                    var rect = new Rect2(
                        origin + new Vector2(lx * TilePixels, ly * TilePixels),
                        new Vector2(TilePixels, TilePixels));
                    DrawRect(rect, Palette[tiles[ly * size + lx]]);
                }
            }
        }
    }
}
