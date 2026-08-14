# Ashfall — nightly engineer log

## 2026-08-14 — AUDIT-ONLY (fix), plus first-night ARCH.md

**Chose:** Server-authoritative `PlayerStates` broadcast now sends each
connected player only the other players within `Tuning.InterestRadiusChunks`
chunks of them (Chebyshev distance), instead of every connected player,
every tick.

**Because:** Audit score. `docs/architecture.md` and `packages/shared-proto/Protocol.cs`
both document interest management as decided — `Protocol.cs` even declares
`Tuning.InterestRadiusChunks` and the `PlayerStates` doc comment says
"Authoritative snapshot of every player in interest range" — but
`apps/world-server/Program.cs`'s tick loop built one shared payload
containing *every* connected player and broadcast it to *everyone*,
unconditionally, at 15 Hz. The constant was declared and never read anywhere
in the codebase. That's a documented invariant silently unmet: per-tick
outbound bandwidth was `O(players²)` instead of `O(players × local density)`,
with no distance cap — every player in a world paid for every other player's
position updates forever, growing quadratically with population. Rated
Severity 4 (behavior directly contradicts the documented contract, and it's
a genuine scaling cliff, not a cosmetic gap) × Blast radius 4 (touches the
core multiplayer loop, every player, every tick, unboundedly) = 16, and
independently ≥9 under the "multiplayer correctness — bandwidth" category
specifically. Per the audit's decision rule, that mandates fixing it tonight
and skipping Phase 2 (the new-feature slot). Full audit notes and everything
*not* fixed tonight are in `BACKLOG.md`; the codebase map used to run the
audit is in `ARCH.md` (both created fresh tonight — first night, no prior
log/backlog/arch existed).

**Changed:**
- `packages/sim-core/World.cs` — added `ChunksWithinInterest(ChunkCoord,
  ChunkCoord, radius)` (pure Chebyshev-distance check) and
  `IsWithinInterest(Vec3 viewer, Vec3 other)` (position → tile → chunk
  convenience wrapper using `Tuning.InterestRadiusChunks`). Sim-shared and
  testable, per the "one shared sim library" invariant — this logic belongs
  next to `ChunkOf`, not duplicated into `Program.cs`.
- `apps/world-server/Program.cs` — the tick loop's `PlayerStates` broadcast
  is now built per-viewer (filtered via `World.IsWithinInterest`) and sent
  directly to that viewer's peer, instead of one shared `Broadcast()` call.
  No wire-format change (same message layout, just a different subset of
  players in it) — no `ProtocolVersion` bump needed.
- `apps/client/scripts/world3d/RemotePlayers.cs` — `Apply` used to only ever
  *add* remote players from a snapshot, relying on an explicit `PlayerLeft`
  message to ever remove one. That was safe when the snapshot always
  contained every connected player; it is not safe now that "missing from
  the snapshot" can mean "walked out of interest range" rather than
  "disconnected." A player who walked away would have stood frozen forever
  as a ghost on clients that could no longer see them update. `Apply` now
  reconciles fully: any tracked body whose id is absent from the current
  snapshot is removed, exactly as if a `PlayerLeft` had arrived for it.
- `tests/sim-core.tests/WorldTests.cs` — added coverage for
  `ChunksWithinInterest` (same-chunk, adjacent, diagonal, just-past-radius,
  far, and negative-coordinate chunks; checked both directions since it must
  be symmetric) and `IsWithinInterest` (one chunk away = in range, ten chunks
  away = not, in world-space metres).

**Risk:** If `Tuning.InterestRadiusChunks` is ever read as tile-radius
instead of chunk-radius somewhere new, or if a future feature needs *every*
player's position regardless of distance (e.g. a world-wide leaderboard/radar
UI), this will silently under-deliver rather than error — there's no
assertion that a consumer actually wants interest filtering. Client-side, if
`RemotePlayers.Apply` is ever called with a partial/filtered list for a
*different* reason than interest range, players will incorrectly disappear.
Neither risk is exercised by any code in the tree today. To spot a problem:
watch for player capsules that never appear on other clients even at close
range (would mean the chunk-distance math is off) or that flicker in/out
right at a chunk boundary while standing still (would mean `TileOf`/`ChunkOf`
rounding needs hysteresis — not implemented tonight, and worth adding if it's
visible in practice: a player exactly on a chunk seam moving a few
centimetres could toggle visibility rapidly).

