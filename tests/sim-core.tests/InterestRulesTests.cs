using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// This is the entire filter behind invariant #5 (interest management), so
/// each boundary gets a test: exactly at the radius is in range, one chunk
/// past it is not, and the check is symmetric regardless of which side calls
/// it the "viewer".
/// </summary>
public class InterestRulesTests
{
    [Fact]
    public void SameChunk_IsInRange()
    {
        var chunk = new ChunkCoord(4, -2);
        Assert.True(InterestRules.InRange(chunk, chunk, radiusChunks: 1));
    }

    [Fact]
    public void ExactlyAtRadius_IsInRange()
    {
        var viewer = new ChunkCoord(0, 0);
        var subject = new ChunkCoord(2, -2);
        Assert.True(InterestRules.InRange(viewer, subject, radiusChunks: 2));
    }

    [Fact]
    public void OneChunkPastRadius_IsOutOfRange()
    {
        var viewer = new ChunkCoord(0, 0);
        var subject = new ChunkCoord(3, 0);
        Assert.False(InterestRules.InRange(viewer, subject, radiusChunks: 2));
    }

    [Fact]
    public void FarDiagonal_IsOutOfRange()
    {
        var viewer = new ChunkCoord(10, 10);
        var subject = new ChunkCoord(12, 12);
        Assert.False(InterestRules.InRange(viewer, subject, radiusChunks: 1));
    }

    [Fact]
    public void IsSymmetric()
    {
        var a = new ChunkCoord(5, 5);
        var b = new ChunkCoord(6, 4);
        Assert.Equal(
            InterestRules.InRange(a, b, radiusChunks: 1),
            InterestRules.InRange(b, a, radiusChunks: 1));
    }
}
