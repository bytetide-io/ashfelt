using System.Collections.Generic;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Builds the visible world from the seed: terrain meshes, trees and water.
///
/// This is the 3D replacement for the tilemap client. It still follows the
/// seed+diffs rule — nothing here is downloaded or stored, it is all derived
/// from a seed the server will supply once networking is ported.
/// </summary>
public partial class World3D : Node3D
{
    [Export] public uint Seed { get; set; } = 1337;

    /// <summary>Chunks generated around the origin for this first slice.</summary>
    [Export] public int Radius { get; set; } = 2;

    private TerrainGenerator _terrain = null!;
    private readonly List<MultiMesh> _foliage = new();

    public override void _Ready()
    {
        _terrain = new TerrainGenerator(Seed);

        var ground = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.95f,
        };

        var chunks = new Node3D { Name = "Chunks" };
        AddChild(chunks);

        var trunks = new List<Transform3D>();
        var canopies = new List<Transform3D>();

        for (int cy = -Radius; cy <= Radius; cy++)
        {
            for (int cx = -Radius; cx <= Radius; cx++)
            {
                var coord = new ChunkCoord(cx, cy);
                chunks.AddChild(TerrainMesher.Build(_terrain, coord, ground));
                CollectTrees(coord, trunks, canopies);
            }
        }

        AddChild(BuildFoliage(trunks, new CylinderMesh
        {
            TopRadius = 0.22f,
            BottomRadius = 0.34f,
            Height = 3.2f,
            RadialSegments = 6,
        }, new Color("5a3f2b")));

        AddChild(BuildFoliage(canopies, new SphereMesh
        {
            Radius = 1.9f,
            Height = 4.4f,
            RadialSegments = 8,
            Rings = 5,
        }, new Color("3c7a42")));

        PlacePlayer();

        GD.Print($"[world3d] built {(Radius * 2 + 1) * (Radius * 2 + 1)} chunks, {trunks.Count} trees");
    }

    /// <summary>
    /// Drops the player onto dry land near the origin, rather than wherever
    /// the scene happened to put them — which might be the seabed.
    /// </summary>
    private void PlacePlayer()
    {
        var player = GetNodeOrNull<Node3D>("Player");
        if (player is null) return;

        double metres = TerrainGenerator.TileMetres;
        for (int radius = 0; radius < 200; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (System.Math.Abs(dx) != radius && System.Math.Abs(dy) != radius) continue;
                    if (_terrain.TileAt(dx, dy) is not (TileType.Grass or TileType.Sand)) continue;

                    // A little above the surface so the first physics tick
                    // settles the capsule onto the ground instead of inside it.
                    player.Position = new Vector3(
                        (float)((dx + 0.5) * metres),
                        (float)_terrain.HeightAt(dx + 0.5, dy + 0.5) + 2f,
                        (float)((dy + 0.5) * metres));
                    return;
                }
            }
        }
    }

    /// <summary>
    /// A Forest tile is still one tree, exactly as in the 2D client — the
    /// gameplay data did not change, only how it is drawn.
    /// </summary>
    private void CollectTrees(ChunkCoord coord, List<Transform3D> trunks, List<Transform3D> canopies)
    {
        int size = TerrainGenerator.ChunkSize;
        double metres = TerrainGenerator.TileMetres;

        for (int ly = 0; ly < size; ly++)
        {
            for (int lx = 0; lx < size; lx++)
            {
                int wx = coord.X * size + lx, wy = coord.Y * size + ly;
                if (_terrain.TileAt(wx, wy) != TileType.Forest) continue;

                // Centre of the tile, sitting on the ground.
                float x = (float)((wx + 0.5) * metres);
                float z = (float)((wy + 0.5) * metres);
                float y = (float)_terrain.HeightAt(wx + 0.5, wy + 0.5);

                // Deterministic jitter so a forest is not a lattice.
                uint hash = SimCore.Noise.Hash(wx, wy, Seed ^ 0x51F7u);
                float scale = 0.8f + (hash & 0xFF) / 255f * 0.5f;
                float lean = ((hash >> 8 & 0xFF) / 255f - 0.5f) * 0.12f;
                float offsetX = ((hash >> 16 & 0xFF) / 255f - 0.5f) * (float)metres * 0.5f;
                float offsetZ = ((hash >> 24 & 0xFF) / 255f - 0.5f) * (float)metres * 0.5f;

                var basis = new Basis(Vector3.Forward, lean).Scaled(Vector3.One * scale);
                var root = new Vector3(x + offsetX, y, z + offsetZ);

                trunks.Add(new Transform3D(basis, root + Vector3.Up * 1.6f * scale));
                canopies.Add(new Transform3D(basis, root + Vector3.Up * 4.2f * scale));
            }
        }
    }

    /// <summary>
    /// One MultiMesh per part keeps thousands of trees to two draw calls,
    /// which is what makes this viable on a phone.
    /// </summary>
    private MultiMeshInstance3D BuildFoliage(List<Transform3D> instances, Mesh mesh, Color colour)
    {
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = instances.Count,
        };
        for (int i = 0; i < instances.Count; i++) multi.SetInstanceTransform(i, instances[i]);
        _foliage.Add(multi);

        return new MultiMeshInstance3D
        {
            Multimesh = multi,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.9f },
        };
    }
}
