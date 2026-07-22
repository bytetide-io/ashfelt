using System.Collections.Generic;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Other players, drawn from the server's authoritative snapshots. Positions
/// are eased rather than snapped, because snapshots arrive at 15 Hz while the
/// game renders far faster.
/// </summary>
public partial class RemotePlayers : Node3D
{
    private readonly Dictionary<int, Node3D> _bodies = new();
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

        var body = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.8f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("c96f4a") },
            Position = at,
        };
        AddChild(body);
        _bodies[id] = body;
    }

    public void Remove(int id)
    {
        _targets.Remove(id);
        _yaws.Remove(id);
        if (_bodies.Remove(id, out var body)) body.QueueFree();
    }

    public override void _Process(double delta)
    {
        float weight = Mathf.Min(1f, (float)delta * 12f);
        foreach (var (id, body) in _bodies)
        {
            if (_targets.TryGetValue(id, out var target))
                body.Position = body.Position.Lerp(target, weight);
            if (_yaws.TryGetValue(id, out var yaw))
                body.Rotation = body.Rotation with { Y = yaw };
        }
    }
}
