using System.Collections.Generic;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// Builds the visible world from the seed plus the server's chunk diffs:
/// terrain meshes, trees and water.
///
/// This is the 3D replacement for the tilemap client. It follows the
/// seed+diffs rule: on Welcome the client requests every chunk in the build
/// radius and waits for all of them before generating foliage, so a tile the
/// server already harvested is never regenerated as a pristine, choppable
/// tree.
/// </summary>
public partial class World3D : Node3D
{
    /// <summary>Chunks generated around the origin for this first slice.</summary>
    [Export] public int Radius { get; set; } = 2;

    /// <summary>
    /// The HUD and its status label live outside the low-res SubViewport so they
    /// render crisp at full resolution; the world reaches them by path.
    /// </summary>
    [Export] public NodePath HudPath { get; set; } = new();
    [Export] public NodePath StatusPath { get; set; } = new();

    /// <summary>Seed supplied by the server on welcome.</summary>
    public uint Seed { get; private set; }

    private TerrainGenerator _terrain = null!;
    private readonly List<MultiMesh> _foliage = new();

    /// <summary>One drawn instance inside a MultiMesh, with the full-size transform
    /// it was built at. A harvested tile's tree or shrub shrinks toward nothing as
    /// it is struck and collapses out of view when finally felled — both scale the
    /// stored original, so repeated strikes never compound.</summary>
    private readonly record struct FoliageRef(MultiMesh Mesh, int Index, Transform3D Original);

    /// <summary>Every foliage instance a tile owns, so a harvest strike or felling
    /// touches exactly the visuals for that tile and nothing else.</summary>
    private readonly Dictionary<(int, int), List<FoliageRef>> _foliageByTile = new();

    /// <summary>Client-side tile diffs, mirroring the server's seed+diffs rule: a
    /// harvested tile reverts here too, so the gather reticle and tap both read the
    /// tile as it now is rather than the pristine generated terrain. Without this
    /// the client keeps offering to gather an already-felled node. Seeded from the
    /// server's chunk data on join/voyage, then kept current by live TileChanged
    /// broadcasts.</summary>
    private readonly Dictionary<(int, int), TileType> _tileDiffs = new();

    /// <summary>Chunk tile data fetched from the server for the current build
    /// radius, keyed by chunk coord. Populated by <see cref="RequestChunks"/> and
    /// consumed once every requested chunk has arrived.</summary>
    private readonly Dictionary<ChunkCoord, TileType[]> _fetchedChunks = new();

    /// <summary>Chunks requested but not yet answered. Building waits for this to
    /// empty so foliage generation always sees the server's diffs, not just the
    /// pristine seed.</summary>
    private readonly HashSet<ChunkCoord> _pendingChunks = new();

    /// <summary>Spawn position held between the chunk request and the build it
    /// gates, so the player is placed only once the world actually exists.</summary>
    private Vector3 _pendingSpawn;

