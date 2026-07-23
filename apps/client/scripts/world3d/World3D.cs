using System.Collections.Generic;
using Ashfall.Proto;
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
    /// <summary>Chunks generated around the origin for this first slice.</summary>
    [Export] public int Radius { get; set; } = 2;

    /// <summary>Seed supplied by the server on welcome.</summary>
    public uint Seed { get; private set; }

    private TerrainGenerator _terrain = null!;
    private readonly List<MultiMesh> _foliage = new();

    /// <summary>One drawn instance inside a MultiMesh, so a harvested tile's
    /// tree or shrub can be collapsed out of view when the server confirms it.</summary>
    private readonly record struct FoliageRef(MultiMesh Mesh, int Index);

    /// <summary>Every foliage instance a tile owns, so applying a TileChanged
    /// hides exactly the visuals for that tile and nothing else.</summary>
    private readonly Dictionary<(int, int), List<FoliageRef>> _foliageByTile = new();

    /// <summary>Placed structures already rendered, keyed by server id so the
    /// join backfill and live broadcasts never draw the same one twice.</summary>
    private readonly Dictionary<long, Node3D> _structures = new();
    private Node3D _structureRoot = null!;

    private WorldConnection _connection = null!;
    private PlayerBody _player = null!;
    private Label _status = null!;
    private RemotePlayers _remotes = null!;
    private SurvivalHud _hud = null!;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _environment = null!;
    private double _reportTimer;
    private bool _built;

    /// <summary>
    /// Time-of-day in [0,1): 0 midnight, 0.5 noon. Seeded by the server on every
    /// StatsUpdate and advanced locally so the sun glides between heartbeats.
    /// </summary>
    private double _timeOfDay;
    private bool _clockStarted;

    public override void _Ready()
    {
        _connection = GetNode<WorldConnection>("WorldConnection");
        _player = GetNode<PlayerBody>("Player");
        _status = GetNode<Label>("Hud/Status");
        _remotes = GetNode<RemotePlayers>("RemotePlayers");
        _hud = GetNode<SurvivalHud>("Hud/SurvivalHud");
        _sun = GetNode<DirectionalLight3D>("Sun");
        _environment = GetNode<WorldEnvironment>("WorldEnvironment");
        _hud.Bind(_connection);
        _hud.PlaceRequested += OnPlaceRequested;
        _player.MoveStick = _hud.MoveStick;
        _hud.JumpPressed += _player.QueueJump;

        // Nothing is generated until the server says which world this is: the
        // seed is the server's to decide, exactly as in the 2D client.
        _player.ProcessMode = ProcessModeEnum.Disabled;
        _connection.StatusChanged += status => CallDeferred(nameof(ShowStatus), status);
        _connection.Welcomed += (seed, _, id, spawn) => CallDeferred(nameof(OnWelcomed), seed, id, spawn);
        _connection.Corrected += (position, reason) =>
            CallDeferred(nameof(OnCorrected), position, (int)reason);
        _connection.PlayersUpdated += states => _remotes.Apply(states, _localId);
        _connection.PlayerLeft += id => CallDeferred(nameof(OnPlayerLeft), id);
        _connection.StatsUpdated += (_, _, _, timeOfDay) =>
            CallDeferred(nameof(SyncClock), timeOfDay);
        _connection.StructurePlaced += (id, kind, tx, ty) =>
            CallDeferred(nameof(OnStructurePlaced), id, (int)kind, tx, ty);
        _connection.TileChanged += (tx, ty, tile) =>
            CallDeferred(nameof(OnTileChanged), tx, ty, (int)tile);
        ShowStatus(_connection.Status);
    }

    private int _localId = -1;

    private void ShowStatus(string status)
    {
        _status.Text = status;
        _status.Visible = !string.IsNullOrEmpty(status);
    }

    private void OnPlayerLeft(int id) => _remotes.Remove(id);

    /// <summary>The server rejected local physics; snap back to its answer.</summary>
    private void OnCorrected(Vector3 position, int reason)
    {
        GD.Print($"[client] corrected by server: {(MoveRejection)reason}");
        _player.Teleport(position);
    }

    private void OnWelcomed(uint seed, int playerId, Vector3 spawn)
    {
        // A second welcome means we have arrived in a new world via a voyage:
        // drop the old world before building the new one from its seed.
        if (_built) ResetWorld();

        _localId = playerId;
        Seed = seed;
        Build();
        _player.Teleport(spawn + Vector3.Up * 1.5f);
        _player.ProcessMode = ProcessModeEnum.Inherit;
    }

    /// <summary>Free the current world's nodes so a voyage can rebuild cleanly.</summary>
    private void ResetWorld()
    {
        _built = false;
        _worldRoot.QueueFree();
        _foliage.Clear();
        _foliageByTile.Clear();
        _structures.Clear();
        _remotes.Clear();
    }

    /// <summary>Realign the local clock to the server's authoritative time-of-day.</summary>
    private void SyncClock(float timeOfDay)
    {
        _timeOfDay = timeOfDay;
        _clockStarted = true;
    }

    public override void _Process(double delta)
    {
        if (_clockStarted) AdvanceClock(delta);

        if (!_built) return;

        _reportTimer += delta;
        double interval = 1.0 / Proto.Tuning.ClientStateHz;
        if (_reportTimer < interval) return;
        _reportTimer = 0;

        _connection.SendClientState(_player.GlobalPosition, _player.Facing);
    }

    // A horizon tilt keeps the sun's arc off the exact vertical, so orienting the
    // light with LookAt never hits the degenerate parallel-up case at noon.
    private const float SunArcTilt = 0.3f;
    private const float DayLightEnergy = 1.15f;
    private const float NightLightEnergy = 0.03f;
    private const float DayAmbientEnergy = 0.18f;
    private const float NightAmbientEnergy = 0.04f;

    private static readonly Color DayLightColour = new("fff4e6");
    private static readonly Color NightLightColour = new("6678b0");

    /// <summary>
    /// Advance the shared clock by wall-time and re-light the sky. One cycle takes
    /// <see cref="Proto.Tuning.SecondsPerGameDay"/> real seconds, matching the rate
    /// the server counts by, so the smoothing never drifts away from the heartbeat.
    /// </summary>
    private void AdvanceClock(double delta)
    {
        _timeOfDay = (_timeOfDay + delta / Proto.Tuning.SecondsPerGameDay) % 1.0;
        UpdateSky(_timeOfDay);
    }

    private void UpdateSky(double timeOfDay)
    {
        // Sunrise at 0.25, noon overhead at 0.5, sunset at 0.75.
        float phase = (float)(timeOfDay - 0.25) * Mathf.Tau;
        var sunward = new Vector3(Mathf.Cos(phase), Mathf.Sin(phase), SunArcTilt).Normalized();
        _sun.LookAt(_sun.GlobalPosition - sunward, Vector3.Up);

        float daylight = Mathf.Max(0f, Mathf.Sin(phase));
        _sun.LightEnergy = Mathf.Lerp(NightLightEnergy, DayLightEnergy, daylight);
        _sun.LightColor = NightLightColour.Lerp(DayLightColour, daylight);
        _environment.Environment.AmbientLightEnergy =
            Mathf.Lerp(NightAmbientEnergy, DayAmbientEnergy, daylight);
    }

    /// <summary>
    /// Everything derived from the current world's seed hangs under one root so a
    /// voyage can drop the whole world in a single free and rebuild from the new
    /// seed, with no stale terrain or foliage lingering.
    /// </summary>
    private Node3D _worldRoot = null!;

    private void Build()
    {
        _terrain = new TerrainGenerator(Seed);

        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);

        var ground = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.95f,
        };

        var chunks = new Node3D { Name = "Chunks" };
        _worldRoot.AddChild(chunks);

        _structureRoot = new Node3D { Name = "Structures" };
        _worldRoot.AddChild(_structureRoot);

        var trunks = new List<Transform3D>();
        var canopies = new List<Transform3D>();
        var treeTiles = new List<(int, int)>();
        var tufts = new List<Transform3D>();
        var tuftTiles = new List<(int, int)>();

        for (int cy = -Radius; cy <= Radius; cy++)
        {
            for (int cx = -Radius; cx <= Radius; cx++)
            {
                var coord = new ChunkCoord(cx, cy);
                chunks.AddChild(TerrainMesher.Build(_terrain, coord, ground));
                CollectTrees(coord, trunks, canopies, treeTiles);
                CollectShrubs(coord, tufts, tuftTiles);
            }
        }

        var trunkFoliage = BuildFoliage(trunks, new CylinderMesh
        {
            TopRadius = 0.22f,
            BottomRadius = 0.34f,
            Height = 3.2f,
            RadialSegments = 6,
        }, new Color("5a3f2b"));
        _worldRoot.AddChild(trunkFoliage);

        var canopyFoliage = BuildFoliage(canopies, new SphereMesh
        {
            Radius = 1.9f,
            Height = 4.4f,
            RadialSegments = 8,
            Rings = 5,
        }, new Color("3c7a42"));
        _worldRoot.AddChild(canopyFoliage);

        for (int i = 0; i < treeTiles.Count; i++)
        {
            RegisterFoliage(treeTiles[i], trunkFoliage.Multimesh, i);
            RegisterFoliage(treeTiles[i], canopyFoliage.Multimesh, i);
        }

        var tuftFoliage = BuildFoliage(tufts, new SphereMesh
        {
            Radius = 0.35f,
            Height = 0.5f,
            RadialSegments = 6,
            Rings = 3,
        }, new Color("6ea63c"));
        _worldRoot.AddChild(tuftFoliage);

        for (int i = 0; i < tuftTiles.Count; i++)
            RegisterFoliage(tuftTiles[i], tuftFoliage.Multimesh, i);

        _built = true;
        GD.Print($"[world3d] built {(Radius * 2 + 1) * (Radius * 2 + 1)} chunks, "
            + $"{trunks.Count} trees, {tuftTiles.Count} shrub tufts");
    }

    /// <summary>
    /// A Forest tile is still one tree, exactly as in the 2D client — the
    /// gameplay data did not change, only how it is drawn.
    /// </summary>
    private void CollectTrees(
        ChunkCoord coord, List<Transform3D> trunks, List<Transform3D> canopies, List<(int, int)> tiles)
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
                tiles.Add((wx, wy));
            }
        }
    }

    /// <summary>How many tufts stand in on a Shrub tile — enough to read as a
    /// bush from the air, few enough to stay two draw calls for the whole world.</summary>
    private const int TuftsPerShrub = 3;

    /// <summary>
    /// A Shrub tile is a cluster of low tufts, gathered for Fiber. Each tuft is
    /// jittered from the tile's hash so a field of shrubs never looks stamped.
    /// </summary>
    private void CollectShrubs(ChunkCoord coord, List<Transform3D> tufts, List<(int, int)> tiles)
    {
        int size = TerrainGenerator.ChunkSize;
        double metres = TerrainGenerator.TileMetres;

        for (int ly = 0; ly < size; ly++)
        {
            for (int lx = 0; lx < size; lx++)
            {
                int wx = coord.X * size + lx, wy = coord.Y * size + ly;
                if (_terrain.TileAt(wx, wy) != TileType.Shrub) continue;

                float y = (float)_terrain.HeightAt(wx + 0.5, wy + 0.5);

                for (int t = 0; t < TuftsPerShrub; t++)
                {
                    uint hash = SimCore.Noise.Hash(wx, wy, Seed ^ (0x5C2Bu + (uint)t * 0x9E37u));
                    float scale = 0.7f + (hash & 0xFF) / 255f * 0.6f;
                    float offsetX = ((hash >> 8 & 0xFF) / 255f - 0.5f) * (float)metres * 0.7f;
                    float offsetZ = ((hash >> 16 & 0xFF) / 255f - 0.5f) * (float)metres * 0.7f;

                    float x = (float)((wx + 0.5) * metres) + offsetX;
                    float z = (float)((wy + 0.5) * metres) + offsetZ;
                    var basis = Basis.Identity.Scaled(Vector3.One * scale);
                    tufts.Add(new Transform3D(basis, new Vector3(x, y + 0.25f * scale, z)));
                    tiles.Add((wx, wy));
                }
            }
        }
    }

    private void RegisterFoliage((int, int) tile, MultiMesh mesh, int index)
    {
        if (!_foliageByTile.TryGetValue(tile, out var refs))
            _foliageByTile[tile] = refs = new List<FoliageRef>();
        refs.Add(new FoliageRef(mesh, index));
    }

    /// <summary>A press that travels less than this before release is a tap
    /// (harvest); anything further is a camera drag and is left to OrbitCamera.</summary>
    private const float TapMaxTravelPixels = 12f;

    private bool _touching;
    private Vector2 _touchStart;
    private Vector2? _pendingTap;

    /// <summary>
    /// Classifies a touch (or editor mouse click) as a tap. The screen position
    /// is banked and resolved in <see cref="_PhysicsProcess"/>, where the physics
    /// space state is safe to query. Camera drags reach OrbitCamera as usual —
    /// nothing here marks the event handled.
    /// </summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventScreenTouch touch:
                if (touch.Pressed) BeginTap(touch.Position);
                else EndTap(touch.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                if (click.Pressed) BeginTap(click.Position);
                else EndTap(click.Position);
                break;
        }
    }

    private void BeginTap(Vector2 position)
    {
        _touching = true;
        _touchStart = position;
    }

    private void EndTap(Vector2 position)
    {
        if (!_touching) return;
        _touching = false;
        if (position.DistanceTo(_touchStart) <= TapMaxTravelPixels) _pendingTap = position;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_pendingTap is not { } tap) return;
        _pendingTap = null;
        TryHarvestAt(tap);
    }

    /// <summary>How far a harvest ray is allowed to travel before giving up.</summary>
    private const float HarvestRayLengthMetres = 500f;

    /// <summary>
    /// Turns a screen tap into a ChopRequest: raycast to the ground, resolve the
    /// tile, and — only if it is harvestable and within reach — ask the server.
    /// The tile does not change here; the server's TileChanged is what removes
    /// the foliage. Input is a request, never a state change.
    /// </summary>
    private void TryHarvestAt(Vector2 screen)
    {
        if (!_built) return;

        var camera = GetViewport().GetCamera3D();
        if (camera is null) return;

        Vector3 origin = camera.ProjectRayOrigin(screen);
        Vector3 direction = camera.ProjectRayNormal(screen);
        if (!TryGroundHit(origin, direction, out Vector3 hit)) return;

        float metres = (float)TerrainGenerator.TileMetres;
        int tileX = Mathf.FloorToInt(hit.X / metres);
        int tileY = Mathf.FloorToInt(hit.Z / metres);

        if (!HarvestRules.IsHarvestable(_terrain.TileAt(tileX, tileY))) return;

        float centreX = (float)((tileX + 0.5) * metres);
        float centreZ = (float)((tileY + 0.5) * metres);
        Vector3 player = _player.GlobalPosition;
        float dx = player.X - centreX, dz = player.Z - centreZ;
        if (dx * dx + dz * dz > Proto.Tuning.ChopRangeMetres * Proto.Tuning.ChopRangeMetres) return;

        _connection.SendChop(tileX, tileY);
    }

    /// <summary>
    /// Finds where a camera ray meets the ground. Prefers the terrain's trimesh
    /// colliders; if the ray somehow misses them it bisects against the analytic
    /// height field, so a tap always resolves to a point on the surface.
    /// </summary>
    private bool TryGroundHit(Vector3 origin, Vector3 direction, out Vector3 hit)
    {
        var query = PhysicsRayQueryParameters3D.Create(
            origin, origin + direction * HarvestRayLengthMetres);
        var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count > 0)
        {
            hit = (Vector3)result["position"];
            return true;
        }

        return TryMarchGround(origin, direction, out hit);
    }

    /// <summary>Marches the ray until it dips below the height field, then bisects
    /// to the crossing — a fallback for when no collider is hit.</summary>
    private bool TryMarchGround(Vector3 origin, Vector3 direction, out Vector3 hit)
    {
        const float step = 1.0f;
        double metres = TerrainGenerator.TileMetres;

        float previous = 0f;
        for (float t = step; t <= HarvestRayLengthMetres; t += step)
        {
            Vector3 point = origin + direction * t;
            double ground = _terrain.HeightAt(point.X / metres, point.Z / metres);
            if (point.Y <= ground)
            {
                float lo = previous, high = t;
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + high) * 0.5f;
                    Vector3 p = origin + direction * mid;
                    if (p.Y <= _terrain.HeightAt(p.X / metres, p.Z / metres)) high = mid;
                    else lo = mid;
                }
                hit = origin + direction * high;
                return true;
            }
            previous = t;
        }

        hit = Vector3.Zero;
        return false;
    }

    /// <summary>
    /// Applies the server's authoritative tile change: when a harvested tile
    /// reverts to bare ground, collapse the tree or shrub that stood on it. The
    /// MultiMesh instance can't be deleted cheaply, so it is scaled to nothing —
    /// invisible, and still just two draw calls.
    /// </summary>
    private void OnTileChanged(int tileX, int tileY, int tile)
    {
        if (!_built) return;
        if (!_foliageByTile.TryGetValue((tileX, tileY), out var refs)) return;

        var collapsed = new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero);
        foreach (var (mesh, index) in refs)
        {
            Vector3 origin = mesh.GetInstanceTransform(index).Origin;
            mesh.SetInstanceTransform(index, new Transform3D(collapsed, origin));
        }
        _foliageByTile.Remove((tileX, tileY));
    }

    /// <summary>How far ahead of the player a structure is placed, in tiles.</summary>
    private const float PlaceReachTiles = 1.0f;

    /// <summary>
    /// Places at the tile directly in front of the player. Facing is the yaw the
    /// physics body reports; forward derives from it the same way the body does,
    /// so "in front" matches where the character looks.
    /// </summary>
    private void OnPlaceRequested(ItemId kind)
    {
        if (!_built) return;

        float yaw = _player.Facing;
        var forward = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
        float metres = (float)TerrainGenerator.TileMetres;
        Vector3 target = _player.GlobalPosition + forward * (PlaceReachTiles * metres);

        int tileX = Mathf.FloorToInt(target.X / metres);
        int tileY = Mathf.FloorToInt(target.Z / metres);
        _connection.SendPlace(kind, tileX, tileY);
    }

    private static readonly Color WallColour = new("8a8f99");
    private static readonly Color CampfireColour = new("d0743a");

    /// <summary>
    /// Renders one placed structure at the centre of its tile, sitting on the
    /// ground. Idempotent by server id so the join backfill and a live broadcast
    /// of the same structure collapse to a single mesh.
    /// </summary>
    private void OnStructurePlaced(long id, int kind, int tileX, int tileY)
    {
        if (!_built || _structures.ContainsKey(id)) return;

        double metres = TerrainGenerator.TileMetres;
        float x = (float)((tileX + 0.5) * metres);
        float z = (float)((tileY + 0.5) * metres);
        float y = (float)_terrain.HeightAt(tileX + 0.5, tileY + 0.5);

        var node = BuildStructure((ItemId)kind);
        node.Position = new Vector3(x, y, z);
        _structureRoot.AddChild(node);
        _structures[id] = node;
    }

    /// <summary>A box for a Wall, a small lit shape for a Campfire.</summary>
    private static Node3D BuildStructure(ItemId kind) => kind switch
    {
        ItemId.Campfire => BuildCampfire(),
        _ => BuildWall(),
    };

    private static MeshInstance3D BuildWall() => new()
    {
        Mesh = new BoxMesh { Size = new Vector3((float)TerrainGenerator.TileMetres, 2.4f, 0.4f) },
        Position = Vector3.Up * 1.2f,
        MaterialOverride = new StandardMaterial3D { AlbedoColor = WallColour, Roughness = 0.9f },
    };

    private static Node3D BuildCampfire()
    {
        var root = new Node3D();
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.6f, Height = 0.5f, RadialSegments = 8 },
            Position = Vector3.Up * 0.25f,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = CampfireColour,
                EmissionEnabled = true,
                Emission = new Color("ff7a1a"),
                EmissionEnergyMultiplier = 2.0f,
                Roughness = 0.8f,
            },
        });
        root.AddChild(new OmniLight3D
        {
            Position = Vector3.Up * 0.8f,
            LightColor = new Color("ff9a3c"),
            LightEnergy = 2.2f,
            OmniRange = 8f,
        });
        return root;
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
