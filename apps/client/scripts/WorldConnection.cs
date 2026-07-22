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

    /// <summary>(seed, chunkSize, playerId, spawn position)</summary>
    public event Action<uint, int, int, Vector3>? Welcomed;
    public event Action<ChunkCoord, TileType[]>? ChunkReceived;
    public event Action<IReadOnlyList<PlayerState>>? PlayersUpdated;
    public event Action<int>? PlayerLeft;
    public event Action<int, int, TileType>? TileChanged;
    public event Action<IReadOnlyDictionary<ItemId, int>>? InventoryUpdated;

    /// <summary>Server rejected a reported position; snap back to this one.</summary>
    public event Action<Vector3, MoveRejection>? Corrected;

    public event Action<string>? StatusChanged;

    /// <summary>
    /// Latest status. Nodes are readied depth-first, so this node connects
    /// before its parent can subscribe — the first status would be lost.
    /// </summary>
    public string Status { get; private set; } = "Connecting…";

    public bool IsLinked => _peer is { ConnectionState: ConnectionState.Connected };

    private NetManager? _net;
    private NetPeer? _peer;
    private readonly NetDataWriter _writer = new();

    public override void _Ready()
    {
        var listener = new EventBasedNetListener();
        listener.PeerConnectedEvent += OnConnected;
        listener.NetworkReceiveEvent += OnReceive;
        listener.PeerDisconnectedEvent += (_, info) =>
        {
            GD.Print($"[client] disconnected: {info.Reason}");
            SetStatus(info.Reason == DisconnectReason.ConnectionFailed
                ? $"Cannot reach world-server at {Host}:{Port}.\nIs it running?  dotnet run --project apps/world-server"
                : $"Disconnected: {info.Reason}");
        };

        _net = new NetManager(listener);
        _net.Start();
        _peer = _net.Connect(Host, Port, ConnectKey);
        GD.Print($"[client] connecting to {Host}:{Port}");
        SetStatus($"Connecting to {Host}:{Port}…");
    }

    public override void _Process(double delta) => _net?.PollEvents();

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

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void OnConnected(NetPeer peer)
    {
        _writer.Reset();
        _writer.Put((byte)MessageId.Hello);
        _writer.Put(ProtocolVersion.Current);
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);
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

            case MessageId.InventoryUpdate:
            {
                int count = reader.GetByte();
                var inventory = new Dictionary<ItemId, int>(count);
                for (int i = 0; i < count; i++)
                    inventory[(ItemId)reader.GetByte()] = reader.GetInt();
                InventoryUpdated?.Invoke(inventory);
                break;
            }

            case MessageId.Correction:
            {
                var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
                Corrected?.Invoke(position, (MoveRejection)reader.GetByte());
                break;
            }
        }
        reader.Recycle();
    }
}
