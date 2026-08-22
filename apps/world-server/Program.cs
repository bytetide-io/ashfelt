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
string gatewayUrl = Environment.GetEnvironmentVariable("ASHFALL_GATEWAY") ?? "http://127.0.0.1:5041";
string gatewayKey = Environment.GetEnvironmentVariable("ASHFALL_GATEWAY_KEY") ?? "ashfall";

var gateway = new GatewayClient(gatewayUrl, gatewayKey);
var world = new World(seed);
await using var store = await WorldStore.OpenAsync(conn, worldId, seed);
int restored = await store.LoadDiffsAsync(world);
int structures = await store.LoadStructuresAsync(world);
Console.WriteLine($"[world] seed={seed} restored {restored} diff(s), {structures} structure(s)");

// Build sites (committed blueprints) are player-caused state, replayed like
// diffs and structures. The highest stored id fixes where new ids resume so a
// client never sees an id reused across a restart.
var buildSites = new Dictionary<long, BuildSite>();
long nextSiteId = 1;
foreach (var loadedSite in await store.LoadBuildSitesAsync())
{
    buildSites[loadedSite.Id] = loadedSite;
    if (loadedSite.Id >= nextSiteId) nextSiteId = loadedSite.Id + 1;
}
Console.WriteLine($"[world] restored {buildSites.Count} build site(s)");

// Reads live terrain, structures and built walls to bound where a player may
// move — the authority behind the client's own collision.
var obstacles = new WorldObstacles(world, buildSites);

