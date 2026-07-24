# Ashfall — architecture notes (nightly baseline)

Honest map of the codebase as of 2026-07-24, written for future nightly
sessions. This is a snapshot, not a substitute for `docs/architecture.md`
(the decided, canonical doc) — read that first. This file adds the
"what's actually here and what's rough" layer the canonical doc doesn't
carry.

## Scale

~5,600 lines of C# total. Small, early-phase (Phase 3 "persistent
characters and voyages" in progress per README). One person's worth of
code, consistently styled, heavily commented with *why* not *what* —
matches CLAUDE.md's own standard. This is not a sprawling legacy codebase;
treat findings below as "debt to plan," not "mess to firefight."

## Component map

| Path | Runtime | Role |
|---|---|---|
| `apps/client` | net8.0, Godot 4 (C#) | Mobile client. 3D low-res-rendered presentation over a tile-grid simulation (see `docs/architecture.md` "Projection: 3D"). |
| `apps/world-server` | net10.0 | Headless authoritative UDP server (LiteNetLib), one process per region/world. Single-threaded event loop: `NetManager.PollEvents()` + a 15 Hz tick, all on one thread — no locking needed, but also no concurrency. |
| `apps/gateway` | net10.0 | ASP.NET Minimal API. Owns accounts/characters (Postgres), the static world registry, and voyage ticket handshake. |
| `packages/sim-core` | net8.0 | Shared deterministic rules: terrain (`TerrainGenerator`, `Noise.Hash`), movement bounds, harvest/craft/placement/survival/day-night rules. Referenced by both client and world-server — the one invariant everything else hangs off. |
| `packages/shared-proto` | net8.0 | Wire message ids (`MessageId`), `ProtocolVersion`, `Tuning` constants. Every message layout is documented inline on the enum member — read `Protocol.cs` before touching netcode. |
| `infra/migrations` | SQL | Four sequential, additive migrations (`001`–`004`), all `CREATE TABLE IF NOT EXISTS` / `ADD COLUMN IF NOT EXISTS`. No migration *runner* found in-repo (see Debt below) — presumed applied by hand or via `docker-compose` init. |
| `tests/sim-core.tests` | xUnit(-shaped) | Covers determinism, crafting, movement, placement, survival, terrain shape, world clock, item catalog. **Only `sim-core` is tested.** Netcode (`Program.cs`, `WorldConnection.cs`, `Player.cs`, gateway endpoints) has zero automated coverage. |

## Netcode shape (what to know before touching it)

- **Server loop:** `apps/world-server/Program.cs` is a top-level-statements
  single file (468 lines) — connect/disconnect/message handling wired as
  closures over shared local state (`players`, `world`, `store`), plus a
  15 Hz `while` loop that ticks survival, broadcasts `PlayerStates`
  (unreliable), flushes dirty inventories, and heartbeats stats. This is
  the de facto "god file" of the server side, but it's a bootstrap/composition
  file, not deep logic — the actual rules live in `sim-core` and are unit
  tested. Reads more like a wiring diagram than a class that grew unchecked.
- **Client link:** `apps/client/scripts/WorldConnection.cs` mirrors the
  message set 1:1 — one `Send*` method per client→server message, one
  `case` per server→client message, re-exposed as C# events. `World3D.cs`
  and `SurvivalHud.cs` subscribe to those events in `_Ready`/`Bind`, once,
  for the node's lifetime — no leak, no double-subscribe pattern found.
- **Authority model:** client predicts movement locally and reports
  position (`ClientState`, unreliable, rate-limited to `Tuning.ClientStateHz`
  = 15 Hz — *not* per-frame); the server bounds it with
  `MovementRules.Check` against the shared height field
  (`Player.TryAccept`) and only ever trusts its own last-accepted position.
  Everything else (harvest, craft, place, eat) is a request/response pair,
  fully server-decided. This matches CLAUDE.md invariant #1 throughout —
  no client-authoritative logic found for inventory, harvesting, crafting,
  building, or combat (combat doesn't exist yet).
- **Voyages:** three-party handshake (world A → gateway → world B) using a
  single-use ticket row (`voyage_ticket`) with a TTL and an explicit
  self-heal path (`ReclaimExpiredTicketAsync`) if a transfer is abandoned
  mid-flight. Ownership during transit is `NULL`, not "both" or "neither
  validated" — read `003_voyage.sql`'s header comment, it's a good model
  for how this system should be described elsewhere too.

## Known debt (see `BACKLOG.md` for scored, actionable entries)

1. **No interest management on player/inventory broadcasts.** CLAUDE.md
   invariant #6 and the `PlayerStates` wire doc both say "in interest
   range"; the implementation (`Program.cs` tick loop) broadcasts every
   connected player's position and every dirty inventory to every
   connected client, unfiltered by distance or chunk. Harmless at current
   player counts, a real bandwidth/scale problem past a few dozen, and a
   doc/code mismatch regardless of scale. `RequestChunk` *does* have
   proper per-client interest (a client only asks for the chunks it
   wants) — it's specifically the per-tick player/inventory broadcast that
   skips filtering.
2. **Synchronous gateway calls on the server's single tick-loop thread.**
   `Hello` and `RequestRelease` both block on
   `gatewayClient.*Async(...).GetAwaiter().GetResult()` directly inside
   `NetworkReceiveEvent`. This is called out and justified in comments
   ("blocking the loop here is deliberate") — but the consequence is real:
   every other connected player's packets queue behind a slow or
   unreachable gateway call for the duration of any one player's join or
   voyage. Correct today (no corruption/duplication — the ticket claim
   design prevents that), but a latency cliff that gets worse as the
   player count grows.
3. **Two files over the 400-line "god script" line:**
   `SurvivalHud.cs` (940 lines — meters, hotbar, action sheet with 4 tabs,
   travel list, gather prompt, inventory grid, item detail, all in one
   `Control`) and `World3D.cs` (735 lines — camera, HUD wiring, gather
   targeting, tile diff overlay, day/night, structure rendering). Neither
   is doing *wrong* things, they're doing *many* things. Composition into
   smaller scenes/components (per CLAUDE.md's own Godot guidance) is due,
   not urgent.
4. **Zero automated coverage outside `sim-core`.** The wire
   serialization/deserialization in `WorldConnection.cs`/`Program.cs`, the
   gateway's HTTP endpoints, and the voyage handshake have no tests. A
   protocol layout bug (an off-by-one field read, say) would only surface
   at runtime, by hand, against a live server.
5. **No migration runner found.** Four numbered SQL files exist; nothing
   in-repo applies them in order or tracks which have run. Fine at four
   files and one operator; will not stay fine.

None of the above was fixed tonight — see `LOG.md` for why (short version:
this session's environment has no .NET SDK, so nothing here could be
built, run, or tested, and CLAUDE.md's own bar for a commit is
`dotnet test tests/sim-core.tests` passing).
