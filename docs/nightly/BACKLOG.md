# Nightly backlog

Ranked by `Severity × Blast radius` (each 1–5). Format matches the audit
rubric in the nightly prompt. Pick from the top next session — with a
working `dotnet` toolchain, see 2026-07-24's `LOG.md` entry for why that
matters.

## 1. No interest management on player/inventory broadcasts — 3 × 4 = 12

`apps/world-server/Program.cs`, main tick loop (`PlayerStates` and the
per-player `InventoryUpdate` flush). Every connected client receives every
other player's position every tick and every dirty inventory, unfiltered
by distance/chunk, despite CLAUDE.md invariant #6 ("a client only ever
receives entities/chunks near it") and the `PlayerStates` wire-format
doc comment both claiming interest-range filtering already happens.
`RequestChunk` *does* filter correctly (client asks only for chunks it
wants); this is specifically the per-tick broadcast path.

Not a live bug at current player counts (single digits). Becomes an O(n²)
bandwidth problem past roughly a few dozen concurrent players on one
world-server, which is a mobile-budget concern per CLAUDE.md. Fix shape:
gate the per-player broadcast loop by `Tuning.InterestRadiusChunks` around
each recipient, same as chunk interest already does.

## 2. Synchronous gateway HTTP calls block the tick loop — 3 × 4 = 12

`apps/world-server/Program.cs`, `Hello` and `RequestRelease` handlers call
`gateway.*Async(...).GetAwaiter().GetResult()` directly inside the
single-threaded `NetworkReceiveEvent` callback. A slow or unreachable
gateway stalls every connected player's packet processing for the
duration of one player's join or voyage — not a correctness bug (the
voyage-ticket design prevents duplication/loss), but a shared-latency
cliff. Explicitly a deliberate tradeoff per the existing code comments,
not an oversight — record any decision to change it, don't just revert.
Fix shape: make Hello/RequestRelease async continuations that don't hold
up the packet loop for other peers, e.g. queue the pending join and
complete it on a later tick once the gateway call resolves.

## 3. God scripts: `SurvivalHud.cs` (940 lines), `World3D.cs` (735 lines) — 2 × 2 = 4

Both single Godot nodes doing 5+ distinct jobs (see `ARCH.md` for the
breakdown). Nothing incorrect, just due for splitting into smaller
scenes/components per CLAUDE.md's own Godot guidance ("no god-scenes").
Lowest urgency here — cosmetic/maintainability, not correctness or scale.

## 4. Zero automated coverage outside `sim-core` — 3 × 3 = 9

`WorldConnection.cs`/`Program.cs` wire (de)serialization, the gateway's
HTTP endpoints, and the voyage handshake have no tests — only the pure
`sim-core` rules do. A protocol layout bug would only surface by hand
against a live server. Fix shape: an integration test harness that spins
up a real `NetManager` pair (or two) against the actual message handlers,
not a mock — the sim-core tests are the right model for *rigor*, not
literally the right *shape* for netcode.

## 5. No migration runner — 2 × 3 = 6

`infra/migrations/*.sql` are four numbered, additive files with no
in-repo tool applying them in order or tracking what's run. Fine at this
size; won't stay fine. Fix shape: a minimal runner in `WorldStore`/gateway
startup that tracks applied filenames in a `schema_migrations` table, or
adopt an existing lightweight .NET migration library if the dependency is
justified and logged per CLAUDE.md's dependency rule.

---

## Environment note (not a codebase finding)

Tonight's session (2026-07-24) had no `.NET SDK` installed in the remote
execution container — `dotnet` was not on `PATH` and no install was found
anywhere on the filesystem. This blocked building, running, or testing
*any* C# change, including verifying this list by compiling. Future
nightly sessions: check `dotnet --version` first; if it's missing, treat
the night as audit-only rather than risking an unverifiable commit to a
server-authoritative codebase.
