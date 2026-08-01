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

    /// <param name="baseUrl">The gateway's base URL.</param>
    /// <param name="key">
    /// Shared secret proving this caller is a trusted world-server, not the
    /// client — the gateway rejects /characters and /voyage* requests without
    /// it. Must match the gateway's ASHFALL_GATEWAY_KEY.
    /// </param>
    public GatewayClient(string baseUrl, string key)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Add("X-Ashfall-Key", key);
    }

    /// <summary>
    /// Atomically takes ownership of a character for <paramref name="worldId"/>
    /// and returns its stored state (freshly defaulted if this UUID has never
    /// saved before). Returns null when another world already owns the
    /// character — the caller must reject the join rather than load a second,
    /// independent copy of the same inventory (see gateway Program.cs).
    /// </summary>
    public async Task<CharacterState?> ClaimCharacterAsync(Guid id, string worldId)
    {
        var response = await _http.PostAsJsonAsync($"/characters/{id}/claim", new { worldId });
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
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
