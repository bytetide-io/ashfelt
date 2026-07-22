using System.Collections.Generic;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Phase 1 entry point: predicted movement, tap-to-chop, and rendering of the
/// authoritative world. The server owns all state; prediction here is purely
/// cosmetic and is corrected by every snapshot.
/// </summary>
public partial class Main : Node2D
{
    /// <summary>How hard a server correction pulls the predicted position back.</summary>
    private const float ReconcileRate = 6f;

    /// <summary>Beyond this error, in tiles, we snap instead of easing.</summary>
    private const float SnapDistanceTiles = 3f;

    private WorldConnection _connection = null!;
    private WorldView _world = null!;
    private TouchInput _input = null!;
    private Camera2D _camera = null!;
    private Label _hud = null!;
    private Label _status = null!;

    private int _playerId = -1;
    private Vector2 _predicted;
    private Vector2 _authoritative;
    private bool _spawned;
    private Vector2 _lastSentIntent = Vector2.Inf;
    private readonly Dictionary<ItemId, int> _inventory = new();
    private readonly HashSet<ChunkCoord> _requested = new();

    public override void _Ready()
    {
        _connection = GetNode<WorldConnection>("WorldConnection");
        _world = GetNode<WorldView>("WorldView");
        _input = GetNode<TouchInput>("TouchInput");
        _camera = GetNode<Camera2D>("Camera2D");
        _hud = GetNode<Label>("Hud/Inventory");
        _status = GetNode<Label>("Hud/Status");

        _connection.Welcomed += (seed, _, playerId, x, y) =>
            CallDeferred(nameof(OnWelcomed), seed, playerId, x, y);
        _connection.ChunkReceived += (coord, tiles) =>
            CallDeferred(nameof(OnChunk), coord.X, coord.Y, System.Array.ConvertAll(tiles, t => (byte)t));
        _connection.TileChanged += (x, y, tile) =>
            CallDeferred(nameof(OnTileChanged), x, y, (byte)tile);
        _connection.PlayersUpdated += OnPlayers;
        _connection.PlayerLeft += id => CallDeferred(nameof(OnPlayerLeft), id);
        _connection.InventoryUpdated += OnInventory;
        _connection.StatusChanged += status => CallDeferred(nameof(ShowStatus), status);
        ShowStatus(_connection.Status);
    }

    private void OnWelcomed(uint seed, int playerId, float x, float y)
    {
        _playerId = playerId;
        _world.Seed = seed;
        _predicted = _authoritative = new Vector2(x, y);
        _world.Entities.LocalId = playerId;
        _spawned = true;
        RequestChunksAround(_predicted);
    }

    private void OnChunk(int cx, int cy, byte[] tiles)
    {
        _world.SetChunk(new ChunkCoord(cx, cy),
            System.Array.ConvertAll(tiles, b => (TileType)b));
    }

    private void OnTileChanged(int x, int y, byte tile) => _world.SetTile(x, y, (TileType)tile);

    private void OnPlayerLeft(int id) => _world.Entities.RemovePlayer(id);

    private void OnPlayers(IReadOnlyList<PlayerState> states)
    {
        foreach (var state in states)
            if (state.Id == _playerId)
                _authoritative = new Vector2(state.X, state.Y);

        _world.Entities.UpdatePlayers(states);
    }

    private void OnInventory(IReadOnlyDictionary<ItemId, int> inventory)
    {
        _inventory.Clear();
        foreach (var (item, amount) in inventory) _inventory[item] = amount;
        CallDeferred(nameof(RefreshHud));
    }

    /// <summary>
    /// An empty world renders as a blank screen, which is indistinguishable
    /// from a broken client. Say what is actually happening instead.
    /// </summary>
    private void ShowStatus(string status)
    {
        _status.Text = status;
        _status.Visible = !string.IsNullOrEmpty(status);
    }

    private void RefreshHud()
    {
        if (_inventory.Count == 0) { _hud.Text = "Inventory: empty"; return; }

        var parts = new List<string>();
        foreach (var (item, amount) in _inventory) parts.Add($"{item} x{amount}");
        _hud.Text = "Inventory: " + string.Join("  ", parts);
    }

    public override void _Process(double delta)
    {
        if (!_spawned) return;

        var direction = _input.Direction;
        if (!direction.IsEqualApprox(_lastSentIntent))
        {
            _connection.SendMoveIntent(direction);
            _lastSentIntent = direction;
        }

        Predict(direction, (float)delta);
        Reconcile((float)delta);

        _world.Entities.SetLocalPosition(_predicted);
        _camera.Position = _predicted * WorldView.TilePixels;

        if (_input.ConsumeTap() is { } tap) TryChopAt(tap);

        RequestChunksAround(_predicted);
    }

    /// <summary>
    /// Runs the same movement rule the server uses, against the tiles we have.
    /// This only hides latency — the server's result always wins.
    /// </summary>
    private void Predict(Vector2 direction, float delta)
    {
        if (direction == Vector2.Zero) return;

        float step = Tuning.MoveTilesPerSecond * delta;
        var next = _predicted + direction.LimitLength(1f) * step;

        if (IsWalkable(next.X, _predicted.Y)) _predicted.X = next.X;
        if (IsWalkable(_predicted.X, next.Y)) _predicted.Y = next.Y;
    }

    private void Reconcile(float delta)
    {
        float error = _predicted.DistanceTo(_authoritative);
        if (error > SnapDistanceTiles) _predicted = _authoritative;
        else if (error > 0.01f)
            _predicted = _predicted.Lerp(_authoritative, Mathf.Min(1f, ReconcileRate * delta));
    }

    private bool IsWalkable(float x, float y)
    {
        var tile = _world.TileAt(Mathf.FloorToInt(x), Mathf.FloorToInt(y));
        // Unknown terrain is treated as walkable: the server will correct us
        // rather than the player being stuck at an unloaded chunk edge.
        return tile is null || TerrainGenerator.IsWalkable(tile.Value);
    }

    private void TryChopAt(Vector2 screenPosition)
    {
        var world = _camera.GetCanvasTransform().AffineInverse() * screenPosition;
        int tx = Mathf.FloorToInt(world.X / WorldView.TilePixels);
        int ty = Mathf.FloorToInt(world.Y / WorldView.TilePixels);

        var tile = _world.TileAt(tx, ty);
        if (tile is null || !HarvestRules.IsHarvestable(tile.Value)) return;

        // Range is enforced server-side; checking here avoids a pointless packet.
        if (_predicted.DistanceTo(new Vector2(tx + 0.5f, ty + 0.5f)) > Tuning.ChopRangeTiles) return;

        _connection.SendChop(tx, ty);
    }

    private void RequestChunksAround(Vector2 position)
    {
        var centre = World.ChunkOf(Mathf.FloorToInt(position.X), Mathf.FloorToInt(position.Y));
        int r = Tuning.InterestRadiusChunks;

        for (int cy = centre.Y - r; cy <= centre.Y + r; cy++)
        {
            for (int cx = centre.X - r; cx <= centre.X + r; cx++)
            {
                var coord = new ChunkCoord(cx, cy);
                if (_world.HasChunk(coord) || !_requested.Add(coord)) continue;
                _connection.RequestChunk(coord);
            }
        }
    }
}
