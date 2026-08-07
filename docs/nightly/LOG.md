# Ashfall — nightly engineer's log

## 2026-08-07 — AUDIT-ONLY (fix applied)

**Chose:** Close a same-`CharacterId` double-connection hole in
`apps/world-server/Program.cs`'s `Hello` handler: if a `CharacterId` is
already attached to another live connection on this world-server, evict the
old connection (flushing its state to the gateway first) before loading the
new one.

**Because:** First night — `docs/nightly/{LOG,BACKLOG,ARCH}.md` didn't exist,
so this session read the codebase cold (`docs/architecture.md`,
`docs/voyage-transfer.md`, `docs/gameplay-roadmap.md`, then the actual
`apps/`/`packages/` source) and ran the audit checklist against it. Found
nothing enforcing "one live connection per character" within a single
world-server: two connections presenting the same `CharacterId` (a fast app
relaunch before the old socket's keepalive timed out, or two devices sharing
a UUID) each load an independent in-memory inventory copy from the gateway,
and whichever disconnects last silently overwrites the other's save
(plain upsert, no version/ownership check). Scored Severity 4 × Blast
radius 4 = 16 — over the ≥15 "must fix tonight" bar, and squarely in the
audit's highest-weighted category (multiplayer correctness, inventory).
Per the decision rule this skips Phase 2 (new feature) entirely.

**Changed:**
- `apps/world-server/Program.cs` — in the `MessageId.Hello` case, after any
  voyage-ticket claim succeeds (so a forged/expired ticket can never evict a
  legitimate session for a join that's about to be rejected anyway), scan
  `players` for an existing connection with the same `CharacterId`. If found:
  remove it from `players`, synchronously save its character state to the
  gateway, disconnect the stale peer, and broadcast `PlayerLeft` for it —
  then proceed with the normal load, which now reads the just-flushed state.
- `docs/voyage-transfer.md` — added a "Rules" bullet documenting the
  single-live-connection-per-character invariant, alongside the existing
  single-world-owner rule it extends.
- `docs/nightly/{LOG,BACKLOG,ARCH}.md` — created (didn't exist before
  tonight). `ARCH.md` is a technical (netcode/persistence) map to complement
  the already-thorough gameplay/content audit in `docs/gameplay-roadmap.md`.
  `BACKLOG.md` holds four sub-threshold findings from tonight's audit that
  did not meet the fix-tonight bar (details there): no interest management
  on the player-position broadcast (12), no automated tests for world-server
  netcode (9), no migration runner for an already-initialized deployment (9),
  and no ownership check on the gateway's character-save endpoint (4).

**Risk:** The eviction path runs the gateway save synchronously
(`.GetAwaiter().GetResult()`), same pattern already used elsewhere in this
handler (character load, voyage ticket claim) — it blocks the world-server's
single tick thread until the HTTP call returns. This was already true of the
existing load-on-Hello call right below it, so it's not a new class of risk,
but a slow/unreachable gateway now stalls two things instead of one during a
reconnect race. If the gateway save throws, it's caught and logged — the old
peer is still evicted and disconnected either way (correct: a hung save must
never block the new, validated connection from taking over). Watch for: log
lines containing `evicted` — if a real player ever sees themselves evicted
unexpectedly (not from their own reconnect), that's a UUID collision or a
bug in this logic, worth a hard look before writing it off as a fluke.

**Revert:** `git revert <this commit's SHA once committed>` — the change is
additive and self-contained to the `Hello` case in one file plus a doc
sentence; reverting drops back to the pre-existing (unsafe) behavior with no
other side effects.

**Verified:** Read the full diff twice for correctness (dictionary mutation
ordering — the stale peer is looked up and removed *outside* the
`foreach` over `players` to avoid a collection-modified-during-enumeration
exception; the `Guid.Empty` guard so two not-yet-`Hello`'d connections never
false-positive-evict each other; the `PeerDisconnectedEvent` handler's
existing `players.Remove(peer, out var player)` early-return means the
evicted peer's own disconnect callback is now a no-op, so there's no
double-save or double-broadcast). Traced every call site of `players` in
`Program.cs` to confirm nothing else assumes at most one entry per
`CharacterId` was already being relied upon elsewhere in a way this change
would break.

**Not verified:** **This environment has no .NET SDK and no running Docker
daemon** — I could not run `dotnet build`, `dotnet test tests/sim-core.tests`,
or actually launch the gateway/world-server to reconnect two real or fake
clients against it. The change is hand-reviewed for correctness, matches the
existing code's style and patterns exactly (mirrors the already-present
fire-and-forget-vs-synchronous save conventions), and touches no sim-core
code so the determinism test suite is untouched by construction — but a
human must run `dotnet build` and `dotnet test tests/sim-core.tests` before
trusting this compiles, and should manually reconnect two clients with the
same device UUID against a local world-server to confirm the eviction fires
and the second session ends up with the first session's latest saved state.

**Rejected tonight:** Didn't reach Phase 2 (new feature) — the audit
surfaced a multiplayer-correctness finding over threshold, and the brief is
explicit that this fixes-first and skips feature work entirely for the
night. Two other candidate "fix tonight" contenders were considered and
scored below the bar on inspection: wall placement claims to `Blocks`
movement (`PlacementRules.Blocks`) but nothing in `MovementRules` actually
checks structures — looked serious at first glance, but it's already
tracked as known, intentional-for-now gameplay debt in
`gameplay-roadmap.md` Phase B ("Wall collision & real shelter"), not a
silent regression, so it doesn't belong in this audit's must-fix bucket.

**Added to backlog:** See `BACKLOG.md` — interest management on the player
broadcast (12), no world-server test coverage (9), no migration runner (9),
gateway save ownership check (4).

**Question for the human:** None blocking — but please run
`dotnet test tests/sim-core.tests` and a manual two-client reconnect test
before this reaches anything beyond a dev branch, since I had no toolchain
to verify either myself.
