# Ashfall — nightly backlog

Ranked by Severity(1-5) × Blast radius(1-5). Not yet built; pick up in a
future night when nothing above-threshold turns up in that night's audit.

| Score | Sev | Blast | Finding |
|------:|:---:|:-----:|---------|
| 12 | 3 | 4 | **No world-server liveness/heartbeat for character ownership.** An unclean world-server crash (kill -9, OOM-kill, host reboot) never fires `PeerDisconnectedEvent`, so `character.owner_world_id` stays claimed by a dead process forever — every future join for that character 409s until an operator manually clears the column. Fix shape: a short-TTL lease the owning world-server renews periodically (mirroring the existing voyage-ticket TTL/self-heal pattern in `infra/migrations/003_voyage.sql` and `gateway/Program.cs`'s `ReclaimExpiredTicketAsync`), or a periodic reconciliation job. Scoped out of tonight's fix deliberately — see LOG.md. |
| 12 | 3 | 4 | **No automated test for gateway/world-server netcode or persistence.** `tests/sim-core.tests` only covers the deterministic rule library. Nothing in CI exercises the gateway's HTTP endpoints (character claim/release, voyage mint/claim) or the world-server's message handlers. Needs a lightweight integration-test project (e.g. `WebApplicationFactory` for the gateway against a throwaway Postgres, or a Testcontainers setup) before any of that logic can be safely refactored again. |
| 9 | 3 | 3 | **Client god-scripts.** `World3D.cs` (735 lines) and `SurvivalHud.cs` (940 lines) each mix multiple systems. Already tracked in detail in `docs/gameplay-roadmap.md` §3.4 with a concrete split plan — don't duplicate that plan here, just keep the pointer. |
| 8 | 2 | 4 | **Static world registry.** `gateway/Program.cs` hardcodes the `worlds` dictionary (`continent-a`/`continent-b` → fixed host:port). Any real multi-instance deployment needs live world-server registration instead of a compile-time table. Explicitly flagged as a Phase 3+ item in the code's own comment already. |
| 6 | 2 | 3 | **`RequestChunk` regenerates + resends full chunk tiles on every request with no client-side cache hint or server-side throttle.** Not measured yet — flagged for a future night to actually profile bandwidth/CPU per request before scoring higher. |

## Gameplay content backlog

Not re-scored here — `docs/gameplay-roadmap.md` already has a scored, phased
gameplay plan (Phase A: food loop, functional campfire, night pressure, tool
bonuses — all now shipped per that doc's own progress notes; Phase B:
skills/progression; Phase C: creatures/combat; Phase D: polish). Read that
doc before picking the next *feature* night; this file is for engineering
debt found during audits.
