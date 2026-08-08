using System.Net;
using System.Net.Http.Json;
using Ashfall.Proto;

namespace Ashfall.WorldServer;

/// <summary>
/// Talks to the gateway's character REST API. The world-server holds world state
/// only (invariant #3): a joining player's inventory and survival meters are
/// loaded from here, and saved back here when they leave.
/// </summary>
public sealed class GatewayClient
{
    private readonly HttpClient _http;

    public GatewayClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    /// <summary>
    /// Fetches a character, or null when the gateway has none yet (a fresh
    /// device UUID) — the caller then starts the player empty and full.
    /// </summary>
    public async Task<CharacterState?> GetCharacterAsync(Guid id)
    {
        var response = await _http.GetAsync($"/characters/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CharacterState>();
    }

    /// <summary>Upserts a character, creating it on first save.</summary>
    public async Task SaveCharacterAsync(Guid id, CharacterState character)
    {
        var response = await _http.PutAsJsonAsync($"/characters/{id}", character);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Claims ownership of a character for a normal (non-voyage) join. True when
    /// no other world currently owns it, or this world already does; false when
    /// another world holds it live — the caller must then reject the join, since
    /// the same character must never be loaded by two world-servers at once.
    /// </summary>
    public async Task<bool> ClaimCharacterAsync(Guid characterId, string worldId)
    {
        var response = await _http.PostAsJsonAsync($"/characters/{characterId}/claim",
            new { worldId });
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Marks a character in-transit toward <paramref name="targetWorldId"/> and
    /// mints a single-use ticket. Returns the target address plus the ticket, or
    /// null when the target world is unknown or the mint failed — in which case
    /// the caller must deny the release and keep the player.
    /// </summary>
    public async Task<VoyageGrant?> RequestVoyageAsync(Guid characterId, string fromWorldId, string targetWorldId)
    {
        var response = await _http.PostAsJsonAsync("/voyage",
            new { characterId, fromWorldId, targetWorldId });
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<VoyageGrant>();
    }

    /// <summary>
    /// Validates and consumes an arriving client's voyage ticket, taking
    /// ownership of the character for this world. True on success; false when the
    /// ticket is invalid, expired or already claimed (the caller rejects the join).
    /// </summary>
    public async Task<bool> ClaimVoyageAsync(Guid characterId, Guid ticket, string worldId)
    {
        var response = await _http.PostAsJsonAsync("/voyage/claim",
            new { characterId, ticket, worldId });
        return response.IsSuccessStatusCode;
    }
}

/// <summary>Where a granted voyage sends the client, plus the ticket it carries.</summary>
public sealed record VoyageGrant
{
    public string TargetHost { get; init; } = "";
    public int TargetPort { get; init; }
    public Guid Ticket { get; init; }
}
