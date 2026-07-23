using System.Collections.Generic;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Other players, drawn from the server's authoritative snapshots. Positions
/// are eased rather than snapped, because snapshots arrive at 15 Hz while the
/// game renders far faster.
///
/// Each puppet is a <see cref="CharacterRig"/> — the same body the local player
/// wears — so a remote player visibly walks and runs. Its gait is inferred from
/// how fast the eased position is actually moving; the snapshot carries no
/// velocity, and none is needed. Harvest swings are local-only until the
/// protocol carries a per-player action, so remote bodies don't yet chop.
/// </summary>
public partial class RemotePlayers : Node3D
{
    private readonly Dictionary<int, CharacterRig> _bodies = new();
    private readonly Dictionary<int, Vector3> _targets = new();
    private readonly Dictionary<int, float> _yaws = new();

    public void Apply(IReadOnlyList<PlayerState> states, int localId)
    {
        foreach (var state in states)
        {
            if (state.Id == localId) continue;
            _targets[state.Id] = state.Position;
            _yaws[state.Id] = state.Yaw;
            if (!_bodies.ContainsKey(state.Id)) CallDeferred(nameof(Spawn), state.Id, state.Position);
        }
    }

    private void Spawn(int id, Vector3 at)
    {
        if (_bodies.ContainsKey(id)) return;

        var body = new CharacterRig { Position = at };
        AddChild(body);
        _bodies[id] = body;
    }

    public void Remove(int id)
    {
        _targets.Remove(id);
        _yaws.Remove(id);
        if (_bodies.Remove(id, out var body)) body.QueueFree();
    }

    /// <summary>Drop everyone — used when a voyage leaves one world for another.</summary>
    public void Clear()
    {
        foreach (var body in _bodies.Values) body.QueueFree();
        _bodies.Clear();
        _targets.Clear();
        _yaws.Clear();
    }

    public override void _Process(double delta)
    {
        float weight = Mathf.Min(1f, (float)delta * 12f);
        float dt = Mathf.Max((float)delta, 0.0001f);
        foreach (var (id, body) in _bodies)
        {
            var before = body.Position;
            if (_targets.TryGetValue(id, out var target))
                body.Position = body.Position.Lerp(target, weight);
            if (_yaws.TryGetValue(id, out var yaw))
                body.Rotation = body.Rotation with { Y = yaw };

            var step = body.Position - before;
            float planarSpeed = new Vector2(step.X, step.Z).Length() / dt;
            body.SetLocomotion(planarSpeed, grounded: true);
        }
    }
}
