# Nightly backlog

Ranked ideas / findings not yet built, scored `Severity(1-5) × Blast radius(1-5)`
per the nightly audit rubric. `≥ 15`, or any multiplayer-correctness finding
`≥ 9`, is a same-night must-fix; everything below lands here instead.

## Open

### Same-world duplicate `Player` on fast reconnect — Sev 3 × Blast 3 = 9
A client that reconnects to the *same* world-server before that server's
`PeerDisconnectedEvent` fires for the stale peer (LiteNetLib's ping-timeout
window, a few seconds by default) creates a second in-memory `Player` for the
same `CharacterId` under a new `NetPeer`. Both would independently claim
ownership in the gateway (idempotently — same `worldId`, so tonight's fix
does not block it) and could both mutate/save inventory. Lower blast radius
than the cross-world case fixed tonight (single-process, single-world, needs
a genuinely fast reconnect to hit), but the same *class* of bug. Fix sketch:
on Hello, if `players.Values.Any(p => p.CharacterId == newId)`, disconnect the
stale peer (or reject the new one) before proceeding.

### Zero test coverage on world-server / gateway — Sev 3 × Blast 4 = 12
No test project exercises the UDP protocol, the gateway HTTP API, or the SQL
in `infra/migrations`. `tests/sim-core.tests` only covers `sim-core`. Tonight's
voyage-ownership fix was verified by hand-building a local Postgres cluster
(no Docker daemon available in this sandbox) and running the exact SQL
extracted from `Program.cs` against the real schema for 8 scenarios — that
should be a `WebApplicationFactory` + test-container integration test living
in the repo, not a one-off verification. Suggest a
`tests/gateway.tests` project using `Microsoft.AspNetCore.Mvc.Testing` +
`Npgsql` against a disposable schema (or Testcontainers, if a dependency on it
is acceptable — needs the usual justification/size note before adding it).

### No migration runner / version tracking — Sev 2 × Blast 3 = 6
`infra/migrations/*.sql` are plain numbered files with no tracking table and
no tool enforcing order or idempotency against a live target. Fine at 4 files;
won't scale past a handful without someone applying the wrong file twice or
skipping one against a fresh environment.

### Gateway world registry is a hardcoded `Dictionary` — Sev 2 × Blast 2 = 4
`apps/gateway/Program.cs`, already flagged in its own comment as a Phase 3+
stopgap. Not urgent while there are two static worlds.

### `SurvivalHud.cs` (940 lines) / `World3D.cs` (735 lines) — unscored
Flagged by line count only (>400-line god-script threshold) — **not
content-reviewed this session**, so no severity score yet. Could be entirely
legitimate cohesive UI/render code; could be doing too much. Next night:
actually read them before scoring.

### Gateway-unreachable-at-join still lets a player continue with a blank slate — Sev 3 × Blast 3 = 9
In the `Hello` handler's load `try/catch`, an exception from
`gateway.GetCharacterAsync` (network blip, gateway down) is caught, logged,
and the player is allowed to continue playing with the `Player` class's
default empty state — `CharacterLoaded` correctly stays `false` after
tonight's fix, so a disconnect won't blank their real save (that part is now
closed), but the player still spends a whole session unable to see their real
inventory, and cannot voyage (`RequestRelease` now denies with "character not
loaded", also from tonight's fix). Acceptable degraded-mode behavior, but
should at minimum notify the client so it doesn't look like a silent item
wipe. Not fixed tonight — kept the existing behavior to keep the diff to one
change; flagging so it isn't mistaken for solved.

## Rejected feature ideas (from Phase 2 consideration, not reached)

Audit found a must-fix (voyage ownership race, see LOG.md), so Phase 2 was
skipped entirely per the decision rule. No feature ideas were designed or
rejected tonight — nothing to record here yet.
