using System;
using System.Collections.Generic;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Ashfall.Client;

public readonly record struct PlayerState(int Id, Vector3 Position, float Yaw);

/// <summary>
/// UDP link to a single world-server.
///
/// The client simulates its own physics and reports the result; the server
/// validates it and corrects when implausible. Everything else — chunks,
/// harvesting, inventory — remains server-authoritative outright.
/// </summary>
public partial class WorldConnection : Node
{
    [Export] public string Host { get; set; } = "127.0.0.1";
    [Export] public int Port { get; set; } = 9050;
    [Export] public string ConnectKey { get; set; } = "ashfall";

    /// <summary>Gateway base URL, used to list voyage destinations.</summary>
    [Export] public string GatewayUrl { get; set; } = "http://127.0.0.1:5041";

    /// <summary>(seed, chunkSize, playerId, spawn position)</summary>
    public event Action<uint, int, int, Vector3>? Welcomed;
    public event Action<ChunkCoord, TileType[]>? ChunkReceived;
    public event Action<IReadOnlyList<PlayerState>>? PlayersUpdated;
    public event Action<int>? PlayerLeft;
    public event Action<int, int, TileType>? TileChanged;

    /// <summary>(tileX, tileY, strikes remaining, strikes total) — a node worn down
    /// but not yet felled, so the client can show it taking the hit.</summary>
    public event Action<int, int, int, int>? HarvestProgress;
    public event Action<IReadOnlyDictionary<ItemId, int>>? InventoryUpdated;

    /// <summary>(structure id, kind, tileX, tileY) — a structure to render.</summary>
    public event Action<long, ItemId, int, int>? StructurePlaced;

    /// <summary>(hunger, stamina, health, warmth) in display points, plus time-of-day in [0,1).</summary>
    public event Action<int, int, int, int, float>? StatsUpdated;

    /// <summary>The current world refused a voyage; the reason, for the player.</summary>
    public event Action<string>? VoyageDenied;

    /// <summary>Server rejected a reported position; snap back to this one.</summary>
    public event Action<Vector3, MoveRejection>? Corrected;

    public event Action<string>? StatusChanged;

    /// <summary>
    /// Latest status. Nodes are readied depth-first, so this node connects
    /// before its parent can subscribe — the first status would be lost.
    /// </summary>
    public string Status { get; private set; } = "Connecting…";

    /// <summary>Seconds between reconnection attempts.</summary>
    [Export] public double RetryInterval { get; set; } = 3.0;

    public bool IsLinked => _peer is { ConnectionState: ConnectionState.Connected };

    /// <summary>Where the device's character UUID is persisted between runs.</summary>
    private const string CharacterIdPath = "user://character_id";

    private NetManager? _net;
    private NetPeer? _peer;
    private readonly NetDataWriter _writer = new();
    private double _retryIn;
    private int _attempts;
    private Guid _characterId;

    public override void _Ready()
    {
        _characterId = LoadOrCreateCharacterId();

        var listener = new EventBasedNetListener();
        listener.PeerConnectedEvent += OnConnected;
        listener.NetworkReceiveEvent += OnReceive;
        listener.PeerDisconnectedEvent += (_, info) =>
        {
            GD.Print($"[client] disconnected: {info.Reason}");
            SetStatus(info.Reason == DisconnectReason.ConnectionFailed
                ? $"Waiting for world-server at {Host}:{Port}…\ndotnet run --project apps/world-server"
                : $"Disconnected: {info.Reason}. Reconnecting…");
            _retryIn = RetryInterval;
        };

        _net = new NetManager(listener);
        _net.Start();
        Connect();
    }

    private void Connect()
    {
        _attempts++;
        _peer = _net!.Connect(Host, Port, ConnectKey);
        GD.Print($"[client] connecting to {Host}:{Port} (attempt {_attempts})");
        SetStatus($"Connecting to {Host}:{Port}…");
    }

    public override void _Process(double delta)
    {
        _net?.PollEvents();

        // Keep retrying rather than stranding the player on an error screen:
        // starting the world-server after the game is the normal dev order,
        // and a dropped server should not cost a restart.
        if (IsLinked || _net is null) return;

        _retryIn -= delta;
        if (_retryIn > 0) return;

        _retryIn = RetryInterval;
        if (_peer is null or { ConnectionState: ConnectionState.Disconnected }) Connect();
    }

