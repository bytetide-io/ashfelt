# Ashfall — nightly session log

## 2026-07-30 — AUDIT + FIX

> **Branch note:** the session harness this ran under requires developing on
> `claude/relaxed-pascal-3ycvsr` rather than `nightly/2026-07-30` — a
> repo-hosting-level constraint that overrides the nightly-prompt template's
> branch instruction. Noting the deviation here per that instruction's own
> "justify it in the log" rule.

> **This session could not build, run, or automatically test the C# code.**
> Neither the `dotnet` SDK nor a running Docker daemon is available in this
> environment (confirmed by direct check: no `dotnet` binary anywhere on
> disk, `docker info` fails to reach a daemon). `dotnet test
> tests/sim-core.tests` — required by CLAUDE.md before any commit — could
> not be run. What I *could* do, and did: start the sandbox's local
> `postgresql-16` install, apply the real migrations, and hand-run the exact
> SQL added to `apps/gateway/Program.cs` against a real database to verify
> the ownership-claim logic (see "Verified" below). The C# itself is
> unverified beyond careful manual reading. A human must run the build and
> `dotnet test` before trusting this beyond the SQL-level verification.

**Chose:** Fixed unenforced character ownership on the ordinary (non-voyage)
join path — the same `CharacterId` could be loaded by two live sessions at
once (two peers, or two world-servers), each mutating an independent
in-memory inventory, with whichever disconnected last silently winning the
save. A working item-duplication exploit.

**Because:** Two independent audit agents (world-server multiplayer
correctness, and gateway/persistence) converged on this same root cause from
opposite ends: `apps/world-server/Program.cs:119-129`'s ticket-less `Hello`
loaded a character via `GetCharacterAsync` with zero ownership check, and
`apps/gateway/Program.cs`'s plain `GET`/`PUT /characters/{id}` never
consulted or set `owner_world_id` at all — only the voyage-ticket path did.
Scored **severity 5 × blast radius 5 = 25**, squarely in the "multiplayer
correctness" category the task brief weights highest ("bugs here are the
worst kind"), and above the ≥9 multiplayer-correctness threshold by a wide
margin.

