using System.Diagnostics;
using Ashfall.WorldServer;

namespace Ashfall.WorldServer.Tests;

public class PendingSaveTrackerTests
{
    [Fact]
    public void WaitFor_WithNothingTracked_ReturnsImmediately()
    {
        var tracker = new PendingSaveTracker();

        Assert.True(Time(() => tracker.WaitFor(Guid.NewGuid())) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitFor_BlocksUntilTheTrackedSaveCompletes()
    {
        var tracker = new PendingSaveTracker();
        var characterId = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        tracker.Track(characterId, gate.Task);

        var waiter = Task.Run(() => tracker.WaitFor(characterId));
        await Task.Delay(50);
        Assert.False(waiter.IsCompleted);

        gate.SetResult();
        var finished = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(waiter, finished);
    }

    [Fact]
    public async Task WaitFor_IsANoOpOnceTheTrackedSaveHasCompleted()
    {
        var tracker = new PendingSaveTracker();
        var characterId = Guid.NewGuid();
        tracker.Track(characterId, Task.CompletedTask);

        // The completion continuation that removes the entry races this
        // assertion — give it a moment to run first.
        await Task.Delay(50);

        Assert.True(Time(() => tracker.WaitFor(characterId)) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void WaitFor_OnlyBlocksForItsOwnCharacter()
    {
        var tracker = new PendingSaveTracker();
        tracker.Track(Guid.NewGuid(), new TaskCompletionSource().Task);

        Assert.True(Time(() => tracker.WaitFor(Guid.NewGuid())) < TimeSpan.FromSeconds(1));
    }

    private static TimeSpan Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed;
    }
}
