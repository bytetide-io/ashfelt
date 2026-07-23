namespace Ashfall.Proto;

/// <summary>
/// The persistent, world-independent part of a player, exchanged as JSON between
/// the gateway (which stores it) and a world-server (which loads it on join and
/// saves it on leave). Skills are out of scope for Phase 3a.
///
/// Inventory is keyed by the stable <see cref="ItemId"/> enum *name*, so a wire
/// renumber never corrupts a stored stack. The meters are display points
/// (0..100), the same integers the protocol puts on the wire.
/// </summary>
public sealed record CharacterState
{
    public Dictionary<string, int> Inventory { get; init; } = new();
    public int Hunger { get; init; } = 100;
    public int Stamina { get; init; } = 100;
    public int Health { get; init; } = 100;
}
