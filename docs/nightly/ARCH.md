# Ashfall — architecture map (nightly notes)

Honest snapshot as of 2026-08-22, written by the first nightly session. This is
a supplement to `docs/architecture.md` (the decided, load-bearing invariants)
and `docs/gameplay-roadmap.md` (planned work) — read those first; this file is
"what's actually there and what's fragile," refreshed as nightly sessions learn
more. Don't duplicate the invariants doc here; link to it.

## Layout (~5,600 lines of C#, excluding tests)

```
apps/client        Godot 4 mobile client (C#, net8.0)
  scripts/
    WorldConnection.cs   421  UDP link to one world-server; owns the wire protocol
    DebugCapture.cs       89  dev-only screenshot/log capture
    ui/
      DesignSystem.cs    252  Ink/Ember palette, fonts, chunky pixel controls — single
                               source of visual style, everything else reads tokens from it
      PixelIcons.cs      147  bakes DesignSystem pixel grids to textures at runtime
      TitleScreen.cs     121  boot scene
      StripedBar.cs       40  shared meter widget
    world3d/
      World3D.cs         735  god-script: net dispatch + terrain streaming + interaction
      SurvivalHud.cs     940  god-script: every HUD panel (meters, hotbar, craft, build,
                               items, travel) in one file
      TerrainMesher.cs   126  builds the 3D mesh from sim-core heights
      PixelTextures.cs    76  procedural nearest-filtered surface textures
      PlayerBody.cs        97  local player physics + input
      RemotePlayers.cs     69  other players' capsules
      OrbitCamera.cs       59
      VirtualJoystick.cs  104
      CrtOverlay.cs        88

apps/world-server   Headless authoritative server (C#, net10.0)
  Program.cs         468  top-level statements: connection handling, tick loop, all
                           message handlers in one switch
  Player.cs          220  per-connection state: position, inventory, survival, movement accept
  WorldStore.cs      151  Postgres persistence — diffs and structures only, never chunks
  GatewayClient.cs    73  HTTP client to the gateway's character/voyage REST API

apps/gateway        Accounts, character storage, world routing (C#, net10.0, ASP.NET minimal API)
  Program.cs         210+ /health, /worlds, /characters/{id} GET+PUT, /voyage, /voyage/claim

packages/sim-core    Shared deterministic simulation (C#, net8.0) — referenced by both
                     client and world-server, never duplicated across the boundary
  TerrainGenerator.cs, Noise.cs, World.cs, MovementRules.cs, HarvestRules.cs,
  CraftingRules.cs, PlacementRules.cs, SurvivalRules.cs, WorldClock.cs, ItemCatalog.cs

packages/shared-proto  Wire message enum, ItemId enum, Tuning constants, CharacterState DTO

infra/migrations     4 additive SQL migrations, applied via docker-entrypoint-initdb.d
infra/docker         compose file: postgres + world-server only (gateway runs via `dotnet run`,
                     not yet containerized)
```

## What's solid

- The client-predicts/server-validates movement split (`MovementRules` in
  sim-core, invariant-driven) is genuinely shared, not reimplemented per side.
- Harvest/craft/place/eat are all server-authorised against sim-core rules
  before the inventory changes; the client never mutates its own state as fact.
- The voyage handshake (`docs/voyage-transfer.md`, gateway `/voyage` +
  `/voyage/claim`) correctly makes the gateway the single source of truth for
  character ownership, with a ticket TTL that self-heals a crash mid-transfer
  without operator intervention. This is unusually careful for a pre-alpha.
- World storage really is seed + diffs — `WorldStore` never writes a chunk,
  only `tile_diff`/`structure` rows, replayed on boot.
- Fire-and-forget saves (harvest diffs, structure placement, leave-save) are
  deliberately kept off the packet-processing path so one slow write never
  stalls every other player in the world — documented inline where it happens.

## Known debt (see `BACKLOG.md` for scored, ranked entries)

- **God-scripts**: `SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines)
  are both already named in `docs/gameplay-roadmap.md` §3.4 as needing a split;
  both have grown since that section was written (was 419 / 616 lines).
- **No interest management yet**: `PlayerStates` and `StatsUpdate` broadcast to
  *every* connected player every tick/heartbeat, not just nearby ones — this is
  invariant #6 on paper but not in `Program.cs` today. Tracked as planned work
  in roadmap §3.3 (entity system to carry it), not a surprise, but worth
  remembering it's still open before player counts grow.
- **No per-action rate limiting** on `ChopRequest`/`CraftRequest`/`PlaceRequest`
  beyond the reach check — see `BACKLOG.md`.
- **Migrations are apply-once**: `infra/migrations/*.sql` only run automatically
  via `docker-entrypoint-initdb.d`, which Postgres only executes against an
  *empty* data volume. An already-running deployment needs someone to apply
  new `.sql` files by hand; there's no migration-runner or `schema_version`
  table. The migrations themselves are safely idempotent (`IF NOT EXISTS`,
  `ADD COLUMN ... DEFAULT`), so this is an operational gap, not a data-loss risk.
- **`CharacterState` has no version field.** So far every change has been an
  additive optional meter with a safe default (warmth), so it hasn't bitten
  anyone yet, but there's no guard against a future breaking change.

## Trust boundaries (as of tonight)

- Client → world-server: UDP, gated by a connect key (`ASHFALL_CONNECT_KEY`,
  default `"ashfall"`) and a protocol version check. Everything the client
  sends is treated as a request, validated against sim-core rules.
- World-server → gateway: HTTP, now gated by a shared key
  (`ASHFALL_GATEWAY_KEY`, default `"ashfall"`) added tonight — see
  `LOG.md` 2026-08-22. Before tonight this was **open**: any caller that could
  reach the gateway's port and knew a character UUID (which every player
  already knows — it's their own device-stored id) could set that character's
  inventory and survival meters directly via `PUT /characters/{id}`, with zero
  validation. This was the most serious thing found tonight.
- Both default keys are the literal string `"ashfall"`, matching the existing
  convention for the client connect key. That default is public (it's in this
  open-source repo) and provides no protection by itself — it only stops an
  *unmodified* client/curl call from working by accident, and gives operators
  a single env var to set before exposing either service beyond localhost.
  Rotating to a real secret in any non-local deployment is an operator
  responsibility this repo doesn't yet enforce or even warn loudly about at
  the *world-server* end (the gateway does warn on startup with the default).

## Build/test environment note

This sandbox has **no .NET SDK installed** (`dotnet` is not on `PATH`, no
`/usr/share/dotnet`). No nightly session running in this environment can
currently run `dotnet build`, `dotnet test`, or launch the gateway/world-server
to verify changes end-to-end. Every code change made under this constraint
must say so explicitly in `LOG.md` and be reviewed/built by a human before
being trusted. This is itself worth fixing (see `BACKLOG.md`) — a nightly
session that can't build is flying blind.
