// Gateway: accounts, character storage, and routing between world-servers.
// Phase 3a adds device-UUID character persistence: a character (inventory +
// survival meters) lives here in the gateway DB, keyed by a UUID the client
// generates and stores locally. World-servers load a character on join and
// save it on leave over the REST endpoints below.

using Ashfall.Proto;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Mirror the world-server: the DB connection string comes from ASHFALL_DB.
// The gateway owns character state, so persistence is required — failing loudly
// here beats silently dropping every character on save.
string conn = Environment.GetEnvironmentVariable("ASHFALL_DB")
    ?? builder.Configuration.GetConnectionString("Ashfall")
    ?? throw new InvalidOperationException(
        "ASHFALL_DB is not set — the gateway requires a database for character storage.");

// EnableDynamicJson lets Npgsql read/write the JSONB inventory column directly
// as a Dictionary<string,int> via System.Text.Json.
builder.Services.AddNpgsqlDataSource(conn, b => b.EnableDynamicJson());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", proto = ProtocolVersion.Current }));

// World registry. Static for now (Phase 3+ replaces this with live world-server
// registration); a voyage target must exist here to be mintable. Keyed by id so
// both /worlds and the voyage endpoints resolve an address from one table.
var worlds = new Dictionary<string, WorldEntry>
{
    ["continent-a"] = new("continent-a", "127.0.0.1", 9050),
    ["continent-b"] = new("continent-b", "127.0.0.1", 9051),
};

app.MapGet("/worlds", () => Results.Ok(worlds.Values));

// How long a minted voyage ticket stays claimable. Short: a voyage is instant,
// so a ticket only has to survive one disconnect-and-reconnect. Past this the
// ticket self-heals — ownership returns to the world the character left.
const int TicketTtlSeconds = 60;

// Character load: returns the stored inventory + survival meters, or 404 when
// the device UUID has never saved a character.
app.MapGet("/characters/{id:guid}", async (Guid id, NpgsqlDataSource db) =>
{
    // Self-heal a stranded voyage: if this character has a ticket that expired
    // unclaimed, ownership returns to the world it left before we hand the state
    // back. This is what makes a crash mid-transfer recover without an operator —
    // the player simply reconnects and is loaded as if the voyage never began.
    await ReclaimExpiredTicketAsync(db, id);

    await using var cmd = db.CreateCommand(
        "SELECT inventory, hunger, stamina, health FROM character WHERE id = $1");
    cmd.Parameters.AddWithValue(id);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound();

    var character = new CharacterState
    {
        Inventory = reader.GetFieldValue<Dictionary<string, int>>(0),
        Hunger = reader.GetInt32(1),
        Stamina = reader.GetInt32(2),
        Health = reader.GetInt32(3),
    };
    return Results.Ok(character);
});

// Character save: upsert the character, creating the row on first save.
app.MapPut("/characters/{id:guid}", async (Guid id, CharacterState character, NpgsqlDataSource db) =>
{
    await using var cmd = db.CreateCommand("""
        INSERT INTO character (id, inventory, hunger, stamina, health, updated_at)
        VALUES ($1, $2, $3, $4, $5, now())
        ON CONFLICT (id) DO UPDATE SET
            inventory = EXCLUDED.inventory,
            hunger = EXCLUDED.hunger,
            stamina = EXCLUDED.stamina,
            health = EXCLUDED.health,
            updated_at = now()
        """);
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.Add(new NpgsqlParameter
    {
        Value = character.Inventory,
        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
    });
    cmd.Parameters.AddWithValue(character.Hunger);
    cmd.Parameters.AddWithValue(character.Stamina);
    cmd.Parameters.AddWithValue(character.Health);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok();
});