**Revert:** `git revert 7dff2891b058b9af3209c6e9ce7fed92a344f7b2` — a single,
self-contained commit ("world-server: interest-manage the PlayerStates
broadcast") touching exactly the four code files listed above plus these
three docs.

**Verified:**
- `dotnet test tests/sim-core.tests` — installed .NET 8 and .NET 10 SDKs into
  this session (neither was present) via `apt-get install dotnet-sdk-8.0
  dotnet-sdk-10.0`; ran clean: **91/91 passed**, including the new
  interest-management tests.
- `dotnet build` on `apps/world-server`, `apps/gateway`, and `apps/client` —
  all three build with **0 warnings, 0 errors**. The client build compiles
  against GodotSharp via the project's own `.godot/mono` output, so this
  does exercise the real client assembly, not just a syntax check.
- Live two-client integration test: built a throwaway LiteNetLib harness
  (not checked in — lives in the session scratchpad, see `BACKLOG.md` for
  turning this into a real test project) that speaks the actual wire
  protocol. Ran a real `world-server` process (in-memory mode, no DB) on a
  test port and drove it with two independent clients:
  - Client A connects and is welcomed.
  - Client B connects **late** (after A is already in the world) and is
    welcomed.
  - Both start at spawn: confirmed each sees the other in `PlayerStates`.
  - B walks away in small steps (terrain-height-tracked, speed-budgeted so
    the server's anti-cheat movement check never rejects a step — 0
    corrections recorded) until its chunk is more than
    `InterestRadiusChunks` away from A's spawn chunk: confirmed **neither
    sees the other** any more.
  - B walks back: confirmed **both see each other again** the moment they
    re-enter range.
  - All checks passed on a clean run against the real modified `Program.cs`
    and `World.cs`.

**Not verified:**
- Did not run this inside the actual Godot editor or on a mobile device —
  no Godot binary is available in this environment. The client project
  builds cleanly and `RemotePlayers.cs`'s reconciliation logic is simple
  enough to trust from reading it, but nobody has watched a second player's
  capsule actually appear/disappear on screen as they walk in and out of
  range. **A human should open two Godot client instances against a shared
  local world-server, walk apart, and confirm the other player's capsule
  visibly disappears and reappears (not just teleports oddly) before calling
  this done.**
- Did not test against a live Postgres-backed `WorldStore`/gateway — the
  live integration test ran in the server's in-memory (no `ASHFALL_DB`)
  mode, since this change touches no persistence code. `dotnet build`
  confirms `WorldStore.cs`/`gateway/Program.cs` still compile untouched.
- Did not measure real bandwidth numbers (bytes/player/second before vs.
  after) under load — no way to simulate dozens of concurrent players in
  this session. The fix changes complexity class (`O(n²)` → `O(n × local
  density)`), which is the actual bug; a concrete measurement is a good
  follow-up once there's a realistic multi-player test environment.

**Rejected tonight:** N/A — this was a mandatory Phase 1 fix (audit finding
scored ≥15 / ≥9-multiplayer-correctness), so Phase 2 (new feature) was
skipped per the decision rule rather than any feature idea being rejected on
its merits.

**Added to backlog:** See `BACKLOG.md` — five items, top two scored 9/25:
no automated netcode tests (the two-client harness used tonight should
become a real `tests/world-server.tests` project), and no chunk streaming as
the player roams past the fixed initial radius. Also: terrain-event
broadcasts (`TileChanged`/`HarvestProgress`/`StructurePlaced`) still aren't
interest-filtered (same gap, lower urgency since they're event-driven not
per-tick), the two large client files, no `RequestChunk` rate limit, and no
schema-version field on the stored character inventory.

**Question for the human:** None blocking. (Non-blocking note: this session
had neither the .NET SDKs nor Godot preinstalled — the SDKs were installable
via `apt-get`, but no headless Godot binary was available for an in-editor
or on-device check. If nightly runs are meant to verify client behavior
in-engine, a Godot headless binary in the environment image would close that
gap.)
