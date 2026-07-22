using System;
using System.Collections.Generic;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Ashfall.Client;

public readonly record struct PlayerState(int Id, float X, float Y);

/// <summary>
/// UDP link to a single world-server. The server is authoritative; this node
/// only sends input and surfaces what it receives.
/// </summary>
public partial class WorldConnection : Node
{
    [Export] public string Host { get; set; } = "127.0.0.1";
    [Export] public int Port { get; set; } = 9050;
    [Export] public string ConnectKey { get; set; } = "ashfall";

    /// <summary>(seed, chunkSize, playerId, spawnX, spawnY)</summary>
    public event Action<uint, int, int, float, float>? Welcomed;
    public event Action<ChunkCoord, TileType[]>? ChunkReceived;
    public event Action<IReadOnlyList<PlayerState>>? PlayersUpdated;
    public event Action<int>? PlayerLeft;
    public event Action<int, int, TileType>? TileChanged;
    public event Action<IReadOnlyDictionary<ItemId, int>>? InventoryUpdated;

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
            GD.Print($"[client] disconnected: {info.Reason}");

        _net = new NetManager(listener) { UnsyncedEvents = false };
        _net.Start();
        _peer = _net.Connect(Host, Port, ConnectKey);
        GD.Print($"[client] connecting to {Host}:{Port}");
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

    /// <summary>Sends an input direction. Unreliable — a newer intent supersedes it.</summary>
    public void SendMoveIntent(Vector2 direction)
    {
        if (!IsLinked) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.MoveIntent);
        _writer.Put(direction.X);
        _writer.Put(direction.Y);
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
                float x = reader.GetFloat(), y = reader.GetFloat();
                GD.Print($"[client] welcomed as player {playerId}, seed={seed}");
                Welcomed?.Invoke(seed, chunkSize, playerId, x, y);
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
                    states.Add(new PlayerState(reader.GetInt(), reader.GetFloat(), reader.GetFloat()));
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
        }
        reader.Recycle();
    }
}
