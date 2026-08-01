using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Turns a <see cref="PlannedPiece"/> into a positioned 3D mesh. One place owns
/// the geometry so a draft ghost, a committed hologram and a built piece all read
/// as the same object at the same spot — only their material differs. Positions
/// derive from the canonical slot: ground pieces lie on the cell, wall pieces
/// stand on the cell edge they name, a roof caps the cell, a post rises at its
/// centre.
/// </summary>
public static class BlueprintPieces3D
{
    private const float WallHeight = 2.6f;
    private const float WallThickness = 0.22f;
    private const float SlabThickness = 0.25f;
    private const float PostThickness = 0.34f;

    private static readonly Color WoodColour = new("8a6a43");
    private static readonly Color StoneColour = new("8d9099");
    private static readonly Color ThatchColour = new("c2a24a");
    private static readonly Color GlassColour = new("9fd3e0");

    /// <summary>The storey height a level adds, so upper floors stack cleanly.</summary>
    public static float LevelHeight => WallHeight;

    /// <summary>
    /// Builds the piece as a node positioned in world space. <paramref name="ghost"/>
    /// draws it translucent (a plan or hologram); <paramref name="valid"/> tints an
    /// illegal ghost red so the designer sees what will be refused.
    /// </summary>
    public static Node3D Build(PlannedPiece piece, TerrainGenerator terrain, bool ghost, bool valid = true)
    {
        float metres = (float)TerrainGenerator.TileMetres;
        var slot = piece.Slot;
        float ground = (float)terrain.HeightAt(slot.X + 0.5, slot.Y + 0.5);
        float baseY = ground + slot.Level * LevelHeight;

        var node = Geometry(piece.Kind, metres);
        node.Position = Anchor(slot, metres, baseY);
        // A wall box lies along X by default; the east edge runs along Z, so turn it.
        if (slot.Layer == PieceLayer.WallEast) node.Rotation = new Vector3(0, Mathf.Pi * 0.5f, 0);
        Paint(node, MaterialFor(piece, ghost, valid));

        // A built, solid piece collides; a ghost (pending or cursor) is walked
        // through, so a plan never blocks the player laying it out.
        bool solid = !ghost
            && StructureCatalog.TryGet(piece.Kind, piece.Material, out var def)
            && def.Solid;
        return solid ? WithCollision(node) : node;
    }

    /// <summary>Wraps a solid piece's box in a static body with a matching collider,
    /// keeping its world transform so it blocks exactly where it is drawn.</summary>
    private static Node3D WithCollision(Node3D node)
    {
        var body = new StaticBody3D { Position = node.Position, Rotation = node.Rotation };
        node.Position = Vector3.Zero;
        node.Rotation = Vector3.Zero;
        body.AddChild(node);
        if (node is MeshInstance3D { Mesh: BoxMesh box })
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size } });
        return body;
    }

    private static Node3D Geometry(BuildPieceKind kind, float metres) => kind switch
    {
        BuildPieceKind.Foundation => Slab(metres, SlabThickness),
        BuildPieceKind.Floor => Slab(metres, SlabThickness),
        BuildPieceKind.Roof => Slab(metres, SlabThickness),
        BuildPieceKind.Pillar => Box(new Vector3(PostThickness, WallHeight, PostThickness)),
        BuildPieceKind.Doorway => Doorway(metres),
        _ => Box(new Vector3(metres, WallHeight, WallThickness)), // Wall / Window
    };

    /// <summary>
    /// Where the piece sits, in metres. Ground and roof centre on the cell; a wall
    /// snaps to the edge its layer names; a post rises at the cell centre.
    /// </summary>
    private static Vector3 Anchor(PieceSlot slot, float metres, float baseY)
    {
        float cx = (slot.X + 0.5f) * metres;
        float cz = (slot.Y + 0.5f) * metres;

        return slot.Layer switch
        {
            PieceLayer.Ground => new Vector3(cx, baseY + SlabThickness * 0.5f, cz),
            PieceLayer.Cover => new Vector3(cx, baseY + LevelHeight - SlabThickness * 0.5f, cz),
            PieceLayer.Post => new Vector3(cx, baseY + WallHeight * 0.5f, cz),
            // North edge sits at the far Z of the cell; East edge at the far X.
            PieceLayer.WallNorth => new Vector3(cx, baseY + WallHeight * 0.5f, (slot.Y + 1f) * metres),
            PieceLayer.WallEast => new Vector3((slot.X + 1f) * metres, baseY + WallHeight * 0.5f, cz),
            _ => new Vector3(cx, baseY + WallHeight * 0.5f, cz),
        };
    }

    private static MeshInstance3D Box(Vector3 size) => new() { Mesh = new BoxMesh { Size = size } };

    private static MeshInstance3D Slab(float metres, float thickness) =>
        new() { Mesh = new BoxMesh { Size = new Vector3(metres, thickness, metres) } };

    /// <summary>A doorway: two jambs and a lintel, leaving a walk-through gap.</summary>
    private static Node3D Doorway(float metres)
    {
        var root = new Node3D();
        float jamb = metres * 0.22f;
        float gap = metres - 2 * jamb;

        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(jamb, WallHeight, WallThickness) },
            Position = new Vector3(-(gap + jamb) * 0.5f, 0, 0),
        });
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(jamb, WallHeight, WallThickness) },
            Position = new Vector3((gap + jamb) * 0.5f, 0, 0),
        });
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(gap, WallHeight * 0.24f, WallThickness) },
            Position = new Vector3(0, WallHeight * 0.38f, 0),
        });
        return root;
    }

    private static StandardMaterial3D MaterialFor(PlannedPiece piece, bool ghost, bool valid)
    {
        Color tint = piece.Kind == BuildPieceKind.Window
            ? GlassColour
            : piece.Material switch
            {
                BuildMaterial.Stone => StoneColour,
                BuildMaterial.Thatch => ThatchColour,
                _ => WoodColour,
            };

        if (ghost) tint = valid ? new Color(0.55f, 0.8f, 1f) : new Color(1f, 0.4f, 0.38f);

        var material = new StandardMaterial3D
        {
            AlbedoColor = ghost ? new Color(tint, 0.42f) : tint,
            Roughness = 1.0f,
            DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
        if (ghost)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
        return material;
    }

    private static void Paint(Node3D node, StandardMaterial3D material)
    {
        if (node is MeshInstance3D mesh) { mesh.MaterialOverride = material; return; }
        foreach (var child in node.GetChildren())
            if (child is MeshInstance3D part) part.MaterialOverride = material;
    }
}
