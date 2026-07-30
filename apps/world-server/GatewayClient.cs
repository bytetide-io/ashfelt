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
    /// Fetches a character while claiming ownership for <paramref name="worldId"/>
    /// (this world-server's own id). A character is owned by exactly one world at
    /// a time, so this can come back three ways: freshly claimed or already ours
    /// (<see cref="CharacterLoadStatus.Loaded"/>), never saved before
    /// (<see cref="CharacterLoadStatus.New"/> — the caller starts the player empty
    /// and full), or currently owned by a different world
    /// (<see cref="CharacterLoadStatus.Denied"/> — the caller must reject the
    /// join rather than load a character two worlds could mutate at once).
    /// </summary>
    public async Task<CharacterLoadResult> GetCharacterAsync(Guid id, string worldId)
    {
        var response = await _http.GetAsync($"/characters/{id}?worldId={Uri.EscapeDataString(worldId)}");
        if (response.StatusCode == HttpStatusCode.NotFound) return new CharacterLoadResult(CharacterLoadStatus.New, null);
        if (response.StatusCode == HttpStatusCode.Conflict) return new CharacterLoadResult(CharacterLoadStatus.Denied, null);
        response.EnsureSuccessStatusCode();
        var character = await response.Content.ReadFromJsonAsync<CharacterState>();
        return new CharacterLoadResult(CharacterLoadStatus.Loaded, character);
    }

    /// <summary>
    /// Upserts a character, creating it on first save. <paramref name="worldId"/>
    /// releases this world's ownership claim as part of the same write, so a
    /// clean leave (or a superseded duplicate connection) never leaves the
    /// character permanently locked to a world nobody is playing on.
    /// </summary>
    public async Task SaveCharacterAsync(Guid id, CharacterState character, string worldId)
    {
        var response = await _http.PutAsJsonAsync($"/characters/{id}?worldId={Uri.EscapeDataString(worldId)}", character);
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

public enum CharacterLoadStatus { New, Loaded, Denied }

/// <summary>Outcome of a claim-and-load attempt; see <see cref="GatewayClient.GetCharacterAsync"/>.</summary>
public readonly record struct CharacterLoadResult(CharacterLoadStatus Status, CharacterState? Character);
