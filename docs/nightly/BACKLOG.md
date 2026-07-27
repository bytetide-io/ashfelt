# Ashfall — nightly backlog

Findings from the audit, ranked by `Severity × Blast radius` (both 1–5),
scored highest first. Anything ≥15 overall, or ≥9 on multiplayer-correctness
alone, is meant to be picked up by the next available night rather than
drift indefinitely. Fixed items move to `LOG.md` and are struck through here,
not deleted, so the scoring history stays visible.

## Fixed

- ~~Character ownership not enforced on normal join (score 25, MP 25)~~ — see
  `LOG.md` 2026-07-27.

## Open

### No interest management — O(n²) player-state broadcast — score 16

`apps/world-server/Program.cs` broadcasts every connected player's position
to every other connected player, every tick (15 Hz), regardless of distance.
`Tuning.InterestRadiusChunks` (`packages/shared-proto/Protocol.cs:154`) is
declared and `docs/architecture.md` invariant #5 calls interest management
"decided," but nothing in the codebase reads that constant — `grep` finds
only the declaration.

- Estimated bandwidth: per-player payload ≈ 2 + 20×N bytes (4-byte id +
  12-byte position + 4-byte yaw). At 15 Hz with 20 players: ≈ (2+400)×15 ≈
  6 KB/s down per client for this message alone, growing linearly per
  player added and quadratically server-wide.
- Severity 4 (no correctness bug at today's player counts, but silently
  violates a stated invariant and will visibly degrade bandwidth/battery as
  population grows — a mobile-specific cost). Blast radius 4 (every
  connected player). Score 16.
- Fix shape: filter `PlayerStates` (and future entity broadcasts) by chunk
  distance using `InterestRadiusChunks`, mirroring how chunk requests are
  already client-pull rather than server-push. Non-trivial: touches the
  world-server's per-tick broadcast loop and needs a spatial index or a
  simple per-player distance scan — worth its own focused night rather than
  a rushed add-on to something else.

### No rate limit on chop/craft/place requests — score 12 (MP-flagged)

`apps/world-server/Program.cs` handles `ChopRequest`/`CraftRequest`/
`PlaceRequest` unconditionally on every reliable message received. Movement
is budgeted by elapsed time (`Player.TryAccept`); these three are not — nothing
on `Player` tracks a per-action cooldown. A modified client can fire requests
as fast as the reliable channel allows (network-RTT-bound, not
human-tap-bound), gathering/crafting/placing at multiples of the intended
rate.

- Severity 3 (economy/balance exploit — still requires holding real
  ingredients for crafting, doesn't break data integrity). Blast radius 4
  (self-exploitable by any player, skews the shared economy for everyone
  else). Score 12.
- Fix shape: a per-action-kind cooldown timestamp on `Player`, checked the
  same way `TryAccept` checks elapsed time for movement. Small, but touches
  the same hot path as tonight's fix — better done on a clean night, not
  stacked on it.

### Hello blocks the tick loop on a synchronous gateway HTTP call — score 12 (MP-flagged)

`apps/world-server/Program.cs`'s Hello handler calls
`gateway.GetCharacterAsync(...).GetAwaiter().GetResult()` (and
`RequestRelease` does the same for voyage calls) inside the single-threaded
tick loop. A slow or degraded gateway stalls `PollEvents` and the tick for
every connected player, not just the one joining. Called out as "deliberate"
in a comment, but the tradeoff has no timeout bound today.

- Severity 3 (visible multiplayer stutter/freeze, not data corruption).
  Blast radius 4 (every player on the world-server, triggered by any one
  join). Score 12.
- Fix shape: bound the gateway call with a timeout and treat a timeout as a
  load failure (same path as any other gateway error today), or move the
  Hello handshake off the tick thread entirely. The latter is a bigger
  change — needs its own design pass on what "the tick loop only ever
  touches `players`/`world`" would have to give up.

### `World3D.cs` god-script — score 12

735 lines: terrain/foliage mesh generation, day/night sky, touch input and
tap-to-gather targeting, structure rendering, and network event wiring all
in one `Node3D`. Already scoped as planned work in
`docs/gameplay-roadmap.md` §3.4 (split by responsibility: net dispatch,
terrain streaming, local player, remote players, interaction/targeting — a
message-dispatch table keyed by `MessageId` instead of a growing `switch`).
Not new information; scoring it here is so it competes fairly for a night's
attention instead of sitting in a roadmap doc indefinitely.

- Severity 3 (maintainability/regression risk, not a live bug). Blast
  radius 4 (touched by nearly every future client change). Score 12.

### No test project for world-server or gateway — score 12

Only `tests/sim-core.tests` exists. The riskiest logic — the Hello
ownership/ticket flow (exactly the bug fixed tonight), reach/rate checks,
`WorldStore` diff replay, and the gateway's voyage mint/claim/reclaim state
machine — has zero automated coverage. `sim-core` is well tested (84 tests);
the netcode and persistence glue that actually exercises those rules under
concurrency is not.

- Severity 3 (no failure today, but materially raises the odds a bug like
  tonight's ships or regresses unnoticed). Blast radius 4 (covers the entire
  netcode/persistence surface). Score 12.
- Fix shape: needs test infrastructure first, not just test files — a
  `WebApplicationFactory`-style harness for the gateway (in-process ASP.NET
  host + a real or embedded Postgres) is the natural starting point since
  it's where tonight's bug lived. Adding a DB-testing dependency
  (Testcontainers or similar) is itself a decision to record per the "no new
  dependency without justification" rule — do that explicitly on the night
  that picks this up, don't sneak it in as a side effect of something else.

### Fire-and-forget persistence writes swallow errors silently — score 9

`apps/world-server/Program.cs` fires `store.SaveDiffAsync(...)` (harvest) and
`store.SaveStructureAsync(...)` (placement) without awaiting, without
`try/catch`, and without logging — unlike the disconnect handler a few lines
above, which wraps its fire-and-forget gateway save in `Task.Run` +
`try/catch` + `Console.Error.WriteLine`. A transient Postgres hiccup means a
felled tree or placed wall stays authoritative only in memory and silently
vanishes on the next restart.

- Severity 3 (silent data loss on transient DB errors, not routine). Blast
  radius 3 (per-world, per-incident, not player-visible in real time).
  Score 9.
- Fix shape: match the existing disconnect-handler pattern —
  `Task.Run` + `try/catch` + `Console.Error.WriteLine`. Small and
  mechanical; a reasonable "one thing" for a lighter night.

### `SurvivalHud.cs` god-script — score 6

940 lines: meters, hotbar, crafting-card list, placement cards, travel menu
(network fetch + rendering), gather-prompt UI, all in one `Control`. Same
category as `World3D.cs` but lower blast radius — UI-only, doesn't touch
server or `sim-core`, and is already reasonably isolated behind `Bind()`/
events internally.

- Severity 2, blast radius 3. Score 6.

### `UpdateGatherPrompt` runs an O(radius²) tile scan every physics frame — score 2

`apps/client/scripts/world3d/World3D.cs`'s `_PhysicsProcess` (typically
60 Hz) scans `(2·radius+1)²` tiles via dictionary lookups unconditionally,
even when the player hasn't moved. Cheap today given `ChopRangeMetres` and
tile size; a candidate for "only recompute when the player's tile changes"
if either grows.

- Severity 1, blast radius 2. Score 2. Not worth a dedicated night; fold
  into whatever night eventually touches `World3D.cs`'s decomposition.
