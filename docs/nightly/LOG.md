# Nightly log

## 2026-07-25 — AUDIT-ONLY (fix)

**Branch note:** the standing instructions for this rotation say to work on
`nightly/{{YYYY-MM-DD}}`. This session's actual git harness assigned and
required branch `claude/relaxed-pascal-22ex5p` for all commits/pushes instead,
with an explicit "never push to a different branch" constraint. I followed the
harness's branch, not the nightly-convention name, since it's the concrete
operational requirement for *this* run rather than a generic instruction
written before this session existed. Recording it here so it isn't mistaken
for a slip.

**Chose:** fixed a character-save/reconnect race in `apps/world-server`. A
player who disconnects and reconnects quickly — the normal case on mobile,
where backgrounding the app or a wifi/cellular handoff drops the UDP link —
could have their character silently rolled back to the state before they
disconnected, permanently losing everything gathered in between.

**Because:** Phase 1 audit, scored **Severity 4 × Blast radius 4 = 16**
(≥15 threshold; also a "race conditions on join/leave/reconnect" multiplayer-
correctness finding ≥9 on its own) — per the standing rule this mandates a fix
tonight and skips Phase 2 (new feature) entirely.

The mechanism: `PeerDisconnectedEvent` (`apps/world-server/Program.cs`)
snapshots the character and fires `gateway.SaveCharacterAsync` as
fire-and-forget (`Task.Run`), deliberately — so a slow write never stalls the
players still in the world. But nothing stopped a fast reconnect's Hello
handler from calling `gateway.GetCharacterAsync` *before* that save landed.
Sequence:

1. Player harvests wood (in memory only — nothing is saved mid-session, see
   `ARCH.md`); connection drops; disconnect handler snapshots state and
   schedules the save, which takes some tens/hundreds of ms (an HTTP round
   trip to the gateway).
2. Client auto-reconnects (its retry logic reconnects to the same
   host/port) before that save's HTTP request completes.
3. New session's Hello handler loads the character from the gateway —
   reading the row as it was *before* step 1's save landed, i.e. without the
   wood.
4. Step 1's save then completes and writes the wood-included snapshot to the
   gateway — but the new (already-running) session's in-memory state doesn't
   know that; it's still working from the stale load.
5. When the new session eventually disconnects, it saves its own snapshot —
   built from the stale baseline — overwriting step 4's write. The wood (and
   anything else gathered in the gap) is gone for good, and nothing in the
   logs would look wrong.

**Changed:**
- `apps/world-server/PendingSaveTracker.cs` (new) — a small `ConcurrentDictionary<Guid, Task>`
  wrapper: `Track(characterId, saveTask)` records an in-flight save, removed
  automatically on completion; `WaitFor(characterId)` blocks until any save
  in flight for that character lands (a no-op on the common case of a normal
  join with nothing pending).
- `apps/world-server/Program.cs` — the disconnect handler now calls
  `pendingSaves.Track(...)` on the save task it already creates; the Hello
  handler calls `pendingSaves.WaitFor(player.CharacterId)` immediately before
  loading, so a fast reconnect always reads its own most recent write.
- `tests/world-server.tests/` (new project) — first tests for `world-server`
  at all. `PendingSaveTrackerTests.cs` covers: no-op when nothing is tracked,
  blocking until the tracked save completes, becoming a no-op again once that
  save has completed, and not blocking on an unrelated character id.
- `.github/workflows/ci.yml` — added `dotnet test tests/world-server.tests`
  (it built `apps/world-server` already but ran no tests against it).
- `docs/nightly/{ARCH,BACKLOG}.md` (new) — first run of this rotation, so
  these didn't exist; created per the standing instructions, `ARCH.md` as the
  primary deliverable alongside the fix.

**Risk:** `pendingSaves.WaitFor` blocks the single-threaded server tick/event
loop for as long as a save is still in flight — the same tradeoff already
accepted for the character *load* on every join (`.GetAwaiter().GetResult()`),
just now also paid, occasionally, by a fast reconnect. A save is one HTTP PUT
to the gateway on localhost/LAN in dev; if the gateway is slow or unreachable,
every other player's tick stalls for that duration. This mirrors the existing
load-blocking behavior and the existing `RequestRelease` behavior (both
already block the loop on a gateway round trip), so it's a consistent
tradeoff, not a new class of risk — but it's worth knowing if the gateway ever
becomes meaningfully slower than the tick interval.

To spot it: watch for `[world] character save failed for ...` in server logs,
or, if it recurs, unexplained tick-rate hitches correlated with reconnect
volume.

**Revert:** `git revert` the commit(s) on this branch, or manually: delete
`apps/world-server/PendingSaveTracker.cs` and `tests/world-server.tests/`,
remove the `pendingSaves` field/calls from `apps/world-server/Program.cs`, and
drop the `dotnet test tests/world-server.tests` line from `ci.yml`. No schema
or protocol change to unwind — this is entirely in-process world-server logic.

**Verified:**
- Read every line touched and the surrounding handlers by hand, tracing the
  exact byte-for-byte sequence of the race before and after the fix.
- Confirmed `ConcurrentDictionary<TKey,TValue>.TryRemove(KeyValuePair<TKey,TValue>)`
  is a public .NET API (conditional remove — only removes if the stored task
  still equals the one just completed, so a save superseded by a second rapid
  disconnect/reconnect before the first finishes can't be removed out from
  under the newer one).
- Confirmed the voyage-transfer path (`RequestRelease`) is unaffected — it
  already saves synchronously before minting a ticket, so it never had this
  race; `PendingSaveTracker` only guards the plain-disconnect path.
- Wrote `PendingSaveTrackerTests` to pin the tracker's behavior in isolation
  (using a controllable `TaskCompletionSource` rather than a real HTTP call).

**Not verified — no `dotnet` SDK available in this sandbox, and installing one
is blocked by the environment's egress policy (attempted `dot.net/v1/dotnet-install.sh`,
got a 403 from the proxy — an explicit "don't retry, report it" signal, not a
transient failure):**
- The new code was never actually compiled. I reviewed it line by line against
  the existing style and the .NET APIs involved, but a human **must** run
  `dotnet build apps/world-server` and `dotnet test tests/world-server.tests`
  before trusting this on a real device or server.
- No live reconnect test against a running world-server + gateway (would need
  2+ simulated clients, one disconnecting and reconnecting quickly, per the
  Definition of Done). Recommend: start `docker compose -f infra/docker/docker-compose.yml up`,
  connect a client, harvest something, kill the connection (airplane mode or
  kill the client process) and immediately reconnect, and confirm the
  harvested item survives.
- Godot client build/import was not run (same missing-SDK constraint; the
  client wasn't touched anyway).

**Rejected tonight:** N/A — Phase 1 found a mandatory-fix finding, so Phase 2
(new feature) was never reached. Two backlog items were close contenders for
"the one thing" (interest management, scored 12; the general server/gateway
testing gap, scored 9) but neither cleared the ≥15 / ≥9-multiplayer-correctness
bar the way the reconnect race did.

**Added to backlog:** interest management unimplemented (12), server/gateway
test coverage gap (9), `Program.cs` dispatch trending toward a god-script (6),
`World3D.Build()` fixed-radius-around-origin (4), `SurvivalHud.cs` size (4).
Full detail in `BACKLOG.md`.

**Question for the human:** none blocking — but please run the build/test
commands above before this reaches a device; I could not.
