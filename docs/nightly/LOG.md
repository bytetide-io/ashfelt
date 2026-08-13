# Nightly log

## 2026-08-13 — AUDIT-ONLY (with mandatory fix)

**Chose:** Interest-manage `PlayerStates` and `StructurePlaced` on the
world-server instead of broadcasting them to every connected player every
tick.

**Because:** Audit score 20 (Sev 4 × Blast 5), the highest of the night and
above both mandatory-fix thresholds (≥15 combined, ≥9 multiplayer-correctness).
`apps/world-server/Program.cs` broadcast a full `PlayerStates` snapshot of
every connected player to every connected peer on every 15 Hz tick
(unbounded O(n²) traffic, and it leaked the whole player roster to everyone
regardless of distance), and sent every `StructurePlaced` — both the
join-time backfill and the live placement broadcast — to every client
regardless of distance. This directly violates CLAUDE.md invariant #6
("a client only ever receives entities/chunks near it") and
`docs/architecture.md`'s interest-management invariant. Notably,
`Tuning.InterestRadiusChunks` already existed in `packages/shared-proto` and
the `PlayerStates` XML doc comment already promised "every player in
interest range" — the constant and the intent were there, the wiring never
was. Per the decision rule, any qualifying finding forces a mandatory fix
and skips Phase 2 (new feature) entirely — this file's Phase 1 audit found
one, so no feature was attempted tonight.

**Changed:**
- `packages/shared-proto/Protocol.cs` — added `Tuning.InterestRadiusMetres`
  and `Tuning.StructureDiscoveryTicks`; updated `PlayerLeft` and
  `StructurePlaced` XML docs to describe their broadened use (no wire-layout
  change, no `ProtocolVersion` bump — both messages keep their exact byte
  layout, only *when* they're sent changes, which is what their docs already
  specified for `PlayerStates`).
- `packages/sim-core/InterestRules.cs` (new) — one pure, testable function:
  `IsWithinRange(Vec3 viewer, Vec3 subject)`.
- `tests/sim-core.tests/InterestRulesTests.cs` (new) — 4 cases: same
  position, just inside/outside the radius, vertical distance not counted
  (mirrors `HasWarmthNear`/`IsWithinReach`).
- `apps/world-server/Player.cs` — added `KnownPlayerIds`/`KnownStructureIds`
  per player, used to diff who/what is newly in or out of range each tick.
- `apps/world-server/Program.cs`:
  - `SendPlayerStates(viewer)` replaces the single global broadcast: each
    viewer now gets only players within `InterestRadiusMetres`, plus an
    explicit `PlayerLeft` for anyone who just dropped out of their range
    (reusing `PlayerLeft`'s existing wire shape — the client's response to
    "this id is gone" is identical whether the cause was a disconnect or
    walking out of range).
  - `SendStructureIfInRange(viewer, structure)` replaces the unconditional
    join backfill and the unconditional live-placement broadcast; also
    driven by a new periodic per-second discovery scan
    (`Tuning.StructureDiscoveryTicks`) so a player who walks toward an
    already-existing structure they weren't near at join time still learns
    about it, without resending anything already known.
  - `PeerDisconnectedEvent` now removes the departing id from every other
    player's `KnownPlayerIds`, so that set can't leak one entry per churned
    player over a long-running server.
- `docs/nightly/{ARCH,BACKLOG,LOG}.md` created — did not exist before
  tonight; `ARCH.md` is the honest-map deliverable these instructions ask
  for when the nightly docs are missing.

**Risk:** The main behavioural risk is the `PlayerLeft` reuse — if a client
build somewhere depended on `PlayerLeft` meaning "disconnected" specifically
(e.g. showing a toast, logging analytics) rather than "stop tracking this
id," that assumption would now be wrong. I checked: `World3D.cs`'s handler
(`OnPlayerLeft`) does nothing but `_remotes.Remove(id)`, so today it's safe.
Second risk: `SendStructureIfInRange`'s periodic scan is O(players ×
structures) once a second — fine at current structure counts, logged to
BACKLOG as a scaling item, not a correctness one. Third: `TileChanged` and
`HarvestProgress` were deliberately left as full broadcasts (not part of the
flagged finding's cited line ranges) — logged to BACKLOG as the natural next
bite of the same invariant, so it isn't silently dropped.

**Revert:** `git revert` the commit on `claude/relaxed-pascal-4kc1w4` titled
"world-server: interest-manage PlayerStates and StructurePlaced broadcasts".
Single self-contained commit, no migration, no persisted-data shape change —
safe to revert wholesale.

**Verified:** Read every touched call site end-to-end and traced the
client-side handlers (`RemotePlayers.Apply`/`Remove`, `World3D.OnPlayerLeft`,
`OnStructurePlaced`) to confirm they tolerate a filtered, incremental stream
instead of a full one each tick — they do, since `Apply` was already
additive-only and relied on explicit removal messages. Manually reviewed the
full diff of `Program.cs` for type/signature correctness (local functions,
`Vec3`/`HashSet<int>`/`HashSet<long>` usage, `ImplicitUsings` coverage) and
cross-checked against the existing `SimCore`/`WorldServer`/test project
references to confirm the new files (`InterestRules.cs`,
`InterestRulesTests.cs`) fit the existing dependency graph.

**Not verified — I could not build or run anything tonight.** The sandbox
has no `dotnet` SDK installed, no cached SDK anywhere on disk, and the
official `dotnet-install.sh` fetch was blocked by the outbound network
policy (403 from the egress proxy on `dot.net`) rather than a transient
failure, so I did not retry or attempt to route around it. Docker is also
unavailable (no daemon socket). Concretely unverified:
`dotnet test tests/sim-core.tests` (the four new `InterestRulesTests` and
the full existing suite), whether `apps/world-server` and
`apps/client`/`apps/gateway` still build, and — per the hard constraint —
whether the project still builds and launches for the mobile target at all.
A human needs to run `dotnet test tests/sim-core.tests` and
`dotnet build` across the solution, and ideally launch two client instances
against a local world-server to watch one player's capsule appear/disappear
on the other's screen as they walk in and out of ~100 m range, before
trusting this change.

**Rejected tonight:** N/A — the audit found a mandatory-fix-tier issue, so
Phase 2 (new feature) was skipped per the decision rule; no feature ideas
were designed or rejected.

**Added to backlog:** See `docs/nightly/BACKLOG.md` — 10 open items ranked by
score, including one (no server-side chop cooldown, score 16) that also
clears the mandatory-fix threshold and is next in line, plus two new items
surfaced while implementing tonight's fix (`TileChanged`/`HarvestProgress`
still unfiltered; the O(players×structures) discovery scan's scaling
ceiling).

**Question for the human:** None blocking — but please run the build/test
commands above before this reaches a device; I could not.
