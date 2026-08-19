# Nightly log

First entry — `docs/nightly/{LOG,BACKLOG,ARCH}.md` did not exist before
tonight; created per the nightly process's instructions, with `ARCH.md`
as the primary deliverable of standing up the process itself.

## 2026-08-19 — AUDIT (fixed a mandatory finding, Phase 2 skipped)

**Chose:** Added a server-side minimum interval between a player's accepted
`ChopRequest`/`CraftRequest`/`PlaceRequest`/`EatRequest` messages
(`packages/sim-core/ActionThrottle.cs`, `Player.TryBeginAction`, wired into
all four handlers in `apps/world-server/Program.cs`).

**Because:** Phase 1 audit found that none of the four economy-affecting
request handlers had any time-based gate. `ChopRequest` in particular: every
accepted strike unconditionally yields its item (`World.TryHarvest` in
`packages/sim-core/World.cs`), and the only per-request check was spatial
reach (`Player.IsWithinReach`). The legitimate client only ever sends one
`ChopRequest` per discrete tap gesture (`World3D.cs` `_harvestQueued` flag,
set once per tap-and-release, consumed once per physics frame) — but nothing
on the server enforced that. A client that skipped the UI and wrote raw UDP
packets could blast `ChopRequest` at whatever rate the network would carry,
each one yielding wood/stone/fiber/berries, with no felling requirement
standing in the way of the yield (felling only gates what the *tile* becomes,
not whether the strike pays out). `CraftRequest`/`PlaceRequest`/`EatRequest`
are bounded by inventory affordability so they can't manufacture resources
from nothing, but they were equally unthrottled and equally able to be
spammed past any intended pace, including flooding the fire-and-forget
`WorldStore` writes and the `InventoryDirty`/broadcast paths per request.

Scored: harvest farming is Severity 5 (trivially reachable — no aim-bot or
timing skill needed, just remove the client's own rate limit) × Blast radius
5 (breaks the entire survival-scarcity design the game is built around: wood/
stone/fiber/food all become worthless once one player can mint them
arbitrarily fast, for themselves or to trade/dupe to others in a shared
world). 25, and independently a multiplayer-correctness finding ≥ 9 on its
own — per the audit's decision rule this was mandatory to fix tonight, and
Phase 2 (new feature) was skipped entirely rather than attempted alongside a
five-things night.

**Changed:**
- `packages/sim-core/ActionThrottle.cs` (new) — the pure, testable rate-limit
  rule: `Ready(lastActionAt, now)` is true once
  `MinIntervalSeconds` (0.15s) has elapsed. Deliberately in `sim-core`
  rather than inline in the world-server so it has the same test-first
  treatment as `MovementRules`, even though (unlike movement) there's no
  client-side prediction to share it with — the reasoning is "same class of
  anti-cheat rule," not "shared across client/server."
- `apps/world-server/Player.cs` — added `_lastActionAt` and
  `TryBeginAction(now)`, a thin wrapper that advances the clock only on
  acceptance.
- `apps/world-server/Program.cs` — one `if (!player.TryBeginAction(Now()))
  { …log…; break; }` guard at the top of each of the four request cases.
- `tests/sim-core.tests/ActionThrottleTests.cs` (new) — reject-at-zero,
  reject-within-interval, accept-at-interval, accept-with-no-prior-action.

**Risk:** `MinIntervalSeconds = 0.15` (≈6.6 actions/sec ceiling per player) is
a judgment call, not a measured value — I don't have telemetry on real tap
cadence. If it's too tight, a fast double-tap on the hotbar (craft, then
immediately craft again) could get silently dropped, which would read as an
unresponsive UI rather than an error (the handler just `break`s past a
throttled request with only a server-side console log — the client gets no
feedback and no correction, unlike a movement rejection). Watch for: reports
of "I tapped craft/eat and nothing happened," or a burst of `"chop
throttled"`/`"craft throttled"` etc. lines in world-server logs during normal
play (a few from a single fast tap sequence are expected and fine; sustained
throttling from one player is the signal something's off, either a
misbehaving client or the threshold being too tight).

**Revert:** `git revert <this commit>` — the change is additive and isolated
to three files plus one new test file; reverting drops the throttle cleanly
back to the prior (exploitable) behavior with no data-shape change to undo.

**Verified:** Read every line of the diff against the existing
`ChopRequest`/`CraftRequest`/`PlaceRequest`/`EatRequest` handlers to confirm
the early-`break` is safe (each `NetPacketReader` is recycled once per
message regardless of how many fields were consumed before the break, so
returning before reading `tx`/`ty`/`kind`/etc. does not desync the reader).
Confirmed `Player.cs` already has `using Ashfall.SimCore;`, so `ActionThrottle`
resolves without a new import. Confirmed the four throttled cases were the
only call sites for those four `MessageId`s. Matched the new test file's
style and namespace to the existing `MovementRulesTests.cs`.

**Not verified:** I could not build or run `dotnet test tests/sim-core.tests`
in this sandbox — the .NET SDK is not installed here and outbound network
access is blocked (installer download got a proxy 403), so I never compiled
this change. CI (`.github/workflows/ci.yml`) will run the full build +
`dotnet test tests/sim-core.tests` on the PR; watch that instead of trusting
this log. I also did not run two live clients against a world-server (no
runtime available here either) — the "spam past the throttle and confirm the
server drops it, then confirm normal single-tap play still feels
instant" check needs a human on a real client or the Godot editor. If CI is
green, the remaining risk is purely the tuning constant, not correctness.

**Rejected tonight:** Considered fixing only `ChopRequest` (the highest-score
path) and leaving `CraftRequest`/`PlaceRequest`/`EatRequest` unthrottled —
rejected because they're the same class of exploit (untrusted client, no
time-bound accept) and leaving three of four unfixed after finding the
pattern would be leaving known-bad code in place, not "fixing exactly one
thing." Considered a per-action-type cooldown (separate intervals for chop
vs. craft vs. place vs. eat) instead of one shared gate — rejected as
premature: nothing in the current design calls for different cadences per
action type, and a single constant is one thing to tune later if that
changes, versus four to keep in sync now.

**Added to backlog:** interest management not implemented despite being a
documented invariant (12), no test coverage for world-server/gateway (12),
`RequestChunk` has no rate limit (9), fire-and-forget saves have no retry (9),
`SurvivalHud.cs` is a 940-line god script (6), `CLAUDE.md` vs. the shipped 3D
client (3). Full detail and fix shapes in `BACKLOG.md`.

**Question for the human:** None blocking. One non-blocking flag: this
container has no .NET SDK and no outbound network to install one, so no
nightly session on this environment can compile or run tests locally —
every night's "Verified" section will read "could not build, relying on CI"
until that's addressed. Worth deciding whether that's acceptable (CI is
real and does run) or whether the environment should get the SDK pre-baked.
