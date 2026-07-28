using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// Bounds a per-recipient player-state snapshot to nearby players (architecture
/// invariant #6: a client only ever receives entities near it). Pure and
/// side-effect free so it is testable without a network harness — the
/// world-server calls it once per recipient per tick, then diffs the result
/// against what it sent last tick to know who dropped out of range.
/// </summary>
public static class InterestRules
{
    /// <summary>
    /// Horizontal distance, in metres, inside which one player's state is sent
    /// to another. Sized off the shared-proto chunk interest radius so player
    /// and chunk interest stay in the same ballpark.
    /// </summary>
    public const double PlayerRadiusMetres =
        (Tuning.InterestRadiusChunks + 1) * TerrainGenerator.ChunkSize * TerrainGenerator.TileMetres;

    /// <summary>Horizontal distance only, mirroring harvest reach and warmth radius.</summary>
    public static bool IsVisible(Vec3 viewer, Vec3 subject) =>
        viewer.HorizontalDistanceTo(subject) <= PlayerRadiusMetres;

    /// <summary>IDs of <paramref name="others"/> within <see cref="PlayerRadiusMetres"/> of <paramref name="viewerPosition"/>.</summary>
    public static HashSet<int> VisibleIds(Vec3 viewerPosition, IEnumerable<(int Id, Vec3 Position)> others)
    {
        var visible = new HashSet<int>();
        foreach (var (id, position) in others)
            if (IsVisible(viewerPosition, position))
                visible.Add(id);
        return visible;
    }

    /// <summary>
    /// IDs that were visible last tick but are not visible this tick — the
    /// recipient must be told these left, or their avatar freezes in place
    /// instead of despawning.
    /// </summary>
    public static IEnumerable<int> NewlyOutOfRange(IReadOnlySet<int> previouslyVisible, IReadOnlySet<int> currentlyVisible) =>
        previouslyVisible.Where(id => !currentlyVisible.Contains(id));
}