    private TileType EffectiveTile(int x, int y) =>
        _tileDiffs.TryGetValue((x, y), out var tile) ? tile : _terrain.TileAt(x, y);

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
    private CrtOverlay _crt = null!;
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
        _status = GetNode<Label>(StatusPath);
        StyleStatus(_status);
        _remotes = GetNode<RemotePlayers>("RemotePlayers");
        _hud = GetNode<SurvivalHud>(HudPath);
        _sun = GetNode<DirectionalLight3D>("Sun");
        _environment = GetNode<WorldEnvironment>("WorldEnvironment");
        // The CRT grade sits over the world but under the HUD's own CanvasLayer,
        // so scanlines never dim the interface.
        _crt = new CrtOverlay();
        AddChild(_crt);
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
        _connection.ChunkReceived += OnChunkReceived;
        _connection.PlayerLeft += id => CallDeferred(nameof(OnPlayerLeft), id);
        _connection.StatsUpdated += (_, _, _, _, timeOfDay) =>
            CallDeferred(nameof(SyncClock), timeOfDay);
        _connection.StructurePlaced += (id, kind, tx, ty) =>
            CallDeferred(nameof(OnStructurePlaced), id, (int)kind, tx, ty);
        _connection.TileChanged += (tx, ty, tile) =>
            CallDeferred(nameof(OnTileChanged), tx, ty, (int)tile);
        _connection.HarvestProgress += (tx, ty, left, total) =>
            CallDeferred(nameof(OnHarvestProgress), tx, ty, left, total);
        ShowStatus(_connection.Status);
    }

    private int _localId = -1;

    /// <summary>Dress the connecting/notice label in the design's parchment-on-ink pill.</summary>
    private static void StyleStatus(Label status)
    {
        if (DesignSystem.Display is { } font) status.AddThemeFontOverride("font", font);
        status.AddThemeFontSizeOverride("font_size", DesignSystem.LabelSize);
        status.AddThemeColorOverride("font_color", DesignSystem.Parchment);
        status.AddThemeStyleboxOverride("normal", DesignSystem.Panel(new Color(DesignSystem.Ink900, 0.82f), 14));
    }

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
        _terrain = new TerrainGenerator(seed);
        _pendingSpawn = spawn;
        RequestChunks();
    }

    /// <summary>Free the current world's nodes so a voyage can rebuild cleanly.</summary>
    private void ResetWorld()
    {
        _built = false;
        _worldRoot.QueueFree();
        _foliage.Clear();
        _foliageByTile.Clear();
        _tileDiffs.Clear();
        _fetchedChunks.Clear();
        _pendingChunks.Clear();
        _structures.Clear();
        _remotes.Clear();
    }

    /// <summary>
    /// Asks the server for every chunk the build radius will render. Building is
    /// deferred until all of them are in hand — the pristine seed alone cannot
    /// tell a standing tree from one already felled by another player, or by this
    /// same player last session.
    /// </summary>
    private void RequestChunks()
    {
        _fetchedChunks.Clear();
        _pendingChunks.Clear();
        ShowStatus("Loading world…");

        for (int cy = -Radius; cy <= Radius; cy++)
        for (int cx = -Radius; cx <= Radius; cx++)
        {
            var coord = new ChunkCoord(cx, cy);
            _pendingChunks.Add(coord);
            _connection.RequestChunk(coord);
        }
    }

    /// <summary>
    /// Records one fetched chunk. Once every requested chunk is in, folds every
    /// tile that differs from the pristine seed into <see cref="_tileDiffs"/> —
    /// the same overlay a live TileChanged applies — so <see cref="Build"/> never
    /// grows foliage the server has already harvested away.
    /// </summary>
    private void OnChunkReceived(ChunkCoord coord, TileType[] tiles)
    {
        _fetchedChunks[coord] = tiles;
        if (!_pendingChunks.Remove(coord) || _pendingChunks.Count > 0) return;

        int size = TerrainGenerator.ChunkSize;
        foreach (var (chunkCoord, chunkTiles) in _fetchedChunks)
        {
            int ox = chunkCoord.X * size, oy = chunkCoord.Y * size;
            for (int ly = 0; ly < size; ly++)
            for (int lx = 0; lx < size; lx++)
            {
                int wx = ox + lx, wy = oy + ly;
                var actual = chunkTiles[ly * size + lx];
                if (actual != _terrain.TileAt(wx, wy)) _tileDiffs[(wx, wy)] = actual;
            }
        }

        ShowStatus("");
        Build();
        _player.Teleport(_pendingSpawn + Vector3.Up * 1.5f);
        _player.ProcessMode = ProcessModeEnum.Inherit;
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
        _crt.SetTimeOfDay((float)timeOfDay);
    }

    /// <summary>
    /// Everything derived from the current world's seed hangs under one root so a
    /// voyage can drop the whole world in a single free and rebuild from the new
    /// seed, with no stale terrain or foliage lingering.
    /// </summary>
    private Node3D _worldRoot = null!;

    private void Build()
    {
        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);

        // Vertex colour carries the biome hue; the ground grain adds the walkable
        // pixel texels on top, mapped in world space by TerrainMesher's UVs.
        var ground = FlatMaterial(texture: PixelTextures.Ground());
        ground.VertexColorUseAsAlbedo = true;

        var chunks = new Node3D { Name = "Chunks" };
        _worldRoot.AddChild(chunks);

        _structureRoot = new Node3D { Name = "Structures" };
        _worldRoot.AddChild(_structureRoot);

        var trunks = new List<Transform3D>();
        var canopies = new List<Transform3D>();
        var treeTiles = new List<(int, int)>();
        var tufts = new List<Transform3D>();
        var tuftTiles = new List<(int, int)>();
        var berries = new List<Transform3D>();
        var berryTiles = new List<(int, int)>();

        for (int cy = -Radius; cy <= Radius; cy++)
        {
            for (int cx = -Radius; cx <= Radius; cx++)
            {
                var coord = new ChunkCoord(cx, cy);
                chunks.AddChild(TerrainMesher.Build(_terrain, coord, ground));
                CollectTrees(coord, trunks, canopies, treeTiles);
                CollectShrubs(coord, tufts, tuftTiles);
                CollectBerryBushes(coord, berries, berryTiles);
            }
        }

        var trunkFoliage = BuildFoliage(trunks, new CylinderMesh
        {
            TopRadius = 0.22f,
            BottomRadius = 0.34f,
            Height = 3.2f,
            RadialSegments = 6,
        }, new Color("5a3f2b"), PixelTextures.Bark(), new Vector3(1, 3, 1));
        _worldRoot.AddChild(trunkFoliage);

        var canopyFoliage = BuildFoliage(canopies, new SphereMesh
        {
            Radius = 1.9f,
            Height = 4.4f,
            RadialSegments = 8,
            Rings = 5,
        }, new Color("3c7a42"), PixelTextures.Leaf(), new Vector3(2, 2, 1));
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
        }, new Color("6ea63c"), PixelTextures.Leaf());
        _worldRoot.AddChild(tuftFoliage);

        for (int i = 0; i < tuftTiles.Count; i++)
            RegisterFoliage(tuftTiles[i], tuftFoliage.Multimesh, i);

        var berryFoliage = BuildFoliage(berries, new SphereMesh
        {
            Radius = 0.42f,
            Height = 0.7f,
            RadialSegments = 6,
            Rings = 3,
        }, new Color("7c3b52"), PixelTextures.Berry());
        _worldRoot.AddChild(berryFoliage);

        for (int i = 0; i < berryTiles.Count; i++)
            RegisterFoliage(berryTiles[i], berryFoliage.Multimesh, i);

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
                if (EffectiveTile(wx, wy) != TileType.Forest) continue;

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
                if (EffectiveTile(wx, wy) != TileType.Shrub) continue;

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

    /// <summary>How many berry clusters stand on one bush tile.</summary>
    private const int ClustersPerBush = 2;

    /// <summary>
    /// A BerryBush tile is a small cluster of rounded, berry-dark forms, foraged
    /// for food. Jittered from the tile hash like shrubs so a patch never looks
    /// stamped; a distinct salt keeps its layout independent of the shrub tufts.
    /// </summary>
    private void CollectBerryBushes(ChunkCoord coord, List<Transform3D> clusters, List<(int, int)> tiles)
    {
        int size = TerrainGenerator.ChunkSize;
        double metres = TerrainGenerator.TileMetres;

        for (int ly = 0; ly < size; ly++)
        {
            for (int lx = 0; lx < size; lx++)
            {
                int wx = coord.X * size + lx, wy = coord.Y * size + ly;
                if (EffectiveTile(wx, wy) != TileType.BerryBush) continue;

                float y = (float)_terrain.HeightAt(wx + 0.5, wy + 0.5);

                for (int c = 0; c < ClustersPerBush; c++)
                {
                    uint hash = SimCore.Noise.Hash(wx, wy, Seed ^ (0xB3EEu + (uint)c * 0x9E37u));
                    float scale = 0.8f + (hash & 0xFF) / 255f * 0.5f;
                    float offsetX = ((hash >> 8 & 0xFF) / 255f - 0.5f) * (float)metres * 0.5f;
                    float offsetZ = ((hash >> 16 & 0xFF) / 255f - 0.5f) * (float)metres * 0.5f;

                    float x = (float)((wx + 0.5) * metres) + offsetX;
                    float z = (float)((wy + 0.5) * metres) + offsetZ;
                    var basis = Basis.Identity.Scaled(Vector3.One * scale);
                    clusters.Add(new Transform3D(basis, new Vector3(x, y + 0.32f * scale, z)));
                    tiles.Add((wx, wy));
                }
            }
        }
    }

    private void RegisterFoliage((int, int) tile, MultiMesh mesh, int index)
    {
        if (!_foliageByTile.TryGetValue(tile, out var refs))
            _foliageByTile[tile] = refs = new List<FoliageRef>();
        refs.Add(new FoliageRef(mesh, index, mesh.GetInstanceTransform(index)));
    }

    /// <summary>A press that travels less than this before release is a tap
    /// (harvest); anything further is a camera drag and is left to OrbitCamera.</summary>
    private const float TapMaxTravelPixels = 12f;

    private bool _touching;
    private Vector2 _touchStart;
    private bool _harvestQueued;

    /// <summary>The nearest harvestable tile in reach, resolved each physics frame
    /// for the reticle. A tap gathers this rather than whatever pixel the thumb
    /// landed on, so hitting a tree never demands pixel-accurate aim.</summary>
    private (int X, int Y)? _gatherTarget;

    /// <summary>
    /// Classifies a touch (or editor mouse click) as a tap. A tap gathers the
    /// currently reticled node; the resolution happens in <see cref="_PhysicsProcess"/>.
    /// Camera drags reach OrbitCamera as usual — nothing here marks the event handled.
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
        if (position.DistanceTo(_touchStart) <= TapMaxTravelPixels) _harvestQueued = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        UpdateGatherPrompt();

        if (!_harvestQueued) return;
        _harvestQueued = false;

        // Input is a request, never a state change: the tile does not change here.
        // The server's TileChanged / HarvestProgress is what wears down the node.
        if (_gatherTarget is { } target) _connection.SendChop(target.X, target.Y);
    }

    /// <summary>
    /// Surfaces the design's "tap to gather" reticle over the nearest harvestable
    /// tile in reach, so the one-thumb player always knows what a tap will pick.
    /// This is presentation only — it reads the shared harvest rules the server
    /// also enforces, and hides when nothing is close enough to chop.
    /// </summary>
    private void UpdateGatherPrompt()
    {
        if (!_built) { _gatherTarget = null; _hud.HideGatherPrompt(); return; }
        var camera = GetViewport().GetCamera3D();
        if (camera is null) { _gatherTarget = null; _hud.HideGatherPrompt(); return; }

        float metres = (float)TerrainGenerator.TileMetres;
        float range = (float)Proto.Tuning.ChopRangeMetres;
        Vector3 player = _player.GlobalPosition;
        int radius = Mathf.CeilToInt(range / metres);
        int playerTileX = Mathf.FloorToInt(player.X / metres);
        int playerTileY = Mathf.FloorToInt(player.Z / metres);

        float bestSq = range * range;
        bool found = false;
        int bestX = 0, bestY = 0;
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int tileX = playerTileX + dx, tileY = playerTileY + dy;
            if (!HarvestRules.IsHarvestable(EffectiveTile(tileX, tileY))) continue;

            float centreX = (float)((tileX + 0.5) * metres);
            float centreZ = (float)((tileY + 0.5) * metres);
            float distSq = (player.X - centreX) * (player.X - centreX)
                         + (player.Z - centreZ) * (player.Z - centreZ);
            if (distSq >= bestSq) continue;
            bestSq = distSq;
            bestX = tileX;
            bestY = tileY;
            found = true;
        }

        if (!found) { _gatherTarget = null; _hud.HideGatherPrompt(); return; }

        _gatherTarget = (bestX, bestY);

        float worldX = (float)((bestX + 0.5) * metres);
        float worldZ = (float)((bestY + 0.5) * metres);
        float worldY = (float)_terrain.HeightAt(bestX + 0.5, bestY + 0.5);
        Vector2 viewportPoint = camera.UnprojectPosition(new Vector3(worldX, worldY, worldZ));

        // The world renders in a half-resolution SubViewport; scale its point up
        // into the full-window space the HUD lives in.
        Vector2 subSize = GetViewport().GetVisibleRect().Size;
        Vector2 hudSize = _hud.Size;
        Vector2 scale = subSize == Vector2.Zero ? Vector2.One : hudSize / subSize;

        var tile = EffectiveTile(bestX, bestY);
        _hud.ShowGatherPrompt(viewportPoint * scale, HarvestRules.Evaluate(tile).Item);
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

        // Record the diff so the reticle and tap stop treating this tile as the
        // node it used to be — the fix for re-tapping a felled stump forever.
        _tileDiffs[(tileX, tileY)] = (TileType)tile;

        if (!_foliageByTile.TryGetValue((tileX, tileY), out var refs)) return;

        var collapsed = new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero);
        foreach (var reference in refs)
            reference.Mesh.SetInstanceTransform(
                reference.Index, new Transform3D(collapsed, reference.Original.Origin));
        _foliageByTile.Remove((tileX, tileY));
    }

    /// <summary>
    /// A node was struck but not yet felled: shrink its foliage toward nothing in
    /// proportion to the strikes left, so a tree visibly comes down over several
    /// taps instead of vanishing in one. Scaling the stored original keeps repeated
    /// strikes from compounding.
    /// </summary>
    private void OnHarvestProgress(int tileX, int tileY, int strikesLeft, int strikesTotal)
    {
        if (!_built || strikesTotal <= 0) return;
        if (!_foliageByTile.TryGetValue((tileX, tileY), out var refs)) return;

        float factor = Mathf.Clamp(strikesLeft / (float)strikesTotal, 0f, 1f);
        foreach (var reference in refs)
        {
            var original = reference.Original;
            var shrunk = new Transform3D(original.Basis.Scaled(Vector3.One * factor), original.Origin);
            reference.Mesh.SetInstanceTransform(reference.Index, shrunk);
        }
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
        MaterialOverride = FlatMaterial(WallColour),
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
                Roughness = 1.0f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
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
    /// A flat, banded material: toon-stepped diffuse with no specular gives the
    /// medium pixel-art read the design calls for, and nearest filtering keeps
    /// every texel crisp. Combined with the fullscreen pixelation pass, the
    /// world looks hand-placed rather than smoothly lit.
    /// </summary>
    private static StandardMaterial3D FlatMaterial(
        Color? albedo = null, Texture2D? texture = null, Vector3? uvScale = null)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = albedo ?? Colors.White,
            Roughness = 1.0f,
            DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        };
        if (texture is not null) material.AlbedoTexture = texture;
        if (uvScale is { } scale) material.Uv1Scale = scale;
        return material;
    }

    /// <summary>
    /// One MultiMesh per part keeps thousands of trees to two draw calls,
    /// which is what makes this viable on a phone.
    /// </summary>
    private MultiMeshInstance3D BuildFoliage(
        List<Transform3D> instances, Mesh mesh, Color colour, Texture2D? texture = null, Vector3? uvScale = null)
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
            MaterialOverride = FlatMaterial(colour, texture, uvScale),
        };
    }
}