A second finding tied at 25 (`DeterminismTests.cs` never pins a golden
reference value — see `BACKLOG.md` #1): I picked the ownership bug over it
because it's a *live, currently exploitable* duplication path today, whereas
the determinism gap is a missing safety net that only causes harm if and when
someone changes generation code in the future. Fixing the active exploit
first was the judgment call; the determinism gap is now the top item in
`BACKLOG.md` for tomorrow night.

**Changed:**
- `apps/gateway/Program.cs` — `GET /characters/{id}` now takes a `worldId`
  query parameter and does an atomic claim (`UPDATE ... WHERE owner_world_id
  IS NULL OR owner_world_id = $worldId RETURNING ...`); a different current
  owner returns `409 Conflict` instead of the character. `PUT
  /characters/{id}` also takes `worldId` and releases the claim back to
  `NULL` as part of the same write, but only if that world still holds it.
- `apps/world-server/GatewayClient.cs` — `GetCharacterAsync` now returns a
  `CharacterLoadResult` (`New` / `Loaded` / `Denied`) instead of a bare
  nullable `CharacterState`; `SaveCharacterAsync` takes the calling world's id.
- `apps/world-server/Program.cs` — the `Hello` handler rejects (`Denied`)
  rather than loads a character owned elsewhere; a duplicate `Hello` for a
  `CharacterId` already live on this process now evicts the older in-memory
  session (removes it, broadcasts `PlayerLeft` for it, disconnects its peer)
  before proceeding — this closes the same-world-server half of the
  exploit that the gateway-side claim alone can't catch, since two peers on
  the *same* world would otherwise both pass the gateway's "already ours"
  check.
- `apps/gateway/API.md` — documented the new `worldId` parameter and the
  `409` response on both endpoints.

**Risk:**
- If I mis-modeled the ASP.NET minimal-API scalar-from-query-string binding
  for `worldId` (untested — no dotnet SDK to compile against), both
  endpoints would fail at request time rather than silently misbehave — a
  human running the gateway locally and hitting `/characters/{id}` would see
  it immediately as a 400 or a binding exception in the console.
- **New residual risk this fix introduces** (logged as `BACKLOG.md` #13): a
  world-server *process crash* while a player is normally connected (not a
  clean disconnect — LiteNetLib's own timeout detection still triggers the
  normal save+release path for a merely dropped connection) leaves
  `owner_world_id` claimed with nobody left alive to release it. Unlike the
  voyage ticket path, there's no TTL self-heal on this claim yet. Before this
  fix, there was no ownership tracking on this path at all, so this lockout
  mode didn't exist — it's a new, low-probability, operator-recoverable
  trade for closing an actively exploitable duplication bug. Flagged, not
  silently accepted.
- Two lower-severity, pre-existing races are explicitly **not** fixed by this
  change and remain in `BACKLOG.md` (#12): a fast reconnect's `GET` claim can
  still race a still-in-flight fire-and-forget save from the prior
  disconnect, losing (not duplicating) a few seconds of progress.

**Verified:** Started the sandbox's local `postgresql-16`, created a scratch
database, applied `infra/migrations/001..004_*.sql` unmodified, then hand-ran
the *exact* SQL text added to `Program.cs` (not a paraphrase) through six
steps: (1) a normal claim on an unowned character succeeds, (2) a second
world's claim while the first still owns it returns 0 rows *and* leaves
ownership untouched — confirming the exploit path is actually closed, (3)
the gateway's owner-lookup fallback correctly reports the current owner for
the 409 response, (4) a same-world reconnect claim is idempotent, (5) a save
correctly releases ownership only when the caller matches the current owner,
(6) a genuinely new character can then be claimed by the second world after
release. All six matched the expected before/after state. Full transcript and
the SQL script are not retained beyond this session (scratch DB dropped after
verification) — the SQL is reproducible directly from the `Program.cs` diff.

**Not verified:**
- The C# compiles. I read every changed line by hand for type/signature
  correctness (parameter counts, `NetPeer`/`KeyValuePair` nullability,
  `ImplicitUsings`/`System.Linq` availability for `.FirstOrDefault`) but this
  is not a substitute for `dotnet build`.
- The world-server, gateway, and client running together end-to-end. No
  Godot editor, no `dotnet run`, no Docker Compose stack in this environment.
- `dotnet test tests/sim-core.tests` — untouched by this change (only
  `apps/gateway` and `apps/world-server` were edited) but not re-run to
  confirm it still passes, since the SDK isn't present here.
- The mobile client build/launch. Per the hard constraints: **saying so
  loudly, as required** — nothing about tonight's change has been confirmed
  to build or run on-device or in-editor.
- Two simulated clients, one joining late, per the Definition of Done's
  multiplayer test bar — not possible without a running world-server process.

**Rejected tonight:**
- Phase 2 (new feature) — the brief says skip it entirely once any finding
  crosses the fix-it threshold, and this one did by a wide margin (25 vs. the
  ≥9 bar for multiplayer correctness).
- Fixing the `DeterminismTests.cs` gap instead (also scored 25) — same
  category-priority reasoning as above; it's now `BACKLOG.md` #1.
- Fixing the fast-reconnect save-ordering race (`BACKLOG.md` #12) in the same
  pass — related but distinctly lower severity (score 6), and doing it
  properly wants a version/timestamp column, which is more surface area than
  "fix exactly one thing" calls for on a night I couldn't compile-test either
  change.

**Added to backlog:** 19 items beyond tonight's fix, ranked in `BACKLOG.md`.
Highest: the `DeterminismTests.cs` golden-value gap (25), blocking gateway
HTTP calls with no timeout stalling every player (20), and the `World3D.cs`
god-script continuing to grow past where the roadmap measured it (16).

**Question for the human:** None blocking. But please run `dotnet build` and
`dotnet test tests/sim-core.tests` before trusting tonight's change beyond
the SQL-level verification above — this environment had no SDK to do it
myself.
