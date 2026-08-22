using System;
using System.Collections.Generic;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Renders committed build sites in the world. The owner receives whole
/// blueprints (<see cref="WorldConnection.BlueprintReceived"/>) and sees pending
/// pieces as translucent holograms and built pieces solid; everyone else receives
/// only completed pieces (<see cref="WorldConnection.BuildProgressed"/>) and so
/// watches a building rise out of nothing.
///
/// Scene mutations are queued off the receive callback and drained in
/// <see cref="_Process"/>, so nodes are only added at a safe point in the frame —
/// the same reason the world defers its own structure spawns.
/// </summary>
public partial class BlueprintView : Node3D
{
    private TerrainGenerator _terrain = null!;
    private readonly Dictionary<(long Site, PieceSlot Slot), Node3D> _nodes = new();
    private readonly Dictionary<long, List<PieceSlot>> _bySite = new();
    private readonly List<Action> _pending = new();

    /// <summary>
    /// Binds the terrain used to position pieces. The world owns the connection
    /// subscription and forwards events here, because the connection outlives a
    /// world rebuild (a voyage) while this view does not — a self-subscription
    /// would leak onto a freed node.
    /// </summary>
    public void Bind(TerrainGenerator terrain) => _terrain = terrain;

    public void QueueBlueprint(long siteId, IReadOnlyList<BlueprintPieceView> pieces) =>
        Enqueue(() => ApplyBlueprint(siteId, pieces));

    public void QueueProgress(long siteId, PlannedPiece piece, bool completed) =>
        Enqueue(() => ApplyProgress(siteId, piece, completed));

    public void QueueRemove(long siteId) => Enqueue(() => ClearSite(siteId));

    private void Enqueue(Action action)
    {
        lock (_pending) _pending.Add(action);
    }

    public override void _Process(double delta)
    {
        if (_pending.Count == 0) return;
        Action[] batch;
        lock (_pending)
        {
            batch = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var action in batch) action();
    }

    /// <summary>The owner's authoritative view: rebuild the whole site from scratch,
    /// pending pieces as ghosts and built pieces solid.</summary>
    private void ApplyBlueprint(long siteId, IReadOnlyList<BlueprintPieceView> pieces)
    {
        ClearSite(siteId);
        foreach (var view in pieces)
            Place(siteId, view.Piece, ghost: !view.Built);
    }

    /// <summary>A completed strike: the piece now stands for everyone. Partial strikes
    /// are ignored here — the owner already sees the pending ghost.</summary>
    private void ApplyProgress(long siteId, PlannedPiece piece, bool completed)
    {
        if (completed) Place(siteId, piece, ghost: false);
    }

    private void Place(long siteId, PlannedPiece piece, bool ghost)
    {
        var key = (siteId, piece.Slot);
        if (_nodes.TryGetValue(key, out var existing)) existing.QueueFree();

        var node = BlueprintPieces3D.Build(piece, _terrain, ghost);
        AddChild(node);
        _nodes[key] = node;

        if (!_bySite.TryGetValue(siteId, out var slots)) _bySite[siteId] = slots = new List<PieceSlot>();
        if (!slots.Contains(piece.Slot)) slots.Add(piece.Slot);
    }

    private void ClearSite(long siteId)
    {
        if (!_bySite.TryGetValue(siteId, out var slots)) return;
        foreach (var slot in slots)
            if (_nodes.Remove((siteId, slot), out var node)) node.QueueFree();
        _bySite.Remove(siteId);
    }

    /// <summary>Drops every rendered site — used when a voyage rebuilds the world.</summary>
    public void ClearAll()
    {
        foreach (var node in _nodes.Values) node.QueueFree();
        _nodes.Clear();
        _bySite.Clear();
        lock (_pending) _pending.Clear();
    }
}
