using System;
using Ashfall.Proto;
using Ashfall.SimCore;
using Godot;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Ashfall.Client;

/// <summary>
/// UDP link to a single world-server. The server is authoritative; this node
/// only requests data and surfaces what it receives.
/// </summary>
public partial class WorldConnection : Node
{
    [Export] public string Host { get; set; } = "127.0.0.1";
    [Export] public int Port { get; set; } = 9050;
    [Export] public string ConnectKey { get; set; } = "ashfall";

    public event Action<uint, int>? Welcomed;
    public event Action<ChunkCoord, TileType[]>? ChunkReceived;

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

        _net = new NetManager(listener);
        _net.Start();
        _peer = _net.Connect(Host, Port, ConnectKey);
        GD.Print($"[client] connecting to {Host}:{Port}");
    }

    public override void _Process(double delta) => _net?.PollEvents();

    public override void _ExitTree() => _net?.Stop();

    public void RequestChunk(ChunkCoord coord)
    {
        if (_peer is null) return;
        _writer.Reset();
        _writer.Put((byte)MessageId.RequestChunk);
        _writer.Put(coord.X);
        _writer.Put(coord.Y);
        _peer.Send(_writer, DeliveryMethod.ReliableOrdered);
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
                if (serverVersion != ProtocolVersion.Current)
                    GD.PushWarning($"[client] protocol mismatch: server v{serverVersion}, client v{ProtocolVersion.Current}");
                GD.Print($"[client] welcomed, seed={seed}");
                Welcomed?.Invoke(seed, chunkSize);
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
        }
        reader.Recycle();
    }
}