    public override void _ExitTree() => _net?.Stop();

    public void RequestChunk(ChunkCoord coord)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.RequestChunk);
        _writer.Put(coord.X);
        _writer.Put(coord.Y);
        _peer!.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Reports where local physics ended up. Unreliable: a dropped update is
    /// superseded by the next, and the server budgets movement by elapsed
    /// time, so losing packets costs the player nothing.
    /// </summary>
    public void SendClientState(Vector3 position, float yaw)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.ClientState);
        _writer.Put(position.X);
        _writer.Put(position.Y);
        _writer.Put(position.Z);
        _writer.Put(yaw);
        _peer!.Send(_writer, DeliveryMethod.Unreliable);
    }

    public void SendChop(int tileX, int tileY)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.ChopRequest);
        _writer.Put(tileX);
        _writer.Put(tileY);
        _peer!.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendCraft(ItemId output)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.CraftRequest);
        _writer.Put((byte)output);
        _peer!.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Ask to eat one edible item. The server authorises it against the item's
    /// food value and consumes one from inventory; the restored meters and
    /// reduced stack come back as the usual stats and inventory updates.
    /// </summary>
    public void SendEat(ItemId food)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.EatRequest);
        _writer.Put((byte)food);
        _peer!.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendPlace(ItemId kind, int tileX, int tileY)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.PlaceRequest);
        _writer.Put((byte)kind);
        _writer.Put(tileX);
        _writer.Put(tileY);
        _peer!.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Ask the current world to release this character toward another world. The
    /// server persists the character, mints a ticket and replies with
    /// <see cref="MessageId.ReleaseGranted"/>; the client never carries inventory,
    /// only the ticket it then presents to the destination.
    /// </summary>
    public void SendRequestRelease(string targetWorldId)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.RequestRelease);
        _writer.Put(targetWorldId);
        _peer!.Send(_writer, DeliveryMethod.ReliableOrdered);
        SetStatus($"Voyaging to {targetWorldId}…");
    }

    /// <summary>
    /// The grant arrived: point at the destination and reconnect. The pending
    /// ticket rides the next Hello, which is how the destination admits us.
    /// </summary>
    private void BeginVoyage(string host, int port, string ticket)
    {
        GD.Print($"[client] voyage granted → {host}:{port}");
        Host = host;
        Port = port;
        _pendingTicket = ticket;

        _peer?.Disconnect();
        _peer = null;
        _retryIn = 0; // reconnect on the next poll, now aimed at the destination
    }

    /// <summary>A world the player can voyage to, from the gateway registry.</summary>
    public readonly record struct WorldInfo(string Id, string Host, int Port);

    /// <summary>
    /// The gateway's world registry, so the travel menu can list destinations.
    /// Networked off the main thread; callers marshal the result back themselves.
    /// </summary>
    public async System.Threading.Tasks.Task<IReadOnlyList<WorldInfo>> FetchWorldsAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            string json = await http.GetStringAsync($"{GatewayUrl.TrimEnd('/')}/worlds");
            var worlds = System.Text.Json.JsonSerializer.Deserialize<List<WorldInfo>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return worlds ?? new List<WorldInfo>();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[client] could not fetch worlds: {ex.Message}");
            return new List<WorldInfo>();
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Ticket to present on the next connect. Empty means a normal join; the
    /// voyage flow sets it before reconnecting to the destination world.
    /// </summary>
    private string _pendingTicket = "";

    private void OnConnected(NetPeer peer)
    {
        _writer.Reset();
        _writer.Put((byte)MessageId.Hello);
        _writer.Put(ProtocolVersion.Current);
        // The device UUID identifies which stored character to load; the server
        // reads exactly 16 bytes after the version.
        _writer.Put(_characterId.ToByteArray());
        // Voyage ticket: empty on a normal join, set only when arriving from
        // another world. The server validates a non-empty ticket before it will
        // admit the character.
        _writer.Put(_pendingTicket);
        _pendingTicket = "";
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Reads the persisted device UUID, generating and saving one on first run.
    /// There is no login: this UUID is the character's identity.
    /// </summary>
    private static Guid LoadOrCreateCharacterId()
    {
        if (Godot.FileAccess.FileExists(CharacterIdPath))
        {
            using var read = Godot.FileAccess.Open(CharacterIdPath, Godot.FileAccess.ModeFlags.Read);
            if (read is not null && Guid.TryParse(read.GetAsText().Trim(), out var existing))
                return existing;
            GD.PushWarning("[client] character_id unreadable; generating a new one");
        }

        var created = Guid.NewGuid();
        using var write = Godot.FileAccess.Open(CharacterIdPath, Godot.FileAccess.ModeFlags.Write);
        write?.StoreString(created.ToString());
        GD.Print($"[client] generated character id {created}");
        return created;
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var id = (MessageId)reader.GetByte();
        switch (id)
        {
            case MessageId.Welcome:
            {
                int serverVersion = reader.GetInt();
                uint seed = reader.GetUInt();
                int chunkSize = reader.GetInt();
                int playerId = reader.GetInt();
                var spawn = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

                if (serverVersion != ProtocolVersion.Current)
                    GD.PushWarning($"[client] protocol mismatch: server v{serverVersion}");

                GD.Print($"[client] welcomed as player {playerId}, seed={seed}");
                SetStatus("");
                Welcomed?.Invoke(seed, chunkSize, playerId, spawn);
                break;
            }

            case MessageId.ChunkData:
            {
                var coord = new ChunkCoord(reader.GetInt(), reader.GetInt());
                var tiles = new TileType[TerrainGenerator.ChunkSize * TerrainGenerator.ChunkSize];
                for (int i = 0; i < tiles.Length; i++) tiles[i] = (TileType)reader.GetByte();
                ChunkReceived?.Invoke(coord, tiles);
                break;
            }

            case MessageId.PlayerStates:
            {
                int count = reader.GetByte();
                var states = new List<PlayerState>(count);
                for (int i = 0; i < count; i++)
                {
                    states.Add(new PlayerState(
                        reader.GetInt(),
                        new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()),
                        reader.GetFloat()));
                }
                PlayersUpdated?.Invoke(states);
                break;
            }

            case MessageId.PlayerLeft:
                PlayerLeft?.Invoke(reader.GetInt());
                break;

            case MessageId.TileChanged:
            {
                int tx = reader.GetInt(), ty = reader.GetInt();
                TileChanged?.Invoke(tx, ty, (TileType)reader.GetByte());
                break;
            }

            case MessageId.HarvestProgress:
            {
                int tx = reader.GetInt(), ty = reader.GetInt();
                int left = reader.GetByte(), total = reader.GetByte();
                HarvestProgress?.Invoke(tx, ty, left, total);
                break;
            }

            case MessageId.InventoryUpdate:
            {
                int count = reader.GetByte();
                var inventory = new Dictionary<ItemId, int>(count);
                for (int i = 0; i < count; i++)
                    inventory[(ItemId)reader.GetByte()] = reader.GetInt();
                InventoryUpdated?.Invoke(inventory);
                break;
            }

            case MessageId.StatsUpdate:
            {
                int hunger = reader.GetInt();
                int stamina = reader.GetInt();
                int health = reader.GetInt();
                int warmth = reader.GetInt();
                float timeOfDay = reader.GetFloat();
                StatsUpdated?.Invoke(hunger, stamina, health, warmth, timeOfDay);
                break;
            }

            case MessageId.StructurePlaced:
            {
                long structureId = reader.GetLong();
                var kind = (ItemId)reader.GetByte();
                int tx = reader.GetInt(), ty = reader.GetInt();
                StructurePlaced?.Invoke(structureId, kind, tx, ty);
                break;
            }

            case MessageId.Correction:
            {
                var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
                Corrected?.Invoke(position, (MoveRejection)reader.GetByte());
                break;
            }

            case MessageId.ReleaseGranted:
            {
                string host = reader.GetString();
                int port = reader.GetInt();
                string ticket = reader.GetString();
                BeginVoyage(host, port, ticket);
                break;
            }

            case MessageId.ReleaseDenied:
            {
                string reason = reader.GetString();
                GD.Print($"[client] voyage denied: {reason}");
                SetStatus($"Cannot voyage: {reason}");
                VoyageDenied?.Invoke(reason);
                break;
            }
        }
        reader.Recycle();
    }
}
