# Ashfall — nightly backlog

Ranked by Severity(1-5) × Blast radius(1-5). Threshold for "must fix
immediately" per the nightly protocol: any score ≥ 15, or any
multiplayer-correctness finding ≥ 9. Re-score if the game's state has moved
on since a finding was logged.

## 1. No interest management — every broadcast goes to every player, and two data structures grow unboundedly with world age

**Severity 4 × Blast radius 4 = 16 → was mandatory-fix threshold; not fixed
tonight, see note at bottom.**

`docs/architecture.md` invariant #6 states: "a world-server sends each client
only the entities and chunks near them." In the actual code
(`apps/world-server/Program.cs`), this isn't implemented for anything except
chunk data (which the client requests explicitly by coordinate):

- `PlayerStates` (position/yaw for every connected player) is broadcast to
  **every** connected player, every tick, via a single shared `Broadcast()`
  call (`Program.cs:392-406`). No per-recipient distance filtering.
- `TileChanged`, `HarvestProgress`, and the live `StructurePlaced` broadcast
  are also unconditional `Broadcast()` calls (`Program.cs:224,236,294`) —
  every harvest strike and every placement anywhere in the world is sent to
  every connected player regardless of where they are.
- **Worse**, the join backfill sends the *entire* structure history:
  `foreach (var structure in world.Structures) ... peer.Send(...)`
  (`Program.cs:145-150`). `World.Structures` (`World.cs:145`) has never had
  anything removed from it in practice (`RemoveStructure` exists but is never
  called), so this list — and the bytes sent to every joining player — grows
  without bound over a world's lifetime.
- `World.HasWarmthNear` (`World.cs`, called once per player per tick at
  night in the main loop) linearly scans every structure in the world to
  check proximity, and `World.Structures` allocates a fresh array on every
  call. At night, with N players and M structures, that's O(N×M) *at 15 Hz*,
  and M only grows.
- `Tuning.InterestRadiusChunks` is declared in `shared-proto/Protocol.cs` but
  is **never referenced anywhere** — it's dead configuration for a feature
  that was never built.

Why this scores high: it's an explicit, named "load-bearing invariant" in
CLAUDE.md ("breaking any of these needs an explicit decision recorded in
docs/") that was simply never implemented, not a decision anyone made. It
doesn't bite today because the *actual* playable area is tiny (chunk
streaming isn't implemented either — see ARCH.md — so the whole game world
right now is the fixed 5×5-chunk slice built at welcome, and every player is
necessarily "near" every other player and every structure by construction).
But the two hot-path scans (join backfill, per-tick warmth check) already
scale with **world age** (total structures ever placed), independent of
how big the playable area is — that part *is* live debt today, not just a
future one.

**Why not fixed tonight:** the correct fix touches the core netcode path
(`Program.cs`'s broadcast loop) and this session's sandbox has no .NET SDK,
no Docker, and the org egress policy blocks fetching one (confirmed 403 from
`builds.dotnet.microsoft.com`). Shipping a hand-verified-only change to the
one file with zero test coverage, in a repo whose own rule is "`dotnet test`
must pass before any commit," was judged worse than logging it precisely. See
`LOG.md` for the full reasoning.

**Suggested fix shape for whoever picks this up:**
1. Give `Player` a cached `ChunkCoord` (updated in `TryAccept`), and add a
   `World.ChunkOf`-based `WithinInterest(a, b)` check using
   `Tuning.InterestRadiusChunks`.
2. Replace the single-writer `PlayerStates` broadcast with a per-recipient
   filtered list (bandwidth becomes O(players-near-you), not O(all-players)).
3. Filter `TileChanged`/`HarvestProgress`/live `StructurePlaced` broadcasts to
   peers within interest radius of the affected tile.
4. The bigger design question: structures currently have no chunk-scoped
   sync at all (unlike tiles, which ride `GenerateChunk`). The clean fix is
   probably to fold structures into `ChunkData` (sent alongside tile diffs
   when a chunk is generated) instead of a separate always-broadcast
   mechanism — but that's blocked on chunk streaming existing first (see
   ARCH.md: `RequestChunk` is currently never called by the client). Don't
   build structure interest-management on top of a chunk-streaming system
   that doesn't exist yet; do them together or in that order.
5. Add a regression test once `world-server` has a test project (see #2).

## 2. Zero automated test coverage for `world-server` and `gateway`

**Severity 4 × Blast radius 3 = 12.**

`tests/sim-core.tests` is the only test project in the repo. The gateway's
voyage ticket mint/claim/reclaim logic — the exact mechanism
`docs/voyage-transfer.md` says exists so "a crash mid-transfer can never
duplicate items" — has no automated test proving that guarantee. Same for
`Player.cs`'s inventory/craft/eat bookkeeping in `world-server` (thinner
risk, since it mostly delegates to already-tested `sim-core` rules, but the
`ConsumeOne`/`ApplyCraft` invariant-violation throws are untested).

Blocked tonight by the same sandbox issue (no dotnet, so no way to write a
test I could confirm even compiles, let alone passes). Whoever picks this up
next will need an environment with the SDK and (for gateway) a way to spin up
Postgres — `infra/docker/docker-compose.yml` already has the pieces.

## 3. Two client files exceed the 400-line god-script guideline

**Severity 2 × Blast radius 2 = 4.**

`SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines). Both read as
single-purpose orchestrators (one HUD builder, one world/scene builder) with
consistent internal organization, not tangled cross-system logic — lower
real risk than the line count implies — but worth a look if either grows
much further or gets a second contributor.

## 4. Sibling node-path coupling in a few client scripts

**Severity 2 × Blast radius 2 = 4.**

`OrbitCamera.cs:29` (`GetNode<Node3D>("../Player")`), `PlayerBody.cs:46`
(`GetNode<Node3D>("../CameraRig")`), `DebugCapture.cs:40`
(`GetNodeOrNull<Node3D>("../Player")`). Three call sites, one scene tree —
small blast radius. A signal/export-based wiring (as `World3D.HudPath`/
`StatusPath` already do) would remove the coupling if these scripts move.

## 5. No migration-tracking table in `infra/migrations`

**Severity 2 × Blast radius 3 = 6.**

Migrations are sequential `.sql` files with no runner and no "migrations
applied" table recording state per database. Nothing stops a migration being
skipped or re-applied by accident against a real environment. Low urgency
pre-alpha (single dev database), rises fast once there's a shared/staging DB.
