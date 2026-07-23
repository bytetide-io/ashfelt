using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Turns a chunk of sim-core terrain into a mesh with collision.
///
/// Nothing here is authored or stored: the mesh is derived from the seed, the
/// same way the 2D tilemap was. Vertex colour carries the surface type, so one
/// material covers every biome and the whole chunk is a single draw call.
/// </summary>
public static class TerrainMesher
{
    private static readonly Color DeepWaterColour = new("1b3a5c");
    private static readonly Color WaterColour = new("2f6690");
    private static readonly Color SandColour = new("d6c180");
    private static readonly Color GrassColour = new("4a7c40");
    private static readonly Color ForestColour = new("3a5f34");
    private static readonly Color RockColour = new("6e6a63");
    private static readonly Color ShrubColour = new("5c8a3c");

    private static Color ColourOf(TileType tile) => tile switch
    {
        TileType.DeepWater => DeepWaterColour,
        TileType.Water => WaterColour,
        TileType.Sand => SandColour,
        TileType.Grass => GrassColour,
        TileType.Forest => ForestColour,
        TileType.Shrub => ShrubColour,
        _ => RockColour,
    };

    /// <summary>
    /// Builds one chunk. Vertices sit on tile corners, so neighbouring chunks
    /// share edge positions exactly and no seam appears between them.
    /// </summary>
    public static MeshInstance3D Build(TerrainGenerator terrain, ChunkCoord coord, Material material)
    {
        int size = TerrainGenerator.ChunkSize;
        double metres = TerrainGenerator.TileMetres;
        int ox = coord.X * size, oy = coord.Y * size;

        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);

        for (int ly = 0; ly < size; ly++)
        {
            for (int lx = 0; lx < size; lx++)
            {
                int wx = ox + lx, wy = oy + ly;
                var colour = ColourOf(terrain.TileAt(wx, wy));

                // Corner heights, sampled continuously rather than per tile —
                // that is what makes the ground roll instead of stair-step.
                var a = Corner(terrain, wx, wy, ox, oy);
                var b = Corner(terrain, wx + 1, wy, ox, oy);
                var c = Corner(terrain, wx + 1, wy + 1, ox, oy);
                var d = Corner(terrain, wx, wy + 1, ox, oy);

                AddTriangle(surface, colour, a, b, c);
                AddTriangle(surface, colour, a, c, d);
            }
        }

        surface.GenerateNormals();

        var instance = new MeshInstance3D
        {
            Mesh = surface.Commit(),
            MaterialOverride = material,
            Position = new Vector3((float)(ox * metres), 0, (float)(oy * metres)),
        };
        // Trimesh collision matches the visible surface exactly, so the player
        // can never stand somewhere the terrain isn't.
        instance.CreateTrimeshCollision();
        return instance;
    }

    /// <summary>
    /// Corner position in chunk-local metres. Height is sampled at world
    /// coordinates but the vertex is local, so chunks stay near the origin and
    /// keep their floating-point precision far from the world centre.
    /// </summary>
    private static Vector3 Corner(TerrainGenerator terrain, int wx, int wy, int ox, int oy)
    {
        double metres = TerrainGenerator.TileMetres;
        return new Vector3(
            (float)((wx - ox) * metres),
            (float)terrain.HeightAt(wx, wy),
            (float)((wy - oy) * metres));
    }

    private static void AddTriangle(SurfaceTool surface, Color colour, Vector3 a, Vector3 b, Vector3 c)
    {
        // Vertex colours are consumed as linear, but the palette is authored in
        // sRGB like every other colour in the project. Without this conversion
        // everything renders pale and washed out.
        colour = colour.SrgbToLinear();

        surface.SetColor(colour);
        surface.AddVertex(a);
        surface.SetColor(colour);
        surface.AddVertex(b);
        surface.SetColor(colour);
        surface.AddVertex(c);
    }
}
