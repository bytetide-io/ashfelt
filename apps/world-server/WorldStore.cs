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

    /// <summary>
    /// Replays every stored build site into fully-reconstructed <see cref="BuildSite"/>
    /// objects: their pieces, which are built, and the materials on site. Called
    /// once at startup, exactly like diffs and structures.
    /// </summary>
    public async Task<IReadOnlyList<BuildSite>> LoadBuildSitesAsync()
    {
        if (_db is null) return Array.Empty<BuildSite>();

        var owners = new Dictionary<long, Guid>();
        await using (var cmd = _db.CreateCommand(
            "SELECT id, owner_id FROM blueprint WHERE world_id = $1"))
        {
            cmd.Parameters.AddWithValue(_worldId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                owners[reader.GetInt64(0)] = reader.GetGuid(1);
        }

        if (owners.Count == 0) return Array.Empty<BuildSite>();

        var pieces = new Dictionary<long, List<PlannedPiece>>();
        var built = new Dictionary<long, List<PieceSlot>>();
        await using (var cmd = _db.CreateCommand("""
            SELECT p.blueprint_id, p.x, p.y, p.level, p.layer, p.kind, p.material, p.built
            FROM blueprint_piece p JOIN blueprint b ON b.id = p.blueprint_id
            WHERE b.world_id = $1
            """))
        {
            cmd.Parameters.AddWithValue(_worldId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                long id = reader.GetInt64(0);
                var slot = new PieceSlot(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                    (PieceLayer)reader.GetInt16(4));
                var piece = new PlannedPiece((BuildPieceKind)reader.GetInt16(5), (BuildMaterial)reader.GetInt16(6), slot);
                (pieces.TryGetValue(id, out var list) ? list : pieces[id] = new()).Add(piece);
                if (reader.GetBoolean(7))
                    (built.TryGetValue(id, out var b) ? b : built[id] = new()).Add(slot);
            }
        }

        var storage = new Dictionary<long, List<(ItemId, int)>>();
        await using (var cmd = _db.CreateCommand("""
            SELECT s.blueprint_id, s.item, s.amount
            FROM build_site_storage s JOIN blueprint b ON b.id = s.blueprint_id
            WHERE b.world_id = $1
            """))
        {
            cmd.Parameters.AddWithValue(_worldId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                long id = reader.GetInt64(0);
                (storage.TryGetValue(id, out var list) ? list : storage[id] = new())
                    .Add(((ItemId)reader.GetInt16(1), reader.GetInt32(2)));
            }
        }

        var sites = new List<BuildSite>();
        foreach (var (id, owner) in owners)
        {
            if (!pieces.TryGetValue(id, out var list)) continue;
            var site = new BuildSite(id, owner, list);
            if (built.TryGetValue(id, out var builtSlots))
                foreach (var slot in builtSlots) site.LoadBuilt(slot);
            if (storage.TryGetValue(id, out var lines))
                foreach (var (item, amount) in lines) site.LoadStorage(item, amount);
            sites.Add(site);
        }
        return sites;
    }

    /// <summary>
    /// Writes a freshly committed blueprint: its row and all its pieces (all
    /// pending). The in-memory <see cref="BuildSite.Id"/> is the authority, so it
    /// is inserted explicitly, keeping ids stable across a restart.
    /// </summary>
    public async Task SaveBlueprintAsync(BuildSite site)
    {
        if (_db is null) return;

        var anchor = AnchorChunk(site);
        await using (var cmd = _db.CreateCommand("""
            INSERT INTO blueprint (id, world_id, owner_id, chunk_x, chunk_y)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (id) DO NOTHING
            """))
        {
            cmd.Parameters.AddWithValue(site.Id);
            cmd.Parameters.AddWithValue(_worldId);
            cmd.Parameters.AddWithValue(site.Owner);
            cmd.Parameters.AddWithValue(anchor.X);
            cmd.Parameters.AddWithValue(anchor.Y);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var piece in site.Pieces)
        {
            var chunk = World.ChunkOf(piece.Slot.X, piece.Slot.Y);
            await using var cmd = _db.CreateCommand("""
                INSERT INTO blueprint_piece
                    (blueprint_id, x, y, level, layer, kind, material, built, chunk_x, chunk_y)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                ON CONFLICT (blueprint_id, x, y, level, layer) DO NOTHING
                """);
            cmd.Parameters.AddWithValue(site.Id);
            cmd.Parameters.AddWithValue(piece.Slot.X);
            cmd.Parameters.AddWithValue(piece.Slot.Y);
            cmd.Parameters.AddWithValue(piece.Slot.Level);
            cmd.Parameters.AddWithValue((short)piece.Slot.Layer);
            cmd.Parameters.AddWithValue((short)piece.Kind);
            cmd.Parameters.AddWithValue((short)piece.Material);
            cmd.Parameters.AddWithValue(site.IsBuilt(piece.Slot));
            cmd.Parameters.AddWithValue(chunk.X);
            cmd.Parameters.AddWithValue(chunk.Y);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Marks one piece built. Called on the strike that completes it.</summary>
    public async Task SavePieceBuiltAsync(long blueprintId, PieceSlot slot)
    {
        if (_db is null) return;

        await using var cmd = _db.CreateCommand("""
            UPDATE blueprint_piece SET built = true
            WHERE blueprint_id = $1 AND x = $2 AND y = $3 AND level = $4 AND layer = $5
            """);
        cmd.Parameters.AddWithValue(blueprintId);
        cmd.Parameters.AddWithValue(slot.X);
        cmd.Parameters.AddWithValue(slot.Y);
        cmd.Parameters.AddWithValue(slot.Level);
        cmd.Parameters.AddWithValue((short)slot.Layer);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Upserts one stockpile line, deleting it when the amount reaches zero.</summary>
    public async Task SaveStorageAsync(long blueprintId, ItemId item, int amount)
    {
        if (_db is null) return;

        if (amount <= 0)
        {
            await using var del = _db.CreateCommand(
                "DELETE FROM build_site_storage WHERE blueprint_id = $1 AND item = $2");
            del.Parameters.AddWithValue(blueprintId);
            del.Parameters.AddWithValue((short)item);
            await del.ExecuteNonQueryAsync();
            return;
        }

        await using var cmd = _db.CreateCommand("""
            INSERT INTO build_site_storage (blueprint_id, item, amount)
            VALUES ($1, $2, $3)
            ON CONFLICT (blueprint_id, item) DO UPDATE SET amount = EXCLUDED.amount
            """);
        cmd.Parameters.AddWithValue(blueprintId);
        cmd.Parameters.AddWithValue((short)item);
        cmd.Parameters.AddWithValue(amount);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Deletes a build site and everything under it (cascades to pieces and storage).</summary>
    public async Task DeleteBlueprintAsync(long blueprintId)
    {
        if (_db is null) return;

        await using var cmd = _db.CreateCommand("DELETE FROM blueprint WHERE id = $1");
        cmd.Parameters.AddWithValue(blueprintId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ChunkCoord AnchorChunk(BuildSite site)
    {
        foreach (var piece in site.Pieces)
            return World.ChunkOf(piece.Slot.X, piece.Slot.Y);
        return new ChunkCoord(0, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }
}
