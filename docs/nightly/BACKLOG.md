# Nightly backlog

Ranked by `Severity(1-5) × Blast radius(1-5)`. Anything ≥15 (or a
multiplayer-correctness item ≥9) is a "fix it tonight" candidate per the
nightly-engineer process; everything below is picked up on a future night.

| Score | Severity | Blast | Finding |
|---|---|---|---|
| 12 | 3 | 4 | `StructurePlaced`/`TileChanged`/`HarvestProgress` are still broadcast to every connected player regardless of distance — the same interest-management gap fixed tonight for `PlayerStates`, just lower frequency (event-driven, not 15 Hz continuous) so it was lower priority. Fix the same way: filter by `Player.IsWithinInterestOf` (or a slightly larger structure/tile-specific radius, since these are rarer and players may want to see distant smoke) before `Broadcast`. |
| 9 | 3 | 3 | The full structure backfill on `Hello` (`apps/world-server/Program.cs`, the `case MessageId.Hello` block) sends *every* stored structure in the world to a newly joined player, unfiltered by distance. Fine at pre-alpha scale; will not scale once a world accumulates thousands of placed structures. |
| 9 | 3 | 3 | No automated tests outside `tests/sim-core.tests`. `apps/world-server` (Player.cs, Program.cs message handlers) and `apps/client` have zero test coverage — a regression in netcode, harvesting, or crafting authority would only be caught by hand-testing. Tonight's interest-management fix was verified with a throwaway 2-client LiteNetLib harness that was *not* committed. Worth turning into a real `tests/world-server.tests` project (xUnit + an in-process `NetManager` pair) so netcode has a regression net. |
| 9 | 3 | 3 | `ChopRequest`/`CraftRequest`/`PlaceRequest`/`EatRequest` have no per-player rate limit. Movement is budgeted against elapsed time since the last accepted position; these four are evaluated independently per packet with no cooldown. A client (malicious or just buggy) that fires these faster than the intended tap cadence gets served faster than intended, and each one triggers a `Broadcast` — so it doubles as a minor amplification/spam vector. Fix shape: track `LastActionAt` per player per message kind (mirroring `Player.LastAcceptedAt`) and reject anything inside a minimum interval. |
| 4 | 2 | 2 | `SendPlayerStates`/legacy `PlayerStates` count is written as a `byte` with no cap — a 256th player in one world-server would overflow it silently. Pre-existing (not introduced tonight), just newly visible while touching this code. Extremely low priority until a single world-server is expected to hold >255 concurrent players. |
| 4 | 2 | 2 | `World3D.cs` (735 lines) mixes terrain/foliage building, tap-to-harvest input, the day/night clock, and structure rendering in one script — the closest thing to a god-node in the client. Not urgent; each responsibility is a short, separated method today. Worth splitting into components (e.g. a `WorldClock` node, a `GatherController`) if more responsibilities land on it. |
| 3 | 2 | 1.5 | `SurvivalHud.cs` (940 lines) is long but single-purpose (HUD construction for one screen). Would benefit from one builder class per tab if it grows further, but is not currently tangled or buggy. |
| 2 | 1 | 2 | Character persistence (`infra/migrations/002_character.sql`) has no explicit schema-version column. Inventory is self-describing JSONB keyed by stable `ItemId` names, which tolerates additive changes, but there's no recorded plan for a genuinely breaking change. Low urgency — no world has shipped yet (`docs/architecture.md`). |

## Notes for whoever picks these up

- The interest-management gap (top two rows) is the same root cause split
  into two blast radii — fixing `PlayerStates` tonight was the higher-frequency,
  higher-bandwidth half. Doing structures/tiles next is a smaller, similar diff.
- The missing-tests item is arguably the highest-leverage one long-term: every
  other multiplayer-correctness finding above would have been caught
  automatically by a netcode test harness instead of requiring a throwaway
  script each time.
