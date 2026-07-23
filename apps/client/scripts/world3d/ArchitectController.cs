using System;
using System.Collections.Generic;
using System.Linq;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// The client-side architect draft: the pieces the player has laid down but not
/// yet committed. It lives entirely on the client — a private plan never touches
/// the wire until <see cref="Commit"/> — and predicts exactly what the server
/// will accept by running the shared <see cref="BuildingRules"/>. Illegal pieces
/// glow red; the running bill of materials is read straight off the draft.
/// </summary>
public partial class ArchitectController : Node3D
{
    private TerrainGenerator _terrain = null!;
    private Func<int, int, bool> _groundBuildable = (_, _) => true;

    private readonly List<PlannedPiece> _draft = new();
    private readonly Dictionary<PieceSlot, Node3D> _ghosts = new();

    /// <summary>The piece kinds a player cycles through, in menu order.</summary>
    private static readonly BuildPieceKind[] Kinds =
    {
        BuildPieceKind.Foundation, BuildPieceKind.Wall, BuildPieceKind.Doorway,
        BuildPieceKind.Window, BuildPieceKind.Pillar, BuildPieceKind.Roof,
    };

    private int _kindIndex;
    public BuildMaterial Material { get; private set; } = BuildMaterial.Wood;

    public BuildPieceKind Kind => Kinds[_kindIndex];
    public IReadOnlyList<PlannedPiece> Draft => _draft;
    public bool IsEmpty => _draft.Count == 0;

    /// <summary>The palette's piece kinds, in menu order, for the HUD to lay out.</summary>
    public static IReadOnlyList<BuildPieceKind> PieceKinds => Kinds;

    /// <summary>Fired whenever the draft or selection changes, so the HUD refreshes.</summary>
    public event Action? Changed;

    public void Bind(TerrainGenerator terrain, Func<int, int, bool> groundBuildable)
    {
        _terrain = terrain;
        _groundBuildable = groundBuildable;
    }

    /// <summary>Selects a piece kind from the palette, keeping the material valid for it.</summary>
    public void SelectKind(BuildPieceKind kind)
    {
        int index = 0;
        for (int i = 0; i < Kinds.Length; i++)
            if (Kinds[i] == kind) { index = i; break; }
        _kindIndex = index;
        ClampMaterial();
        Changed?.Invoke();
    }

    /// <summary>Selects a material, ignored unless this piece kind supports it — so a
    /// thatch roof and a stone wall are offered but a thatch wall never is.</summary>
    public void SelectMaterial(BuildMaterial material)
    {
        if (!StructureCatalog.MaterialsFor(Kind).Contains(material)) return;
        Material = material;
        Changed?.Invoke();
    }

    /// <summary>Keeps the selected material valid for the current kind after a kind change.</summary>
    private void ClampMaterial()
    {
        var materials = StructureCatalog.MaterialsFor(Kind);
        if (materials.Count > 0 && !materials.Contains(Material)) Material = materials[0];
    }

    /// <summary>
    /// The slot the current kind would occupy at cell (<paramref name="cellX"/>,
    /// <paramref name="cellY"/>), given where the player stands so a wall snaps to
    /// the near edge. Roofs cap the cell, foundations and floors lie on it, posts
    /// rise at its centre.
    /// </summary>
    public PieceSlot SlotFor(int cellX, int cellY, Vector3 player)
    {
        var layer = StructureCatalog.LayerOf(Kind);
        if (layer != PieceLayer.WallNorth) // non-wall kinds keep their fixed layer
            return new PieceSlot(cellX, cellY, 0, layer);

        float metres = (float)TerrainGenerator.TileMetres;
        float dx = player.X - (cellX + 0.5f) * metres;
        float dz = player.Z - (cellY + 0.5f) * metres;
        PieceLayer edge = Mathf.Abs(dz) >= Mathf.Abs(dx)
            ? (dz >= 0 ? PieceLayer.WallNorth : PieceLayer.WallSouth)
            : (dx >= 0 ? PieceLayer.WallEast : PieceLayer.WallWest);
        return PieceSlot.Canonical(cellX, cellY, 0, edge);
    }

    /// <summary>
    /// Lays the current piece at a slot, or lifts it if that slot already holds
    /// one — a tap toggles, so a mis-tap is one tap to undo. Rebuilds the ghosts.
    /// </summary>
    public void Toggle(PieceSlot slot)
    {
        int existing = _draft.FindIndex(p => p.Slot.Equals(slot));
        if (existing >= 0) _draft.RemoveAt(existing);
        else _draft.Add(new PlannedPiece(Kind, Material, slot));
        Rebuild();
    }

    public void Undo()
    {
        if (_draft.Count == 0) return;
        _draft.RemoveAt(_draft.Count - 1);
        Rebuild();
    }

    public void Clear()
    {
        _draft.Clear();
        Rebuild();
    }

    /// <summary>The pieces to commit, or an empty list when the draft is illegal.</summary>
    public IReadOnlyList<PlannedPiece> Commit() => Validation().Ok ? _draft.ToArray() : Array.Empty<PlannedPiece>();

    public BuildingRules.PlanValidation Validation() => BuildingRules.Validate(_draft, _groundBuildable);

    public IReadOnlyList<MaterialCost> BillOfMaterials() => BuildingRules.BillOfMaterials(_draft);

    private void Rebuild()
    {
        foreach (var node in _ghosts.Values) node.QueueFree();
        _ghosts.Clear();

        var bad = new HashSet<PieceSlot>();
        foreach (var problem in Validation().Problems) bad.Add(problem.Key.Slot);

        foreach (var piece in _draft)
        {
            var node = BlueprintPieces3D.Build(piece, _terrain, ghost: true, valid: !bad.Contains(piece.Slot));
            AddChild(node);
            _ghosts[piece.Slot] = node;
        }
        Changed?.Invoke();
    }
}
