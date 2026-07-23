using Ashfall.Proto;
using Ashfall.SimCore;
using Npgsql;

namespace Ashfall.WorldServer;

/// <summary>
/// Persistence for a single world. Terrain is never written — only the diffs
/// players cause. On startup the diffs are replayed over generated terrain.
/// </summary>
public sealed class WorldStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _db;
    private readonly string _worldId;

    private WorldStore(NpgsqlDataSource? db, string worldId)
    {
        _db = db;
        _worldId = worldId;
    }

    public bool IsPersistent => _db is not null;

    /// <summary>
    /// Connects and ensures the world row exists. When no connection string is
    /// configured the store runs in memory-only mode so local dev needs no
    /// database.
    /// </summary>
    public static async Task<WorldStore> OpenAsync(string? connectionString, string worldId, uint seed)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("[store] no ASHFALL_DB set — running without persistence");
            return new WorldStore(null, worldId);
        }

        var db = NpgsqlDataSource.Create(connectionString);
        await using (var cmd = db.CreateCommand(
            "INSERT INTO world (id, seed) VALUES ($1, $2) ON CONFLICT (id) DO NOTHING"))
        {
            cmd.Parameters.AddWithValue(worldId);
            cmd.Parameters.AddWithValue((long)seed);
            await cmd.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"[store] connected, world='{worldId}'");
        return new WorldStore(db, worldId);
    }

    /// <summary>Replays every stored diff into the world. Called once at startup.</summary>
    public async Task<int> LoadDiffsAsync(World world)
    {
        if (_db is null) return 0;

        await using var cmd = _db.CreateCommand(
            "SELECT chunk_x, chunk_y, local_x, local_y, tile FROM tile_diff WHERE world_id = $1");
        cmd.Parameters.AddWithValue(_worldId);

        int count = 0;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int wx = reader.GetInt32(0) * TerrainGenerator.ChunkSize + reader.GetInt16(2);
            int wy = reader.GetInt32(1) * TerrainGenerator.ChunkSize + reader.GetInt16(3);
            world.LoadDiff(wx, wy, (TileType)reader.GetInt16(4));
            count++;
        }
        return count;
    }

    /// <summary>
    /// Writes a single tile diff. Upsert keyed by chunk + local coordinate, so
    /// repeated edits to one tile never grow the table.
    /// </summary>
    public async Task SaveDiffAsync(int wx, int wy, TileType tile)
    {
        if (_db is null) return;

        var chunk = World.ChunkOf(wx, wy);
        int size = TerrainGenerator.ChunkSize;
        short lx = (short)(wx - chunk.X * size);
        short ly = (short)(wy - chunk.Y * size);

        await using var cmd = _db.CreateCommand("""
            INSERT INTO tile_diff (world_id, chunk_x, chunk_y, local_x, local_y, tile)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (world_id, chunk_x, chunk_y, local_x, local_y)
            DO UPDATE SET tile = EXCLUDED.tile, updated_at = now()
            """);
        cmd.Parameters.AddWithValue(_worldId);
        cmd.Parameters.AddWithValue(chunk.X);
        cmd.Parameters.AddWithValue(chunk.Y);
        cmd.Parameters.AddWithValue(lx);
        cmd.Parameters.AddWithValue(ly);
        cmd.Parameters.AddWithValue((short)tile);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Replays every stored structure into the world. Called once at startup.</summary>
    public async Task<int> LoadStructuresAsync(World world)
    {
        if (_db is null) return 0;

        await using var cmd = _db.CreateCommand(
            "SELECT id, tile_x, tile_y, kind FROM structure WHERE world_id = $1");
        cmd.Parameters.AddWithValue(_worldId);

        int count = 0;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            long id = reader.GetInt64(0);
            int tileX = reader.GetInt32(1);
            int tileY = reader.GetInt32(2);
            var kind = Enum.Parse<ItemId>(reader.GetString(3));
            world.LoadStructure(new Structure(id, tileX, tileY, kind));
            count++;
        }
        return count;
    }

    /// <summary>
    /// Writes a placed structure. The in-memory <see cref="Structure.Id"/> is the
    /// authority, so it is inserted explicitly rather than left to the sequence,
    /// keeping the id a client already saw stable across a restart.
    /// </summary>
    public async Task SaveStructureAsync(Structure structure)
    {
        if (_db is null) return;

        var chunk = World.ChunkOf(structure.TileX, structure.TileY);
        await using var cmd = _db.CreateCommand("""
            INSERT INTO structure (id, world_id, chunk_x, chunk_y, tile_x, tile_y, kind)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(structure.Id);
        cmd.Parameters.AddWithValue(_worldId);
        cmd.Parameters.AddWithValue(chunk.X);
        cmd.Parameters.AddWithValue(chunk.Y);
        cmd.Parameters.AddWithValue(structure.TileX);
        cmd.Parameters.AddWithValue(structure.TileY);
        cmd.Parameters.AddWithValue(structure.Kind.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }
}
