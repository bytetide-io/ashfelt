# Ashfall — architecture notes (nightly)

Honest working map for unattended nightly sessions. `docs/architecture.md` and
`docs/voyage-transfer.md` are the source of truth for *decided* invariants;
this file is where debt, gaps, and "things I noticed while in here" live.
Update it whenever a session's audit changes the picture — don't let it rot
into fiction.

## Shape (as of 2026-08-09)

```
apps/client       Godot 4 mobile client, C#, net8.0
apps/world-server Headless authoritative UDP server (LiteNetLib), net10.0
apps/gateway      ASP.NET Core minimal API — accounts, character storage,
                  voyage routing, net10.0
packages/sim-core Shared deterministic sim (terrain, harvest, craft, place,
                   survival, movement rules, world clock), net8.0
packages/shared-proto  Wire message + CharacterState shapes, net8.0
infra/migrations  Hand-rolled numbered SQL files, applied manually/by hand —
                   no migration runner or version table found
tests/sim-core.tests   xunit, sim-core only — no test project touches
                   world-server or gateway at all
```

Total C# is small (~5.6k LOC across apps+packages as of tonight). This is
still an early, single-region-pair (`continent-a`, `continent-b`) prototype,
not yet a fleet.

## What's solid

- `sim-core` is genuinely the single source of truth for terrain, harvest,
  craft, placement, survival, and movement-bound checking. Client and server
  both reference it; determinism tests exist and are treated as a
  compatibility contract (correctly — see `DeterminismTests.cs`).
- The world-server's tick loop is careful about async: loads block (correct —
  joining is inherently a wait), saves are fire-and-forget off the packet
  loop (correct — a slow write must never stall other players), diffs are
  never full-chunk (seed + diff persistence, matches invariant #2).
- Movement is server-validated against `MovementRules.Check` using a shared
  height field — no server-side physics engine, a deliberate and documented
  tradeoff (see `docs/architecture.md` "Movement authority").
- Voyage transfer (`docs/voyage-transfer.md`) is a well-designed three-party
  handshake *on paper*: single-use tickets, TTL self-heal, ownership column.
  **Tonight's fix closed the gap between that design and what the code
  actually enforced** — see LOG.md 2026-08-09. The plain-Hello join path
  never consulted `owner_world_id` at all before tonight; only the voyage
  endpoints did. Worth an explicit callout here because it's exactly the kind
  of gap that's invisible from reading `docs/voyage-transfer.md` alone — the
  doc describes the intended protocol, not which code paths actually check it.

## Known debt / gaps (see BACKLOG.md for scored, actionable items)

- **Zero test coverage outside sim-core.** No test project exercises
  world-server or gateway — not the UDP protocol, not the HTTP API, not the
  SQL. Tonight's fix was verified by hand against a real (locally-built,
  non-Docker) Postgres instance and manual protocol tracing, not by CI. That
  is a gap, not a success story.
- **No migration runner.** `infra/migrations/*.sql` are numbered files with no
  tracking table (`schema_migrations` or similar) and no tool that applies
  them idempotently against a target DB version. `docker-compose` presumably
  applies them somehow on container init — worth confirming, not confirmed
  tonight.
- **Two potential god-scripts on the client**, by line count alone (not
  content-reviewed this session): `apps/client/scripts/world3d/SurvivalHud.cs`
  (940 lines) and `apps/client/scripts/world3d/World3D.cs` (735 lines). Both
  exceed the >400-line god-script threshold. Not triaged for actual coupling
  vs. legitimately-cohesive UI code — flagged, not scored, in BACKLOG.
- **World registry is a hardcoded `Dictionary` in `apps/gateway/Program.cs`**,
  explicitly marked `// Phase 3+ replaces this with live world-server
  registration` in a comment. Fine for two static worlds; will not survive
  more than a handful of manually-configured regions.
- **A single UDP world-server process can still end up with two `Player`
  objects for one character** if a client reconnects to the *same* world
  before that world's `PeerDisconnectedEvent` fires for the stale peer
  (LiteNetLib ping-timeout window). Tonight's ownership fix is cross-world;
  it does not close this same-world, same-tick race. See BACKLOG.

## Where to look before touching things

- Voyage/ownership: `apps/gateway/Program.cs` (endpoints) +
  `infra/migrations/003_voyage.sql` (schema/invariant doc-comment) +
  `apps/world-server/Program.cs` `case MessageId.Hello` and
  `case MessageId.RequestRelease`. These three now have to agree; if you
  change one, re-read the other two.
- Survival meters: `packages/sim-core/SurvivalRules.cs` (pure, fixed-point,
  well-commented) is the only place the math lives — `Player.cs` just calls
  into it.
- Anything client-rendering-related: start from
  `apps/client/scripts/ui/DesignSystem.cs`, per `docs/architecture.md`.
