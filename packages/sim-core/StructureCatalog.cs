using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>A quantity of one item — a build cost line or a deposit.</summary>
public readonly record struct MaterialCost(ItemId Item, int Amount);

/// <summary>What must already exist for a piece to be legally placed or built.</summary>
public enum SupportKind : byte
{
    /// <summary>Rests on walkable terrain (foundations, ground-level posts).</summary>
    Ground,
    /// <summary>Needs the piece directly below in the same cell (upper floors).</summary>
    Below,
    /// <summary>Needs a ground-layer piece in a cell the wall borders.</summary>
    Foundation,
    /// <summary>Needs a wall or a post on the cell (roofs, upper floors).</summary>
    WallOrPost,
}

/// <summary>
/// The static facts about one building piece: which layer it fills, what it costs
/// to build, how many strikes raise it, what it must rest on, and how it behaves
/// once built (solid? sheltering?). Keyed by the wire-stable
/// (<see cref="BuildPieceKind"/>, <see cref="BuildMaterial"/>) pair.
///
/// This is to buildings what <see cref="ItemCatalog"/> is to items: the single
/// declarative source both client and server read (invariant #4). Adding a
/// buildable piece — a stone doorway, a thatch roof — is one row here, no server,
/// protocol or client branch. Costs and strikes are integers so the whole system
/// stays deterministic (invariant #5).
/// </summary>
public readonly record struct StructureDef(
    BuildPieceKind Kind,
    BuildMaterial Material,
    PieceLayer Layer,
    SupportKind Support,
    bool Solid,
    bool ProvidesShelter,
    bool IsStation,
    int BuildStrikes,
    int Tier,
    IReadOnlyList<MaterialCost> Cost);

/// <summary>
/// The building-piece table, in stable declaration order. Shared, ordered,
/// integer-valued and covered by tests asserting every cost references a real
/// item and every piece's layer matches its kind. Iterate <see cref="All"/> for
/// a deterministic build menu; look a piece up with <see cref="TryGet"/>.
/// </summary>
public static class StructureCatalog
{
    private static readonly PieceLayer WallSlot = PieceLayer.WallNorth; // canonical wall layer used by the menu

    public static readonly IReadOnlyList<StructureDef> All = new StructureDef[]
    {
        new(BuildPieceKind.Foundation, BuildMaterial.Wood, PieceLayer.Ground, SupportKind.Ground,
            Solid: false, ProvidesShelter: false, IsStation: false, BuildStrikes: 3, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Wood, 4) }),
        new(BuildPieceKind.Foundation, BuildMaterial.Stone, PieceLayer.Ground, SupportKind.Ground,
            Solid: false, ProvidesShelter: false, IsStation: false, BuildStrikes: 4, Tier: 2,
            Cost: new MaterialCost[] { new(ItemId.Stone, 6) }),

        new(BuildPieceKind.Floor, BuildMaterial.Wood, PieceLayer.Ground, SupportKind.Below,
            Solid: false, ProvidesShelter: false, IsStation: false, BuildStrikes: 2, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Plank, 2) }),

        new(BuildPieceKind.Wall, BuildMaterial.Wood, WallSlot, SupportKind.Foundation,
            Solid: true, ProvidesShelter: true, IsStation: false, BuildStrikes: 3, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Plank, 3) }),
        new(BuildPieceKind.Wall, BuildMaterial.Stone, WallSlot, SupportKind.Foundation,
            Solid: true, ProvidesShelter: true, IsStation: false, BuildStrikes: 4, Tier: 2,
            Cost: new MaterialCost[] { new(ItemId.Stone, 5) }),

        new(BuildPieceKind.Doorway, BuildMaterial.Wood, WallSlot, SupportKind.Foundation,
            Solid: false, ProvidesShelter: true, IsStation: false, BuildStrikes: 3, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Plank, 4) }),

        new(BuildPieceKind.Window, BuildMaterial.Wood, WallSlot, SupportKind.Foundation,
            Solid: true, ProvidesShelter: true, IsStation: false, BuildStrikes: 3, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Plank, 2), new(ItemId.Fiber, 2) }),

        new(BuildPieceKind.Pillar, BuildMaterial.Wood, PieceLayer.Post, SupportKind.Ground,
            Solid: false, ProvidesShelter: false, IsStation: false, BuildStrikes: 2, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Wood, 3) }),

        new(BuildPieceKind.Roof, BuildMaterial.Thatch, PieceLayer.Cover, SupportKind.WallOrPost,
            Solid: false, ProvidesShelter: true, IsStation: false, BuildStrikes: 2, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Fiber, 5) }),
        new(BuildPieceKind.Roof, BuildMaterial.Wood, PieceLayer.Cover, SupportKind.WallOrPost,
            Solid: false, ProvidesShelter: true, IsStation: false, BuildStrikes: 2, Tier: 1,
            Cost: new MaterialCost[] { new(ItemId.Plank, 3) }),
    };

    private static readonly IReadOnlyDictionary<(BuildPieceKind, BuildMaterial), StructureDef> ByKey =
        All.ToDictionary(def => (def.Kind, def.Material));

    public static bool TryGet(BuildPieceKind kind, BuildMaterial material, out StructureDef def) =>
        ByKey.TryGetValue((kind, material), out def);

    /// <summary>The definition for a piece pair, or throws — an unknown pair is a content bug.</summary>
    public static StructureDef Of(BuildPieceKind kind, BuildMaterial material) =>
        ByKey.TryGetValue((kind, material), out var def)
            ? def
            : throw new ArgumentOutOfRangeException(nameof(kind), (kind, material), "No StructureDef for this pair.");

    /// <summary>
    /// The materials a piece kind can be built from, in catalog order — so the
    /// designer only ever offers a material that actually exists for the piece.
    /// </summary>
    public static IReadOnlyList<BuildMaterial> MaterialsFor(BuildPieceKind kind)
    {
        var materials = new List<BuildMaterial>();
        foreach (var def in All)
            if (def.Kind == kind && !materials.Contains(def.Material)) materials.Add(def.Material);
        return materials;
    }

    /// <summary>The layer a piece kind fills, independent of which wall edge a wall lands on.</summary>
    public static PieceLayer LayerOf(BuildPieceKind kind) => kind switch
    {
        BuildPieceKind.Foundation or BuildPieceKind.Floor => PieceLayer.Ground,
        BuildPieceKind.Wall or BuildPieceKind.Doorway or BuildPieceKind.Window => PieceLayer.WallNorth,
        BuildPieceKind.Pillar => PieceLayer.Post,
        BuildPieceKind.Roof => PieceLayer.Cover,
        _ => PieceLayer.None,
    };

    /// <summary>True when a kind is placed on a cell edge (a wall, door or window).</summary>
    public static bool IsEdgeKind(BuildPieceKind kind) =>
        kind is BuildPieceKind.Wall or BuildPieceKind.Doorway or BuildPieceKind.Window;
}
