# Ashfall — nightly backlog

Ranked by `Severity(1-5) × Blast radius(1-5)`. Highest first. A future night
should generally pick the top item unless it's already been superseded —
check the score is still accurate before starting (the codebase moves).

## Open

### 1. Gateway has zero automated tests — score 12 (Sev 4 × Blast 3)

`apps/gateway/Program.cs` now holds the character-ownership invariant that
prevents cross-world item duplication (see LOG.md 2026-08-08) — genuinely
load-bearing SQL — with no automated regression coverage. Tonight's fix was
verified by hand against a live local Postgres (no Docker available in this
sandbox — see `ARCH.md`); that verification is not repeatable by CI and
won't catch a future regression.

Suggested shape: a `tests/gateway.tests` project using
`Microsoft.AspNetCore.Mvc.Testing`'s `WebApplicationFactory` against a real
Postgres — either `Testcontainers.PostgreSql` (needs Docker, which this
sandbox lacks — confirm CI has it before committing to this) or a
docker-compose-launched instance in CI. Cover at minimum: claim vs. claim
(exclusivity), claim after voyage release, claim after voyage claim, ticket
expiry self-heal, ticket replay rejection. Tonight's manual curl script is a
good starting point for the assertions to encode — didn't turn it into a
committed script because standing up the test *infrastructure* (project,
CI wiring, Postgres-in-CI decision) is a separate unit of work from tonight's
fix, and bundling it in would have been "five things," not "the one thing."

### 2. Reconnect-during-voyage ownership race — score 8 (Sev 4 × Blast 2)

Verified live tonight while testing item 0's fix (see LOG.md): if world A
mints a voyage ticket for a character (owner → NULL) and the *origin* world
A receives a plain reconnect for that same character before world B claims
the ticket, A's `/characters/{id}/claim` succeeds (NULL reads as
"unclaimed", not "in transit"). B's later `/voyage/claim` then correctly
fails — the `owner_world_id IS NULL` guard added tonight prevents any actual
duplication — but the ticket is silently consumed for nothing and the
client arriving at B gets a generic "already claimed" rejection instead of
being told to go back to A. No item/inventory duplication occurs (confirmed
by direct test), so this is a UX/robustness gap, not a dupe bug. Fix shape:
either (a) have `/characters/{id}/claim` refuse to claim a character that has
a live (non-expired) voyage ticket unless `worldId == ticket.from_world_id`,
or (b) have the world-server's `RequestRelease` path not remove the local
`Player` until the target world confirms the claim (bigger change, touches
the "grant then relinquish" ordering documented in `voyage-transfer.md`).
(a) is the smaller, more surgical fix and should be preferred.

### 3. `PlayerStates`/structure broadcasts ignore interest management — score 9 (Sev 3 × Blast 3)

`docs/architecture.md` invariant #5 and `CLAUDE.md`'s load-bearing invariant
#6 both state a client should only receive entities/chunks near it.
`Tuning.InterestRadiusChunks` is declared in `shared-proto/Protocol.cs` but
is **dead** — grepped tonight, zero references anywhere in the codebase. In
practice, `apps/world-server/Program.cs`'s tick loop `Broadcast`s every
player's position to every other connected player unconditionally
(`PlayerStates`, every tick), and the `Hello` handler backfills *every*
structure ever placed in the world to every joining client. Fine at today's
player counts; becomes O(n²) bandwidth as a world fills up, and it's a
documented invariant currently unenforced in code. Not fixed tonight —
doesn't clear the ≥15 (or multiplayer-correctness ≥9) bar, and it's a
scaling concern rather than a correctness bug at current scale.

### 4. Client god-scripts — score 9 (Sev 3 × Blast 3)

`SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines) — see `ARCH.md` for
the breakdown. Already flagged in `docs/gameplay-roadmap.md` §3.4 at roughly
half these sizes; growth has outpaced the plan to decompose them. Not a
correctness bug, but every new HUD panel or client feature now lands in an
already-oversized file. `gameplay-roadmap.md` §3.4 already has a concrete
decomposition plan (net dispatch / terrain streaming / local player / remote
players / interaction as separate systems, plus a shared widget kit) — a
future night implementing gameplay content should budget for peeling at
least one responsibility off one of these files rather than adding to them
wholesale.

### 5. Unauthenticated character endpoints — score 12 (Sev 4 × Blast 3), but likely intentional for now

`GET`/`PUT /characters/{id}` (and now `/claim`) trust whatever UUID the
caller presents — there is no proof the caller is the device that owns that
UUID. Anyone who learns another player's UUID can read or overwrite their
inventory. This is the documented v1 "device-UUID, no login" model
(`README.md` Phase 3a), so it reads as an accepted, temporary simplification
rather than an oversight — flagging it here so it's a deliberate, tracked
trade-off rather than a silent one, and so it's picked up before any wider
playtest or public server. Not touched tonight: fixing it well means picking
an auth story (signed device tokens? account system?) which is a design
decision, not a bug fix, and out of scope for an unsupervised night.

### 6. No shared message-serialization helper (proto duplication risk) — score 6 (Sev 3 × Blast 2)

Already identified in `docs/gameplay-roadmap.md` §3.2: every `MessageId`'s
wire layout is hand-matched between client and server as freeform
`writer.Put`/`reader.Get` calls in the same order, documented only in XML
comments. A mismatched pair is a silent wire bug, not a compile error.
Restated here only to keep one ranked list; the roadmap doc owns the
proposed fix shape (typed reader/writer helpers, or per-message static
`Write`/`Read`).

## Environment / process notes (not gameplay bugs, but cost real time tonight)

- No .NET SDK preinstalled in this sandbox; no working Docker daemon. Both
  are installable/substitutable per `ARCH.md`'s environment notes, but budget
  time for it, or better: bake `dotnet-sdk-10.0` and a Docker-capable runtime
  into the session image so future nights don't repeat this setup cost.
