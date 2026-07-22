using System.Collections.Generic;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Owns the client's copy of the world and paints it.
///
/// Ground goes into a TileMapLayer (batched by the engine, unlike the
/// per-tile draw calls this replaced), while anything with height becomes a
/// sprite in the Y-sorted <see cref="EntityLayer"/>.
/// </summary>
public partial class WorldView : Node
{
    /// <summary>Art is 32x32. Purely a client concern — sim-core has no pixels.</summary>
    public const int TilePixels = 32;

    private const int GroundSource = 0;
    private const int Variants = 4;

    /// <summary>Salt keeping decorative variation independent of terrain noise.</summary>
    private const uint DecorSalt = 0x0DEC0DE1;

    [Export] public NodePath GroundPath { get; set; } = "../Ground";
    [Export] public NodePath EntitiesPath { get; set; } = "../Entities";

    public TileMapLayer Ground { get; private set; } = null!;
    public EntityLayer Entities { get; private set; } = null!;

    public override void _Ready()
    {
        Ground = GetNode<TileMapLayer>(GroundPath);
        Entities = GetNode<EntityLayer>(EntitiesPath);
    }

    private readonly Dictionary<ChunkCoord, TileType[]> _chunks = new();

    public uint Seed { get; set; }
    public int ChunkCount => _chunks.Count;

    public bool HasChunk(ChunkCoord coord) => _chunks.ContainsKey(coord);

    public void SetChunk(ChunkCoord coord, TileType[] tiles)
    {
        _chunks[coord] = tiles;

        int size = TerrainGenerator.ChunkSize;
        for (int ly = 0; ly < size; ly++)
            for (int lx = 0; lx < size; lx++)
                PaintTile(coord.X * size + lx, coord.Y * size + ly, tiles[ly * size + lx]);
    }

    /// <summary>Applies a single authoritative tile diff from the server.</summary>
    public void SetTile(int wx, int wy, TileType tile)
    {
        var coord = World.ChunkOf(wx, wy);
        if (!_chunks.TryGetValue(coord, out var tiles)) return;

        int size = TerrainGenerator.ChunkSize;
        tiles[(wy - coord.Y * size) * size + (wx - coord.X * size)] = tile;
        PaintTile(wx, wy, tile);
    }

    public TileType? TileAt(int wx, int wy)
    {
        var coord = World.ChunkOf(wx, wy);
        if (!_chunks.TryGetValue(coord, out var tiles)) return null;

        int size = TerrainGenerator.ChunkSize;
        return tiles[(wy - coord.Y * size) * size + (wx - coord.X * size)];
    }

    private void PaintTile(int wx, int wy, TileType tile)
    {
        // A tree stands on grass. Painting forest-floor under it would outline
        // every trunk with a visible square, which is the classic tile-grid
        // giveaway; the canopy is what makes it read as woodland.
        var ground = tile == TileType.Forest ? TileType.Grass : tile;
        Ground.SetCell(new Vector2I(wx, wy), GroundSource,
            new Vector2I(Variant(wx, wy), (int)ground));

        switch (tile)
        {
            case TileType.Forest:
                Entities.SetProp(wx, wy, PropKind.Tree, Variant(wx, wy));
                break;
            case TileType.Rock:
                Entities.SetProp(wx, wy, PropKind.Boulder, Variant(wx, wy));
                break;
            default:
                // Chopped or mined: the prop goes away, the ground stays.
                Entities.ClearProp(wx, wy);
                break;
        }
    }

    /// <summary>
    /// Picks one of the tile variants. Derived from the shared noise hash, so
    /// every client decorates identically without a byte of extra traffic.
    /// </summary>
    private int Variant(int wx, int wy) =>
        (int)(SimCore.Noise.Hash(wx, wy, Seed ^ DecorSalt) % Variants);
}