var players = new Dictionary<NetPeer, Player>();
var writer = new NetDataWriter();
int nextPlayerId = 1;
long tick = 0;

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

    // Save the character back to the gateway off the packet loop so a slow
    // write never stalls the players still in the world. The state is snapshot
    // now, synchronously, so the async write sees a stable copy.
    if (player.CharacterId != Guid.Empty)
    {
        var characterId = player.CharacterId;
        var snapshot = player.ToCharacterState();
        _ = Task.Run(async () =>
        {
            try { await gateway.SaveCharacterAsync(characterId, snapshot); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[world] character save failed for {characterId}: {ex.Message}");
            }
        });
    }

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

            var uuidBytes = new byte[16];
            reader.GetBytes(uuidBytes, 16);
            var characterId = new Guid(uuidBytes);

            // A second live connection presenting the same character UUID — same
            // world twice, most simply a double-connect during a flaky reconnect —
            // would otherwise load a second, independent in-memory copy of one
            // inventory: two Player objects, each free to spend the same starting
            // items. Reject outright rather than risk two sessions mutating one
            // character.
            if (players.Values.Any(p => p != player && p.CharacterId == characterId))
            {
                Console.WriteLine($"[world] rejecting duplicate connection for character {characterId}");
                peer.Disconnect();
                break;
            }
            player.CharacterId = characterId;

            string ticketText = reader.GetString();

            // A voyage arrival carries a ticket: this server must claim ownership
            // before it may load the character. A failed claim (expired, forged,
            // already used) means the character is not ours to load — reject the
            // join rather than risk two worlds owning one character. A normal join
            // has an empty ticket and claims below instead.
            if (ticketText.Length > 0)
            {
                bool claimed = Guid.TryParse(ticketText, out var ticket)
                    && gateway.ClaimVoyageAsync(player.CharacterId, ticket, worldId)
                        .GetAwaiter().GetResult();
                if (!claimed)
                {
                    Console.WriteLine($"[world] rejecting voyage for {player.CharacterId}: bad ticket");
                    peer.Disconnect();
                    break;
                }
                Console.WriteLine($"[world] claimed voyaging character {player.CharacterId}");
            }

            // Load happens at Hello (not connect) because the UUID is only known
            // now. Blocking the loop here is deliberate: joining is inherently a
            // wait, and seeding synchronously keeps the inventory the tick loop
            // reads free of cross-thread mutation. Saves, by contrast, are
            // fire-and-forget so a leaving player never stalls the others.
            //
            // Claiming (not a plain load) is what makes this world the character's
            // sole owner for the session: a voyage arrival is already owned by us
            // from the ticket claim above and this call is then a same-world
            // no-op; a normal join claims for the first time. Either way, a
            // denied or failed claim must not let the player in — proceeding with
            // default state on a transient gateway error would silently reset (and
            // then overwrite-on-save) whatever the gateway actually holds.
            CharacterState? character;
            try
            {
                character = gateway.ClaimCharacterAsync(player.CharacterId, worldId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[world] character claim failed for {player.CharacterId}: {ex.Message}");
                peer.Disconnect();
                break;
            }
            if (character is null)
            {
                Console.WriteLine($"[world] rejecting join for {player.CharacterId}: owned by another world");
                peer.Disconnect();
                break;
            }
            player.LoadCharacter(character);
            Console.WriteLine($"[world] player {player.Id} character {player.CharacterId} claimed");

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

            // Backfill every tile diff already recorded — trees felled, rocks
            // broken, shrubs stripped — before this player connected. Without this
            // a joining client renders those nodes as pristine forever: the server
            // holds the diffed tile and silently refuses to re-harvest it, so the
            // node just never falls no matter how many times it's tapped.
            foreach (var diff in world.Diffs)
            {
                writer.Reset();
                writer.Put((byte)MessageId.TileChanged);
                writer.Put(diff.Key.X);
                writer.Put(diff.Key.Y);
                writer.Put((byte)diff.Value);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }

            // Backfill the already-built world so a joining player sees every
            // structure, not just the ones placed after they arrived.
            foreach (var structure in world.Structures)
            {
                writer.Reset();
                WriteStructurePlaced(writer, structure);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }

            // Backfill build sites: everyone sees the *built* pieces of every site
            // (public world state), but only the owner receives the full blueprint
            // — its pending hologram and its stockpile are private to them.
            foreach (var site in buildSites.Values)
            {
                if (site.Owner == player.CharacterId)
                {
                    SendBlueprintState(player, site);
                }
                else
                {
                    foreach (var piece in site.BuiltPieces)
                    {
                        writer.Reset();
                        WriteBuiltPiece(writer, site.Id, piece);
                        peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
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
            var rejection = player.TryAccept(world.Terrain, reported, yaw, Now(), obstacles);
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

            // A tool matching the node speeds the gather: resolve the node's
            // preferred class, then how good a tool of that class the player holds.
            var preferredTool = HarvestRules.PreferredTool(world.TileAt(tx, ty));
            int toolTier = player.ToolTierFor(preferredTool);
            var strike = world.TryHarvest(tx, ty, preferredTool, toolTier);
            if (!strike.Allowed) break;

            // Every strike drops resource; a tree just takes several before it
            // falls. Marking the inventory dirty lets the count tick up per hit.
            player.Give(strike.Item, strike.Amount);

            if (strike.Felled)
            {
                // Fire-and-forget the write: the diff is already authoritative in
                // memory, and stalling the packet loop on the database would stall
                // every other player.
                _ = store.SaveDiffAsync(tx, ty, strike.Becomes);

                writer.Reset();
                writer.Put((byte)MessageId.TileChanged);
                writer.Put(tx);
                writer.Put(ty);
                writer.Put((byte)strike.Becomes);
                Broadcast(writer);
            }
            else
            {
                // Not felled yet: tell everyone how worn the node is so it visibly
                // wears down under the strikes. This is transient — never persisted.
                writer.Reset();
                writer.Put((byte)MessageId.HarvestProgress);
                writer.Put(tx);
                writer.Put(ty);
                writer.Put((byte)strike.StrikesLeft);
                writer.Put((byte)strike.StrikesTotal);
                Broadcast(writer);
            }

            Console.WriteLine($"[world] player {player.Id} struck {strike.Item} at ({tx},{ty}) " +
                              (strike.Felled ? "(felled)" : $"({strike.StrikesLeft} left)"));
            break;
        }

        case MessageId.CraftRequest:
        {
            var output = (ItemId)reader.GetByte();
            var craft = CraftingRules.Evaluate(player.Inventory, output);
            if (!craft.Allowed) break;

            player.ApplyCraft(craft.Deltas);
            Console.WriteLine($"[world] player {player.Id} crafted {output}");
            break;
        }

        case MessageId.EatRequest:
        {
            var food = (ItemId)reader.GetByte();
            if (!player.Eat(food)) break;

            // Echo the restored meters at once so hunger jumps on the bite rather
            // than waiting for the next heartbeat; the heartbeat still self-heals a
            // dropped echo.
            SendStats(player, tick);
            Console.WriteLine($"[world] player {player.Id} ate {food}");
            break;
        }

        case MessageId.PlaceRequest:
        {
            var kind = (ItemId)reader.GetByte();
            int tx = reader.GetInt(), ty = reader.GetInt();

            if (!player.Has(kind))
            {
                Console.WriteLine($"[world] player {player.Id} lacks {kind} to place");
                break;
            }
            if (!player.IsWithinReach(tx, ty))
            {
                Console.WriteLine($"[world] player {player.Id} place out of range at ({tx},{ty})");
                break;
            }

            var placement = world.TryPlace(tx, ty, kind);
            if (!placement.Allowed) break;

            player.ConsumeOne(kind);
            // Fire-and-forget the write: the structure is already authoritative
            // in memory, exactly as with harvest diffs.
            _ = store.SaveStructureAsync(placement.Structure);

            writer.Reset();
            WriteStructurePlaced(writer, placement.Structure);
            Broadcast(writer);

            Console.WriteLine($"[world] player {player.Id} placed {kind} at ({tx},{ty})");
            break;
        }

        case MessageId.CommitBlueprint:
        {
            int count = reader.GetUShort();
            var pieces = new List<PlannedPiece>(count);
            for (int i = 0; i < count; i++)
            {
                var kind = (BuildPieceKind)reader.GetByte();
                var material = (BuildMaterial)reader.GetByte();
                int px = reader.GetInt(), py = reader.GetInt(), level = reader.GetInt();
                var layer = (PieceLayer)reader.GetByte();
                pieces.Add(new PlannedPiece(kind, material, PieceSlot.Canonical(px, py, level, layer)));
            }
            if (pieces.Count == 0) break;

            // The client predicts validity, but the server is the authority: an
            // illegal plan (unsupported piece, bad layer, aliased slot, off
            // buildable ground) is refused outright rather than half-built.
            var validation = BuildingRules.Validate(pieces, GroundBuildable);
            if (!validation.Ok)
            {
                Console.WriteLine($"[world] player {player.Id} committed an invalid blueprint " +
                                  $"({validation.Problems.Count} problem(s))");
                break;
            }

            var site = new BuildSite(nextSiteId++, player.CharacterId, pieces);
            buildSites[site.Id] = site;
            _ = store.SaveBlueprintAsync(site);

            SendBlueprintState(player, site);
            Console.WriteLine($"[world] player {player.Id} committed blueprint {site.Id} ({site.PieceCount} pieces)");
            break;
        }

        case MessageId.DepositRequest:
        {
            long siteId = reader.GetLong();
            var item = (ItemId)reader.GetByte();
            int amount = reader.GetInt();

            if (!buildSites.TryGetValue(siteId, out var site) || site.Owner != player.CharacterId) break;

            // Take only what the site needs and only what the player holds; the
            // site refuses the rest, so nothing is lost from the player's stack.
            int held = player.Inventory.GetValueOrDefault(item);
            int taken = site.Deposit(item, Math.Min(amount, held));
            if (taken <= 0) break;

            player.Take(item, taken);
            _ = store.SaveStorageAsync(siteId, item, site.Storage.GetValueOrDefault(item));
            SendBlueprintState(player, site);
            Console.WriteLine($"[world] player {player.Id} deposited {taken} {item} into site {siteId}");
            break;
        }

        case MessageId.BuildRequest:
        {
            long siteId = reader.GetLong();
            if (!buildSites.TryGetValue(siteId, out var site) || site.Owner != player.CharacterId) break;

            var next = site.PeekNextBuildable(GroundBuildable);
            if (next is not { } target) break;
            if (!player.IsWithinReach(target.Slot.X, target.Slot.Y))
            {
                Console.WriteLine($"[world] player {player.Id} build out of range at ({target.Slot.X},{target.Slot.Y})");
                break;
            }

            var strike = site.TryBuildAt(target.Slot, GroundBuildable);
            if (!strike.Acted) break;

            if (strike.Completed)
            {
                _ = store.SavePieceBuiltAsync(siteId, target.Slot);
                // Materials were consumed on the completing strike: persist the
                // stockpile lines the piece drew from.
                if (StructureCatalog.TryGet(target.Kind, target.Material, out var def))
                    foreach (var line in def.Cost)
                        _ = store.SaveStorageAsync(siteId, line.Item, site.Storage.GetValueOrDefault(line.Item));

                // A completed piece is public: everyone sees it appear.
                writer.Reset();
                WriteBuildProgress(writer, siteId, strike);
                Broadcast(writer);
                SendBlueprintState(player, site);
                Console.WriteLine($"[world] player {player.Id} built {target.Kind} on site {siteId}" +
                                  (site.IsComplete ? " (complete)" : ""));
            }
            else
            {
                // A partial strike wears an unbuilt piece up — private to the owner,
                // since only they can see the pending hologram.
                writer.Reset();
                WriteBuildProgress(writer, siteId, strike);
                player.Peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
            break;
        }

        case MessageId.CancelBlueprint:
        {
            long siteId = reader.GetLong();
            if (!buildSites.TryGetValue(siteId, out var site) || site.Owner != player.CharacterId) break;

            // Tear it down: the stockpile returns to the owner, and every piece —
            // built or pending — is removed from the shared world.
            foreach (var (item, amount) in site.Storage) player.Give(item, amount);
            buildSites.Remove(siteId);
            _ = store.DeleteBlueprintAsync(siteId);

            writer.Reset();
            writer.Put((byte)MessageId.BuildSiteRemoved);
            writer.Put(siteId);
            Broadcast(writer);
            Console.WriteLine($"[world] player {player.Id} cancelled site {siteId}");
            break;
        }

        case MessageId.RequestRelease:
        {
            string targetWorldId = reader.GetString();

            void Deny(string reason)
            {
                Console.WriteLine($"[world] release denied for player {player.Id}: {reason}");
                writer.Reset();
                writer.Put((byte)MessageId.ReleaseDenied);
                writer.Put(reason);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }

            if (player.CharacterId == Guid.Empty) { Deny("no character"); break; }

            // Save synchronously first: the gateway must hold the authoritative
            // state before any world can claim it, or a voyage could load stale
            // inventory. Only then mint the ticket. Blocking the loop is deliberate
            // — a release is a rare, deliberate act, not a per-tick path.
            VoyageGrant? grant;
            try
            {
                gateway.SaveCharacterAsync(player.CharacterId, player.ToCharacterState())
                    .GetAwaiter().GetResult();
                grant = gateway.RequestVoyageAsync(player.CharacterId, worldId, targetWorldId)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Deny($"gateway unreachable: {ex.Message}");
                break;
            }

            if (grant is null) { Deny($"target '{targetWorldId}' unavailable"); break; }

            // Grant, then relinquish: tell the client where to go, then remove the
            // entity so this server no longer owns it. The gateway already cleared
            // ownership to in-transit, so the two states never contradict.
            writer.Reset();
            writer.Put((byte)MessageId.ReleaseGranted);
            writer.Put(grant.TargetHost);
            writer.Put(grant.TargetPort);
            writer.Put(grant.Ticket.ToString());
            peer.Send(writer, DeliveryMethod.ReliableOrdered);

            players.Remove(peer);
            Console.WriteLine($"[world] released player {player.Id} ({player.CharacterId}) " +
                              $"to {targetWorldId} at {grant.TargetHost}:{grant.TargetPort}");

            writer.Reset();
            writer.Put((byte)MessageId.PlayerLeft);
            writer.Put(player.Id);
            Broadcast(writer, exclude: peer);
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

    tick++;
    // A player is warm in daylight, or at night while sheltering within a
    // campfire's warmth radius. Exposed at night, warmth drains and — once
    // empty — health, so night is a real threat and the fire earns its keep.
    bool daytime = WorldClock.IsDaytime(WorldClock.TimeOfDay(tick));
    foreach (var player in players.Values)
    {
        // Warm in daylight, by a lit campfire, or under the roof of a built
        // shelter — so raising a roof is a real answer to the night, not decor.
        bool warm = daytime || world.HasWarmthNear(player.Position) || ShelteredAt(player.Position);
        player.AdvanceSurvival(1, warm);
    }

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

        if (tick % Tuning.StatsHeartbeatTicks == 0)
            foreach (var player in players.Values) SendStats(player, tick);
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

// The survival meters plus time-of-day for one player. Sent both on the slow
// heartbeat and immediately after an action that changes the meters (eating),
// so the client never waits a full beat to see the effect of what it just did.
void SendStats(Player player, long atTick)
{
    var survival = player.Survival;
    writer.Reset();
    writer.Put((byte)MessageId.StatsUpdate);
    writer.Put(survival.HungerPoints);
    writer.Put(survival.StaminaPoints);
    writer.Put(survival.HealthPoints);
    writer.Put(survival.WarmthPoints);
    writer.Put((float)WorldClock.TimeOfDay(atTick));
    // Unreliable: a dropped stats packet is replaced by the next heartbeat, and
    // the client interpolates time-of-day locally between beats.
    player.Peer.Send(writer, DeliveryMethod.Unreliable);
}

static void WriteStructurePlaced(NetDataWriter data, Structure structure)
{
    data.Put((byte)MessageId.StructurePlaced);
    data.Put(structure.Id);
    data.Put((byte)structure.Kind);
    data.Put(structure.TileX);
    data.Put(structure.TileY);
}

// Whether the terrain under a cell can carry a foundation — the same walkability
// gate the legacy placement path uses, so a building rests where a wall could.
bool GroundBuildable(int x, int y) => TerrainGenerator.IsWalkable(world.TileAt(x, y));

// Whether a built roof shelters the player's cell — the survival payoff of
// finishing a building, checked each tick alongside the campfire warmth radius.
bool ShelteredAt(Vec3 position)
{
    double metres = TerrainGenerator.TileMetres;
    int cellX = (int)Math.Floor(position.X / metres);
    int cellY = (int)Math.Floor(position.Z / metres);
    foreach (var site in buildSites.Values)
        if (site.HasBuiltRoofOver(cellX, cellY)) return true;
    return false;
}

// The whole of one build site, sent only to its owner: the pending hologram, the
// built pieces, and the on-site stockpile. The client rebuilds its private view
// from this, so it is authoritative and idempotent.
void SendBlueprintState(Player owner, BuildSite site)
{
    writer.Reset();
    writer.Put((byte)MessageId.BlueprintState);
    writer.Put(site.Id);
    writer.Put((ushort)site.PieceCount);
    foreach (var piece in site.Pieces)
    {
        writer.Put((byte)piece.Kind);
        writer.Put((byte)piece.Material);
        writer.Put(piece.Slot.X);
        writer.Put(piece.Slot.Y);
        writer.Put(piece.Slot.Level);
        writer.Put((byte)piece.Slot.Layer);
        writer.Put((byte)(site.IsBuilt(piece.Slot) ? 1 : 0));
    }
    writer.Put((byte)site.Storage.Count);
    foreach (var (item, amount) in site.Storage)
    {
        writer.Put((byte)item);
        writer.Put(amount);
    }
    owner.Peer.Send(writer, DeliveryMethod.ReliableOrdered);
}

static void WriteBuildProgress(NetDataWriter data, long siteId, BuildSite.BuildStrike strike)
{
    data.Put((byte)MessageId.BuildProgress);
    data.Put(siteId);
    data.Put((byte)strike.Piece.Kind);
    data.Put((byte)strike.Piece.Material);
    data.Put(strike.Piece.Slot.X);
    data.Put(strike.Piece.Slot.Y);
    data.Put(strike.Piece.Slot.Level);
    data.Put((byte)strike.Piece.Slot.Layer);
    data.Put((byte)strike.StrikesLeft);
    data.Put((byte)strike.StrikesTotal);
    data.Put((byte)(strike.Completed ? 1 : 0));
}

// A built piece as a completed-strike message, for backfilling a joining player
// who is not the owner: they learn only of pieces that already stand.
static void WriteBuiltPiece(NetDataWriter data, long siteId, PlannedPiece piece)
{
    int total = BuildingRules.HitsToBuild(piece.Kind, piece.Material);
    data.Put((byte)MessageId.BuildProgress);
    data.Put(siteId);
    data.Put((byte)piece.Kind);
    data.Put((byte)piece.Material);
    data.Put(piece.Slot.X);
    data.Put(piece.Slot.Y);
    data.Put(piece.Slot.Level);
    data.Put((byte)piece.Slot.Layer);
    data.Put((byte)0);
    data.Put((byte)total);
    data.Put((byte)1);
}
