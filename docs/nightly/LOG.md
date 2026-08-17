# Nightly log

First entry — `docs/nightly/` didn't exist before tonight, created per the
overnight-engineer brief along with `ARCH.md` and `BACKLOG.md`.

**Branch note:** the brief asks for `nightly/{{YYYY-MM-DD}}`, but this
session's git instructions pin it to a specific pre-provisioned branch
(`claude/relaxed-pascal-96gdxp`) and say never to push elsewhere without
explicit permission. Followed the more specific instruction; work landed
there instead of a `nightly/` branch. Flagging so nobody's surprised the
branch name doesn't match the template.

---

## 2026-08-17 — AUDIT-ONLY (fix applied, threshold hit — Phase 2 skipped)

**Chose:** Moved the `world-server`'s gateway HTTP calls (character load on
join, ticket claim on voyage arrival, character save + voyage mint on
release) off the network event thread. They used to run inline via
`.GetAwaiter().GetResult()` inside LiteNetLib event handlers.

**Because:** `world-server` is a single-threaded event loop —
`NetManager.PollEvents()` runs every queued handler synchronously on the
same thread that then runs the tick loop (survival advance, the 15 Hz
`PlayerStates` broadcast, `SendStats`). A blocking gateway call inside
*any* handler therefore blocks *every* connected player's next state
broadcast for as long as that call takes — not just the joining/leaving
player's own connection. Scored Severity 5 × Blast radius 5 = 25 (also
clears the "any multiplayer-correctness finding ≥ 9" bar on its own): every
join and every voyage release hits this path, the blast radius is the whole
world-server, and a slow or unreachable gateway (network blip, a slow query,
a restart) turns into a freeze for everyone in the world, not a delay for
one player. Per the decision rule this had to be fixed tonight; Phase 2 was
skipped.

**Changed:**
- `apps/world-server/Program.cs` — added a `ConcurrentQueue<Action>`
  (`mainThreadQueue`), drained at the top of every tick before
  `PollEvents()`. The `Hello` and `RequestRelease` cases now do their fast,
  synchronous parsing inline (protocol version check, GUID parse, inventory
  snapshot) and hand the gateway round trip to a new `async Task
  HandleHelloAsync` / `HandleReleaseAsync` local function, whose
  continuation enqueues onto `mainThreadQueue` instead of touching
  `players`/`world`/`writer` from a background thread. Both continuations
  guard against the peer having disconnected mid-flight
  (`players.TryGetValue(peer, out var current) && current == player`)
  before touching it.
- `apps/world-server/Player.cs` — added `Ready` (true once Hello's gateway
  round trip completes) and `Releasing` (true from `RequestRelease` until
  granted/denied) flags. `ClientState`, `ChopRequest`, `CraftRequest`,
  `EatRequest`, `PlaceRequest`, and `RequestRelease` now drop the message if
  either is set. This wasn't optional: once the gateway calls stopped
  blocking, the window between "player exists in `players`" and "character
  actually loaded/saved" became real wall-clock time instead of a few
  microseconds inside one `PollEvents()` batch, and without the guard a
  player could gather/craft/place during that window and have it silently
  overwritten by `LoadCharacter` (on join) or lost when the entity is
  removed with an already-taken inventory snapshot (on release).

