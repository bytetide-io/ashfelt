using System.Collections.Generic;
using Godot;

namespace Ashfall.Client;

public enum PropKind { Tree, Boulder }

/// <summary>
/// Everything that stands up off the ground: trees, boulders and players.
///
/// This node has Y-sorting enabled, which is what creates the sense of depth —
/// sprites are drawn in order of their feet, so walking below a tree puts you
/// in front of it and walking above puts you behind it. Every sprite here is
/// therefore positioned at its feet, with an offset lifting the artwork up.
/// </summary>
public partial class EntityLayer : Node2D
{
    private static readonly Dictionary<PropKind, string[]> PropArt = new()
    {
        [PropKind.Tree] = ["res://art/tree_0.png", "res://art/tree_1.png", "res://art/tree_2.png"],
        [PropKind.Boulder] = ["res://art/boulder.png"],
    };

    private readonly Dictionary<(int X, int Y), Sprite2D> _props = new();
    private readonly Dictionary<int, Sprite2D> _players = new();
    private readonly Dictionary<int, Vector2> _targets = new();

    private Texture2D _playerTexture = null!;
    private Sprite2D? _localSprite;

    public int LocalId { get; set; } = -1;

    public override void _Ready() => _playerTexture = GD.Load<Texture2D>("res://art/player.png");

    /// <summary>Anchors a sprite so its origin sits at the feet, for Y-sorting.</summary>
    private static Sprite2D MakeSprite(Texture2D texture)
    {
        var sprite = new Sprite2D
        {
            Texture = texture,
            Centered = false,
            Offset = new Vector2(-texture.GetWidth() / 2f, -texture.GetHeight()),
        };
        return sprite;
    }

    public void SetProp(int wx, int wy, PropKind kind, int variant)
    {
        if (_props.ContainsKey((wx, wy))) return;

        var art = PropArt[kind];
        var sprite = MakeSprite(GD.Load<Texture2D>(art[variant % art.Length]));
        // Feet sit at the bottom-centre of the tile the prop occupies.
        sprite.Position = new Vector2(
            wx * WorldView.TilePixels + WorldView.TilePixels / 2f,
            (wy + 1) * WorldView.TilePixels);
        AddChild(sprite);
        _props[(wx, wy)] = sprite;
    }

    public void ClearProp(int wx, int wy)
    {
        if (!_props.Remove((wx, wy), out var sprite)) return;
        sprite.QueueFree();
    }

    public void UpdatePlayers(IReadOnlyList<PlayerState> states)
    {
        foreach (var state in states)
        {
            if (state.Id == LocalId) continue;

            var target = new Vector2(state.X, state.Y) * WorldView.TilePixels;
            _targets[state.Id] = target;

            if (_players.ContainsKey(state.Id)) continue;
            var sprite = MakeSprite(_playerTexture);
            sprite.Position = target;
            sprite.Modulate = new Color("ffd9b3");
            AddChild(sprite);
            _players[state.Id] = sprite;
        }
    }

    public void RemovePlayer(int id)
    {
        _targets.Remove(id);
        if (_players.Remove(id, out var sprite)) sprite.QueueFree();
    }

    /// <summary>The local player is positioned from prediction, not snapshots.</summary>
    public void SetLocalPosition(Vector2 tilePosition)
    {
        _localSprite ??= CreateLocalSprite();
        _localSprite.Position = tilePosition * WorldView.TilePixels;
    }

    private Sprite2D CreateLocalSprite()
    {
        var sprite = MakeSprite(_playerTexture);
        AddChild(sprite);
        return sprite;
    }

    public override void _Process(double delta)
    {
        // Remote players are eased toward their last authoritative position so
        // a 15 Hz snapshot rate doesn't read as stuttering.
        float weight = Mathf.Min(1f, (float)delta * 12f);
        foreach (var (id, sprite) in _players)
            if (_targets.TryGetValue(id, out var target))
                sprite.Position = sprite.Position.Lerp(target, weight);
    }
}
