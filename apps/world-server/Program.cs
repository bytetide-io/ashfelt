using Ashfall.Proto;
using Ashfall.SimCore;
using LiteNetLib;
using LiteNetLib.Utils;

int port = int.TryParse(Environment.GetEnvironmentVariable("ASHFALL_PORT"), out var p) ? p : 9050;
uint seed = uint.TryParse(Environment.GetEnvironmentVariable("ASHFALL_SEED"), out var s) ? s : 1337u;
string key = Environment.GetEnvironmentVariable("ASHFALL_CONNECT_KEY") ?? "ashfall";

var terrain = new TerrainGenerator(seed);
var writer = new NetDataWriter();
var listener = new EventBasedNetListener();
var server = new NetManager(listener) { UpdateTime = 15 };

listener.ConnectionRequestEvent += request => request.AcceptIfKey(key);

listener.PeerConnectedEvent += peer =>
    Console.WriteLine($"[world] peer connected: {peer.Address}");

listener.PeerDisconnectedEvent += (peer, info) =>
    Console.WriteLine($"[world] peer disconnected: {peer.Address} ({info.Reason})");

listener.NetworkReceiveEvent += (peer, reader, _, _) =>
{
    var id = (MessageId)reader.GetByte();
    switch (id)
    {
        case MessageId.Hello:
        {
            int clientVersion = reader.GetInt();
            writer.Reset();
            writer.Put((byte)MessageId.Welcome);
            writer.Put(ProtocolVersion.Current);
            writer.Put(seed);
            writer.Put(TerrainGenerator.ChunkSize);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            Console.WriteLine($"[world] hello from client (proto v{clientVersion})");
            break;
        }
        case MessageId.RequestChunk:
        {
            var coord = new ChunkCoord(reader.GetInt(), reader.GetInt());
            // Terrain is regenerated, never loaded: seed + coord is the source of
            // truth. Player diffs get layered on top here in Phase 1.
            var chunk = terrain.Generate(coord);

            writer.Reset();
            writer.Put((byte)MessageId.ChunkData);
            writer.Put(coord.X);
            writer.Put(coord.Y);
            foreach (var tile in chunk.Tiles) writer.Put((byte)tile);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            break;
        }
        default:
            Console.WriteLine($"[world] unhandled message {id}");
            break;
    }
    reader.Recycle();
};

server.Start(port);
Console.WriteLine($"[world] listening on udp/{port}, seed={seed}");

using var shutdown = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Set(); };

while (!shutdown.IsSet)
{
    server.PollEvents();
    Thread.Sleep(15);
}

server.Stop();
Console.WriteLine("[world] stopped");
