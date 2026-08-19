# Nightly backlog

Ranked by `Severity(1-5) × Blast radius(1-5)`. Pick from the top next session
unless something newly discovered scores higher. Don't re-litigate an item
without adding a new dated note explaining why.

## Open

### 1. No spatial interest management — every broadcast goes to every peer (12)
**Severity 3 × Blast radius 4.** `PlayerStates` (every tick, 15 Hz),
`TileChanged`, `HarvestProgress`, `StructurePlaced` are all sent via the
world-server's `Broadcast()`, which has no distance filter — every connected
peer on a world-server gets every other player's position every tick,
regardless of range. This is invariant #6 in both `CLAUDE.md` and
`docs/architecture.md`, so it's a documented gap, not a style nit. Invisible
at playtest population; becomes real bandwidth (mobile budget!) as a region
fills up. Fix shape: track each player's last-known chunk, filter
`PlayerStates`/tile/structure broadcasts to peers within
`Tuning.InterestRadiusChunks` (already defined, unused for this purpose).
Touches every `Broadcast()` call site in `apps/world-server/Program.cs` —
plan for a full session, not a quick patch.

### 2. No automated test coverage for world-server or gateway (12)
**Severity 3 × Blast radius 4.** `tests/sim-core.tests` is the only test
project; nothing exercises `Player.cs`, the voyage handshake in
`apps/gateway/Program.cs`, or the message handlers in
`apps/world-server/Program.cs` directly. The voyage ticket race
(`ClaimVoyageAsync` returning false on `rows == 0`) and the disconnect
save-race are exactly the kind of bug that would fail silently without a
test. Needs a project reference from a new test project to `WorldServer.csproj`
+ `Gateway.csproj`, and `Program.cs`'s top-level-statement shape may need
light refactoring (extract handlers into a testable class) before it's
practical to unit test in isolation — scope this as its own session.

### 3. `RequestChunk` has no rate limit or bound (9)
**Severity 3 × Blast radius 3.** Unlike the four economy-affecting requests
(now throttled — see `LOG.md` 2026-08-19), a client can request arbitrary
chunk coordinates at any rate. Each request costs a `TerrainGenerator` regen
plus a full uncompressed chunk packet. Not a dupe/economy exploit — chunks
carry no mutable state beyond diffs already applied via world-server memory —
but it's an uncosted CPU + bandwidth sink a hostile client could lean on.
Natural follow-up to tonight's `ActionThrottle`: either fold `RequestChunk`
into the same throttle (coarser interval, since a real client only requests
chunks as it crosses chunk boundaries) or cap in-flight/pending chunk
requests per peer.

### 4. Fire-and-forget diff/structure/character saves have no retry (9)
**Severity 3 × Blast radius 3.** `WorldStore.SaveDiffAsync`,
`SaveStructureAsync`, and the on-disconnect `SaveCharacterAsync` in
`Program.cs` are all `_ = Task.Run(...)` or unawaited — deliberate, so a slow
write never stalls the tick loop (documented in-code, good reasoning) — but a
failed write today only logs to console and is gone. A transient Postgres
blip during a tree-felling diff write silently un-persists that chop; the
same blip on a disconnect save silently reverts that player's inventory to
whatever the gateway last held. Not a correctness violation of anything a
player can exploit, just quiet data loss on infra hiccups. Fix shape: a small
in-memory retry queue drained on the tick loop, or an outbox table.

### 5. `SurvivalHud.cs` is a 940-line god script (6)
**Severity 2 × Blast radius 3.** Owns HUD chrome, inventory list, crafting
panel, building placement UI, hotbar, and the day/night chip — five-plus
systems in one node. Not urgent (it's organized internally, not spaghetti),
but it's the file that will hurt first when combat UI, trading, or more
crafting tabs land. Consider splitting into per-tab components
(`CraftingPanel`, `BuildingPanel`, `InventoryPanel`) that `SurvivalHud`
composes, once a second UI feature is on deck.

### 6. `CLAUDE.md`'s opening description says "2D pixel top-down"; the client is 3D (3)
**Severity 1 × Blast radius 3.** `docs/architecture.md`'s "Projection: 3D"
section documents a deliberate, shipped pivot to third-person 3D with
physics-based movement and a low-res-render pixel-art look. `CLAUDE.md`
(the standing working agreement, read by every session including this one)
still opens with "mobile-only 2D pixel top-down survival game." Not touched
tonight — `CLAUDE.md` is the human's working agreement, not something to
silently edit overnight — but worth a human pass to reconcile the framing,
since a future session (or agent) taking `CLAUDE.md` literally could argue
against legitimate 3D work.

## Resolved

### 2026-08-19 — Unthrottled economy requests let a client farm resources at wire speed
Was: `Severity 5 × Blast radius 5 = 25` (multiplayor-correctness ≥ 9 →
mandatory). See `LOG.md` for the fix.
