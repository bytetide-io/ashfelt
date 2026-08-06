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
    // The server now only broadcasts players within its interest radius
    // (see Tuning.InterestRadiusMetres), so a remote player legitimately stops
    // appearing in snapshots when they walk out of range, not just when they
    // disconnect. PlayerLeft removes a disconnect immediately; this timeout is
    // the fallback for an interest-range exit (and for a dropped PlayerLeft).
    private const float StaleTimeoutSeconds = 1.5f;

    private readonly Dictionary<int, Node3D> _bodies = new();
    private readonly Dictionary<int, Vector3> _targets = new();
    private readonly Dictionary<int, float> _yaws = new();
    private readonly Dictionary<int, ulong> _lastSeenMsec = new();

    public void Apply(IReadOnlyList<PlayerState> states, int localId)
    {
        ulong now = Time.GetTicksMsec();
        foreach (var state in states)
        {
            if (state.Id == localId) continue;
            _targets[state.Id] = state.Position;
            _yaws[state.Id] = state.Yaw;
            _lastSeenMsec[state.Id] = now;
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
        _lastSeenMsec.Remove(id);
        if (_bodies.Remove(id, out var body)) body.QueueFree();
    }

    /// <summary>Drop everyone — used when a voyage leaves one world for another.</summary>
    public void Clear()
    {
        foreach (var body in _bodies.Values) body.QueueFree();
        _bodies.Clear();
        _targets.Clear();
        _yaws.Clear();
        _lastSeenMsec.Clear();
    }

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksMsec();
        ulong timeoutMsec = (ulong)(StaleTimeoutSeconds * 1000);
        foreach (var id in new List<int>(_bodies.Keys))
        {
            if (now - _lastSeenMsec.GetValueOrDefault(id) > timeoutMsec)
            {
                Remove(id);
            }
        }

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
