using Ashfall.Proto;
using Ashfall.SimCore;
using Ashfall.WorldServer;
using LiteNetLib;
using LiteNetLib.Utils;

int port = int.TryParse(Environment.GetEnvironmentVariable("ASHFALL_PORT"), out var p) ? p : 9050;
uint seed = uint.TryParse(Environment.GetEnvironmentVariable("ASHFALL_SEED"), out var s) ? s : 1337u;
string key = Environment.GetEnvironmentVariable("ASHFALL_CONNECT_KEY") ?? "ashfall";
string worldId = Environment.GetEnvironmentVariable("ASHFALL_WORLD_ID") ?? "continent-a";
string? conn = Environment.GetEnvironmentVariable("ASHFALL_DB");

var world = new World(seed);
await using var store = await WorldStore.OpenAsync(conn, worldId, seed);
int restored = await store.LoadDiffsAsync(world);
Console.WriteLine($"[world] seed={seed} restored {restored} diff(s)");

var players = new Dictionary<NetPeer, Player>();
var writer = new NetDataWriter();
int nextPlayerId = 1;

var listener = new EventBasedNetListener();
var server = new NetManager(listener) { UpdateTime = 15 };

listener.ConnectionRequestEvent += request => request.AcceptIfKey(key);

var clock = System.Diagnostics.Stopwatch.StartNew();
double Now() => clock.Elapsed.TotalSeconds;

listener.PeerConnectedEvent += peer =>
{
    var spawn = Player.FindSpawn(world, world.Terrain);
    var player = new Player(nextPlayerId++, peer, spawn);
    player.ResetClock(Now());
    players[peer] = player;
    Console.WriteLine($"[world] player {player.Id} connected from {peer.Address} " +
                      $"at ({spawn.X:F1},{spawn.Y:F1},{spawn.Z:F1})");
};

listener.PeerDisconnectedEvent += (peer, info) =>
{
    if (!players.Remove(peer, out var player)) return;
    Console.WriteLine($"[world] player {player.Id} disconnected ({info.Reason})");

    writer.Reset();
    writer.Put((byte)MessageId.PlayerLeft);
    writer.Put(player.Id);
    Broadcast(writer, exclude: peer);
};

listener.NetworkReceiveEvent += (peer, reader, _, _) =>
{
    if (!players.TryGetValue(peer, out var player)) { reader.Recycle(); return; }

    var id = (MessageId)reader.GetByte();
    switch (id)
    {
        case MessageId.Hello:
        {
            int clientVersion = reader.GetInt();
            if (clientVersion != ProtocolVersion.Current)
            {
                Console.WriteLine($"[world] rejecting client with proto v{clientVersion}");
                peer.Disconnect();
                break;
            }

            writer.Reset();
            writer.Put((byte)MessageId.Welcome);
            writer.Put(ProtocolVersion.Current);
            writer.Put(seed);
            writer.Put(TerrainGenerator.ChunkSize);
            writer.Put(player.Id);
            writer.Put((float)player.Position.X);
            writer.Put((float)player.Position.Y);
            writer.Put((float)player.Position.Z);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            player.InventoryDirty = true;
            break;
        }

        case MessageId.RequestChunk:
        {
            var coord = new ChunkCoord(reader.GetInt(), reader.GetInt());
            Console.WriteLine($"[world] player {player.Id} requested chunk ({coord.X},{coord.Y})");
            // Terrain is regenerated from the seed, with stored diffs layered
            // on top — chunks themselves are never persisted.
            var chunk = world.GenerateChunk(coord);

            writer.Reset();
            writer.Put((byte)MessageId.ChunkData);
            writer.Put(coord.X);
            writer.Put(coord.Y);
            foreach (var tile in chunk.Tiles) writer.Put((byte)tile);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            break;
        }

        case MessageId.ClientState:
        {
            var reported = new Vec3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            float yaw = reader.GetFloat();

            // The client simulates physics; the server decides whether the
            // result was possible. Anything else is taken on trust nowhere.
            var rejection = player.TryAccept(world.Terrain, reported, yaw, Now());
            if (rejection == MoveRejection.None) break;

            Console.WriteLine($"[world] player {player.Id} move rejected: {rejection}");
            writer.Reset();
            writer.Put((byte)MessageId.Correction);
            writer.Put((float)player.Position.X);
            writer.Put((float)player.Position.Y);
            writer.Put((float)player.Position.Z);
            writer.Put((byte)rejection);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            break;
        }

        case MessageId.ChopRequest:
        {
            int tx = reader.GetInt(), ty = reader.GetInt();
            if (!player.IsWithinReach(tx, ty))
            {
                Console.WriteLine($"[world] player {player.Id} chop out of range at ({tx},{ty})");
                break;
            }

            var harvest = world.TryHarvest(tx, ty);
            if (!harvest.Allowed) break;

            player.Give(harvest.Item, harvest.Amount);
            // Fire-and-forget the write: the diff is already authoritative in
            // memory, and stalling the packet loop on the database would stall
            // every other player.
            _ = store.SaveDiffAsync(tx, ty, harvest.Becomes);

            writer.Reset();
            writer.Put((byte)MessageId.TileChanged);
            writer.Put(tx);
            writer.Put(ty);
            writer.Put((byte)harvest.Becomes);
            Broadcast(writer);

            Console.WriteLine($"[world] player {player.Id} harvested {harvest.Item} at ({tx},{ty})");
            break;
        }

        default:
            Console.WriteLine($"[world] unhandled message {id}");
            break;
    }
    reader.Recycle();
};

if (!server.Start(port))
{
    // Without this check a failed bind still reached the tick loop and logged
    // "listening", so a second instance looked healthy while accepting nothing.
    Console.Error.WriteLine($"[world] FATAL: could not bind udp/{port} — is another world-server running?");
    return 1;
}
Console.WriteLine($"[world] listening on udp/{port}");

using var shutdown = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Set(); };

int tickMs = 1000 / Tuning.TicksPerSecond;

while (!shutdown.IsSet)
{
    server.PollEvents();

    if (players.Count > 0)
    {
        writer.Reset();
        writer.Put((byte)MessageId.PlayerStates);
        writer.Put((byte)players.Count);
        foreach (var player in players.Values)
        {
            writer.Put(player.Id);
            writer.Put((float)player.Position.X);
            writer.Put((float)player.Position.Y);
            writer.Put((float)player.Position.Z);
            writer.Put(player.Yaw);
        }
        // Unreliable: a dropped snapshot is replaced by the next one 66ms later.
        Broadcast(writer, method: DeliveryMethod.Unreliable);

        foreach (var player in players.Values)
        {
            if (!player.InventoryDirty) continue;
            player.InventoryDirty = false;

            writer.Reset();
            writer.Put((byte)MessageId.InventoryUpdate);
            writer.Put((byte)player.Inventory.Count);
            foreach (var (item, amount) in player.Inventory)
            {
                writer.Put((byte)item);
                writer.Put(amount);
            }
            player.Peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
    }

    Thread.Sleep(tickMs);
}

server.Stop();
Console.WriteLine("[world] stopped");
return 0;

void Broadcast(NetDataWriter data, NetPeer? exclude = null,
    DeliveryMethod method = DeliveryMethod.ReliableOrdered)
{
    foreach (var peer in players.Keys)
        if (peer != exclude)
            peer.Send(data, method);
}