**Risk:** The core risk is a mistake in the main-thread hand-off — if a
continuation ever touched `players`/`world`/`writer` from the background
task instead of via the queue, that's a data race with no compiler warning.
Reviewed every `mainThreadQueue.Enqueue` call site; none of them close over
mutable state outside the lambda except by reading it (fine — reads from a
background thread of things the main thread also reads are still not safe
in general, but the only such reads here are `player.Id`/`player.CharacterId`
after that player has been snapshotted, not mutated concurrently). Second
risk: a player who now sits in `Releasing` forever if `HandleReleaseAsync`
throws somewhere I didn't anticipate — `DenyAndUnlock` resets `Releasing =
false` in both the caught-exception and null-grant paths, so this should be
covered, but it's the one spot a swallowed exception would be worst.
Symptom to watch for: a player who requests a voyage, gets denied, and then
can never chop/craft/place/move again in that session — that would mean the
unlock path missed a case.

**Verified:**
- `dotnet test tests/sim-core.tests` — 84/84 passing, both before and after
  (this change doesn't touch `sim-core`, ran it anyway per the workflow
  rule).
- `dotnet build` clean (0 warnings, 0 errors) for `world-server`, `gateway`,
  `sim-core`, `shared-proto`.
- Installed `dotnet-sdk-10.0` + the net8.0 runtime via
  `packages.microsoft.com`'s apt repo (not present in the container by
  default; `dotnet-install.sh` is blocked by the egress proxy — see
  ARCH.md's environment note) so this could actually be built and run, not
  just read.
- Ran a real `gateway` (native `postgresql-16`, migrations applied) +
  `world-server` and a throwaway LiteNetLib test client (in the session
  scratchpad, not committed): normal join reaches `Welcome` in ~200ms with
  the real gateway, two concurrent joins both complete cleanly, and the
  15 Hz `PlayerStates` broadcast lands with ~66ms spacing throughout.
- **Direct repro of the bug and the fix**, which is the verification that
  actually matters here: pointed `ASHFALL_GATEWAY` at a raw TCP listener
  that accepts a connection and never responds (simulating an unreachable/
  hung gateway). With tonight's fix, a connected client kept receiving
  `PlayerStates` every ~66ms for the full 12-second test window despite its
  own `Hello` hanging forever against the dead gateway. Stashed the fix,
  rebuilt the pre-fix code, reran the identical scenario: the client
  received **zero** `PlayerStates` packets in 12 seconds — the tick loop was
  completely frozen, exactly as diagnosed. Popped the stash, rebuilt,
  reconfirmed the fix passes the same scenario before writing this up.

**Not verified:** No real Godot client was launched — this container has no
Godot editor, so the fix was exercised through a hand-rolled LiteNetLib test
client speaking the wire protocol, not the actual mobile client. A human
should do one real join/harvest/craft/place/voyage-request pass from the
Godot client against a world-server built from this branch before it ships,
mainly to confirm the `Ready` gate doesn't introduce a visible delay before
the client's first action is accepted (it shouldn't — `Ready` flips inside
the same tick `Welcome` is sent in practice, but that's inference from the
code, not something I watched happen in the real client). Mobile
build/launch itself (the "must build and launch for the mobile target"
constraint) was not attempted — no Godot editor or mobile export template is
available in this environment; only the four .NET class libraries/apps were
built and run.

**Rejected tonight:** Didn't touch the `Releasing`/`Ready` guard's edge case
where a player disconnects and reconnects with a new `NetPeer` while an old
`HandleHelloAsync`/`HandleReleaseAsync` task for the *previous* peer is still
in flight — the `current != player` reference check already handles this
(a new `Player` object is created per connection, so the stale continuation's
`joining`/`leaving` reference will never match the dictionary's current
entry), so no extra guard was needed once traced through. Considered adding
a full `NetworkHandlers` class to get this off the top-level-statements file
entirely — rejected for tonight because it's a much larger diff for the same
external behavior, better done alongside the testing work in BACKLOG.md #1
so the extraction can be verified by tests instead of by hand.

**Added to backlog:** see BACKLOG.md — no automated tests for
`world-server`/`gateway` (16, the next fix-worthy item), interest management
not implemented (12), `World3D.cs` and `SurvivalHud.cs` god scripts (12
each), `Program.cs` growing (8), gateway's hardcoded world registry (6).

**Question for the human:** None blocking — the fix is complete, tested by
hand, and reversible (see below).

**Revert:** `git revert` the commit on `claude/relaxed-pascal-96gdxp`
titled "world-server: stop blocking the tick loop on gateway calls at
Hello/RequestRelease" — it's a single, self-contained commit touching only
`apps/world-server/Program.cs` and `apps/world-server/Player.cs`, no schema
or wire-protocol changes, no feature flag needed because reverting it just
restores the previous (working, just-not-safe-under-a-slow-gateway) inline
behavior.
