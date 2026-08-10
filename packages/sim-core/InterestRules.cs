namespace Ashfall.SimCore;

/// <summary>
/// Bounds which players a world-server event reaches — invariant #5 in
/// docs/architecture.md: "a client only ever receives entities/chunks near it."
/// Pure and shared so the world-server's netcode and any future entity system
/// (docs/gameplay-roadmap.md §3.3) apply the identical rule.
///
/// The area is a square of chunks around the viewer (matching how the client
/// already streams terrain by <c>Radius</c> in <c>World3D.Build</c>), not a
/// circle — cheaper to test and to reason about, and chunk edges are already
/// the unit interest is measured in.
/// </summary>
public static class InterestRules
{
    public static bool InRange(ChunkCoord viewer, ChunkCoord subject, int radiusChunks) =>
        Math.Abs(viewer.X - subject.X) <= radiusChunks &&
        Math.Abs(viewer.Y - subject.Y) <= radiusChunks;
}