// Voyage mint: world-server A (the trusted caller — never the client) marks the
// character in-transit toward a target world and gets back a single-use ticket
// plus the target's address. A calls this during RequestRelease, after it has
// already saved the authoritative character state. Ownership is cleared to NULL
// here: from this moment no world owns the live character.
app.MapPost("/voyage", async (VoyageRequest req, NpgsqlDataSource db) =>
{
    if (!worlds.TryGetValue(req.TargetWorldId, out var target))
        return Results.NotFound(new { error = $"unknown target world '{req.TargetWorldId}'" });

    var ticket = Guid.NewGuid();

    // ON CONFLICT lets a fresh voyage overwrite a stale (expired, unclaimed)
    // ticket for the same character; a live voyage is simply re-minted.
    await using var mint = db.CreateCommand("""
        WITH released AS (
            UPDATE character SET owner_world_id = NULL WHERE id = $1 RETURNING id
        )
        INSERT INTO voyage_ticket (character_id, ticket, from_world_id, target_world_id, expires_at)
        SELECT $1, $2, $3, $4, now() + ($5 || ' seconds')::interval FROM released
        ON CONFLICT (character_id) DO UPDATE SET
            ticket = EXCLUDED.ticket,
            from_world_id = EXCLUDED.from_world_id,
            target_world_id = EXCLUDED.target_world_id,
            expires_at = EXCLUDED.expires_at,
            created_at = now()
        """);
    mint.Parameters.AddWithValue(req.CharacterId);
    mint.Parameters.AddWithValue(ticket);
    mint.Parameters.AddWithValue(req.FromWorldId);
    mint.Parameters.AddWithValue(req.TargetWorldId);
    mint.Parameters.AddWithValue(TicketTtlSeconds.ToString());
    int rows = await mint.ExecuteNonQueryAsync();

    // No CTE row means the character has never been saved — A must PUT it first.
    if (rows == 0)
        return Results.NotFound(new { error = $"no character '{req.CharacterId}' to release" });

    return Results.Ok(new { targetHost = target.Host, targetPort = target.Port, ticket });
});

// Voyage claim: world-server B validates and consumes the ticket the arriving
// client presented, taking ownership. Deleting the row makes the ticket
// single-use, so a replayed claim finds nothing and 409s — never duplicating.
app.MapPost("/voyage/claim", async (VoyageClaim req, NpgsqlDataSource db) =>
{
    await using var read = db.CreateCommand(
        "SELECT ticket, expires_at FROM voyage_ticket WHERE character_id = $1");
    read.Parameters.AddWithValue(req.CharacterId);

    Guid ticket;
    DateTime expiresAt;
    await using (var reader = await read.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync())
            return Results.Conflict(new { error = "no ticket — never issued, or already claimed" });
        ticket = reader.GetGuid(0);
        expiresAt = reader.GetDateTime(1);
    }

    if (ticket != req.Ticket)
        return Results.Conflict(new { error = "ticket mismatch" });

    if (expiresAt <= DateTime.UtcNow)
    {
        // Expired: hand the character back to the world it left, then report gone.
        await ReclaimExpiredTicketAsync(db, req.CharacterId);
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    await using var claim = db.CreateCommand("""
        WITH consumed AS (
            DELETE FROM voyage_ticket WHERE character_id = $1 RETURNING character_id
        )
        UPDATE character SET owner_world_id = $2
        WHERE id = (SELECT character_id FROM consumed)
        """);
    claim.Parameters.AddWithValue(req.CharacterId);
    claim.Parameters.AddWithValue(req.WorldId);
    int rows = await claim.ExecuteNonQueryAsync();

    // Lost the race to a concurrent claim that already consumed the row.
    if (rows == 0)
        return Results.Conflict(new { error = "ticket already claimed" });

    return Results.Ok();
});

app.Run();

// Returns a character's ownership to the world it left when its ticket has
// expired unclaimed, and drops the stale ticket. A no-op when nothing expired.
static async Task ReclaimExpiredTicketAsync(NpgsqlDataSource db, Guid characterId)
{
    await using var cmd = db.CreateCommand("""
        WITH expired AS (
            DELETE FROM voyage_ticket
            WHERE character_id = $1 AND expires_at <= now()
            RETURNING character_id, from_world_id
        )
        UPDATE character SET owner_world_id = expired.from_world_id
        FROM expired WHERE character.id = expired.character_id
        """);
    cmd.Parameters.AddWithValue(characterId);
    await cmd.ExecuteNonQueryAsync();
}

record WorldEntry(string Id, string Host, int Port);
record VoyageRequest(Guid CharacterId, string FromWorldId, string TargetWorldId);
record VoyageClaim(Guid CharacterId, Guid Ticket, string WorldId);
