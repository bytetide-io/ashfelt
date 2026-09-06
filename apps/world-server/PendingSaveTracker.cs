using System.Collections.Concurrent;

namespace Ashfall.WorldServer;

/// <summary>
/// Tracks the fire-and-forget character save started when a player disconnects
/// (see <c>PeerDisconnectedEvent</c> in Program.cs), so a fast reconnect can wait
/// for its own write to land before loading — instead of racing it: reading the
/// stale row underneath the in-flight save, then clobbering that save with a
/// disconnect snapshot taken from the stale state when the new session ends.
/// </summary>
public sealed class PendingSaveTracker
{
    private readonly ConcurrentDictionary<Guid, Task> _saves = new();

    /// <summary>
    /// Records <paramref name="save"/> as in flight for <paramref name="characterId"/>.
    /// Removed automatically once it completes, success or failure.
    /// </summary>
    public void Track(Guid characterId, Task save)
    {
        _saves[characterId] = save;
        save.ContinueWith(
            _ => _saves.TryRemove(new KeyValuePair<Guid, Task>(characterId, save)),
            TaskScheduler.Default);
    }

    /// <summary>
    /// Blocks until any save in flight for <paramref name="characterId"/> completes.
    /// A no-op when none is pending — the common case of a normal join.
    /// </summary>
    public void WaitFor(Guid characterId)
    {
        if (_saves.TryGetValue(characterId, out var pending))
            pending.GetAwaiter().GetResult();
    }
}
