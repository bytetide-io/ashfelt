using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Phase 0 entry point: connect to the world-server and render the chunks
/// around the origin. Movement and prediction arrive in Phase 1.
/// </summary>
public partial class Main : Node2D
{
    private const int ViewRadiusInChunks = 1;

    private WorldConnection _connection = null!;
    private ChunkRenderer _renderer = null!;
    private Camera2D _camera = null!;

    public override void _Ready()
    {
        _connection = GetNode<WorldConnection>("WorldConnection");
        _renderer = GetNode<ChunkRenderer>("ChunkRenderer");
        _camera = GetNode<Camera2D>("Camera2D");

        int span = TerrainGenerator.ChunkSize * ChunkRenderer.TilePixels;
        _camera.Position = new Vector2(span / 2f, span / 2f);

        _connection.Welcomed += (_, _) =>
        {
            for (int cy = -ViewRadiusInChunks; cy <= ViewRadiusInChunks; cy++)
                for (int cx = -ViewRadiusInChunks; cx <= ViewRadiusInChunks; cx++)
                    _connection.RequestChunk(new ChunkCoord(cx, cy));
        };

        _connection.ChunkReceived += (coord, tiles) =>
            CallDeferred(nameof(ApplyChunk), coord.X, coord.Y, System.Array.ConvertAll(tiles, t => (byte)t));
    }

    private void ApplyChunk(int cx, int cy, byte[] tiles)
    {
        var typed = System.Array.ConvertAll(tiles, b => (TileType)b);
        _renderer.SetChunk(new ChunkCoord(cx, cy), typed);
    }
}
