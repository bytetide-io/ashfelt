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
    /// Claims and fetches a character for <paramref name="worldId"/>. The claim
    /// is atomic at the gateway: it fails with <see cref="CharacterLoadOutcome.OwnedElsewhere"/>
    /// when a different world-server currently owns the character, so the same
    /// character can never be loaded (and independently mutated) by two
    /// world-servers at once.
    /// </summary>
    public async Task<CharacterLoadResult> GetCharacterAsync(Guid id, string worldId)
    {
        var response = await _http.GetAsync($"/characters/{id}?worldId={Uri.EscapeDataString(worldId)}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new CharacterLoadResult(CharacterLoadOutcome.New, null);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return new CharacterLoadResult(CharacterLoadOutcome.OwnedElsewhere, null);

        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<CharacterState>();
        return new CharacterLoadResult(CharacterLoadOutcome.Loaded, state);
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

public enum CharacterLoadOutcome { New, Loaded, OwnedElsewhere }

/// <summary>Result of a character claim-and-load attempt. See <see cref="CharacterLoadOutcome"/>.</summary>
public sealed record CharacterLoadResult(CharacterLoadOutcome Outcome, CharacterState? State);
