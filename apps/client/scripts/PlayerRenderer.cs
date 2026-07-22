using System.Collections.Generic;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Placeholder player rendering: the local player plus every remote player the
/// server has told us about. Remote players are smoothed toward their last
/// authoritative position rather than snapping each snapshot.
/// </summary>
public partial class PlayerRenderer : Node2D
{
    private static readonly Color LocalColour = new("f2e8c9");
    private static readonly Color RemoteColour = new("c96f4a");

    private readonly Dictionary<int, Vector2> _target = new();
    private readonly Dictionary<int, Vector2> _shown = new();

    public int LocalId { get; set; } = -1;
    public Vector2 LocalPosition { get; set; }

    public void ApplySnapshot(IReadOnlyList<PlayerState> states)
    {
        foreach (var state in states)
        {
            if (state.Id == LocalId) continue;
            var pos = new Vector2(state.X, state.Y);
            _target[state.Id] = pos;
            if (!_shown.ContainsKey(state.Id)) _shown[state.Id] = pos;
        }
    }

    public void Remove(int id)
    {
        _target.Remove(id);
        _shown.Remove(id);
    }

    public override void _Process(double delta)
    {
        foreach (var id in new List<int>(_shown.Keys))
            if (_target.TryGetValue(id, out var target))
                _shown[id] = _shown[id].Lerp(target, (float)Mathf.Min(1.0, delta * 12.0));

        QueueRedraw();
    }

    public override void _Draw()
    {
        const float radius = ChunkRenderer.TilePixels * 0.4f;
        foreach (var pos in _shown.Values)
            DrawCircle(pos * ChunkRenderer.TilePixels, radius, RemoteColour);

        if (LocalId >= 0)
            DrawCircle(LocalPosition * ChunkRenderer.TilePixels, radius, LocalColour);
    }
}
