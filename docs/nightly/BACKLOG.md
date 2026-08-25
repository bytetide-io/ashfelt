# Ashfall — nightly backlog

Ranked findings not yet built. Score = Severity(1-5) × Blast radius(1-5) per
the nightly-engineer audit rule. Anything ≥15, or ≥9 on a multiplayer-
correctness finding, is meant to be fixed the night it's found (or the next
night that *can* fix it — see #1's note).

| # | Finding | Severity | Blast | Score | Category |
|---|---|---|---|---|---|
| 1 | `apps/world-server` and `apps/gateway` have no automated tests | 3 | 5 | **15** | testing / multiplayer-correctness |
| 2 | Gateway voyage/character routes untested against a real Postgres | 3 | 4 | 12 | testing / persistence |
| 3 | Hello handshake blocks the tick loop on a synchronous gateway round-trip | 3 | 4 | 12 | multiplayer-correctness (documented tradeoff, see ARCH.md) |
| 4 | `World3D.cs` (735 lines) mixes terrain/foliage build, sky/clock, tap-to-gather input, and structure rendering | 2 | 3 | 6 | architecture / maintainability |
| 5 | `SurvivalHud.cs` (940 lines) builds every HUD surface (meters, hotbar, craft/build/travel tabs) procedurally in one Control | 2 | 2 | 4 | architecture / maintainability |
| 6 | No mobile perf numbers on record (frame time, draw calls, texture memory) | 2 | 3 | 6 | mobile performance (unverified, not a known bug) |
| 7 | `structure.owner_id` / `structure.health` columns unused by any code path | 1 | 2 | 2 | dead schema |

## #1 detail — why it's #1 but wasn't fixed tonight

This is the clearest ≥15 finding in the codebase — everything in `sim-core`
has a test, and the code one layer up that turns those rules into
authoritative multiplayer state (`Player.cs`'s inventory/craft/eat/move
invariants) has none. It's also low-risk to fix: `Player.cs` is pure logic,
additive test-only change, no behavior touched.

**Not fixed tonight because this sandbox has no working .NET SDK** (see
`ARCH.md` → Toolchain note) — `dotnet` isn't on `PATH`, and neither `apt`
nor `dotnet-install.sh` could reach a package mirror through the proxy.
`CLAUDE.md` requires `dotnet test tests/sim-core.tests` to pass before any
commit, and this session cannot run that command, so it made no `.cs`
changes rather than commit unverified code. **First night with a working
toolchain: add `tests/world-server.tests`** (xunit, same shape as
`SimCore.Tests.csproj`, referencing `WorldServer.csproj`) covering at least:

- `Player.TryAccept` — accepts a legal move, rejects each `MoveRejection`
  case, and confirms `LastAcceptedAt` only advances on acceptance (the
  anti-spam budget).
- `Player.Give` / `ConsumeOne` / `ApplyCraft` — normal paths, and that the
  documented fail-loud invariant (`InvalidOperationException` on an
  impossible negative) actually throws.
- `Player.Eat` — restores hunger via the catalog food value, returns
  `false` (no state change) for a non-food or absent item.
- `Player.LoadCharacter` / `ToCharacterState` — round-trips inventory and
  all four survival meters without loss.
- `Player.IsWithinReach` — horizontal-only distance, matches
  `Tuning.ChopRangeMetres`.

#2 (gateway routes) needs an integration harness (e.g. Testcontainers for
Postgres) rather than a plain unit test, since the logic lives inline in
minimal-API lambdas against real SQL (`ON CONFLICT` upserts, the voyage
ticket race in `/voyage/claim`). Bigger lift — do it as its own night once
#1 is done and a toolchain is confirmed working.
