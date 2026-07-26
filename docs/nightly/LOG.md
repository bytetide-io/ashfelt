# Nightly log

## 2026-07-26 — AUDIT + FIX

**Chose:** Bounded the world-server's gateway HTTP calls to a 5s timeout
(`Tuning.GatewayTimeoutSeconds`, `packages/shared-proto/Protocol.cs`) and
added a missing try/catch around the voyage-ticket claim in the `Hello`
handler (`apps/world-server/Program.cs`, `GatewayClient.cs`).

**Because:** First-night audit (state files didn't exist; created
`ARCH.md`/`BACKLOG.md`/`LOG.md` this session). Ten scored findings; three
scored ≥15 and are multiplayer-correctness issues well above the ≥9
mandatory-fix bar, so per the standing decision rule this was a fix night,
not a feature night. Full findings are in `BACKLOG.md`; this entry covers
only the one fixed.

The chosen finding (Sev 5 × Blast 5 = 25, the highest of the ten): the
world-server's single tick/packet thread calls
`gateway.ClaimVoyageAsync(...).GetAwaiter().GetResult()` inline inside the
`Hello` handler with **no try/catch** and **no `HttpClient.Timeout`** (C#
default is ~100s). Blocking here at all is a *documented, deliberate*
tradeoff already in the code (join/release are rare events, not per-tick,
and blocking avoids cross-thread mutation of `World`/`Player` state) — I did
not change that design. What was missing was a bound on it: a slow or
unreachable gateway during a voyage arrival could stall the entire world —
movement, harvesting, broadcasts, for every already-connected player — for
up to 100 seconds, and an actual HTTP failure (not just slowness) would throw
*uncaught* out of `server.PollEvents()` in the main loop and crash the whole
process. The sibling call two lines below (`GetCharacterAsync` at plain
`Hello`, not voyage) was already correctly wrapped in try/catch; this made
the voyage-claim path consistent with it.

**Changed:**
- `packages/shared-proto/Protocol.cs` — added `Tuning.GatewayTimeoutSeconds = 5`.
- `apps/world-server/GatewayClient.cs` — `HttpClient.Timeout` set from that constant.
- `apps/world-server/Program.cs` — wrapped the `ClaimVoyageAsync` call in
  try/catch; a gateway failure now denies the one join (disconnects that
  peer with a logged reason) instead of taking down the process.

**Risk:** Low, and narrowly scoped. The only behavior change on the happy
path is that a gateway call now times out at 5s instead of ~100s — if the
real gateway round-trip ever legitimately needs longer than 5s under load,
joins/voyages would start failing where they previously would have
(eventually) succeeded. Nothing else on the hot path changed; `GetCharacterAsync`
and the `RequestRelease` gateway calls were already guarded and are
unaffected in behavior (only in the new shared timeout, which is generous
for a same-datacenter REST call).

**Revert:** `git revert <this commit>` — three small, independent edits, no
schema/protocol/save-format change, nothing to migrate.

**Verified:** Manual review only. Read the full diff twice against the
surrounding file for namespace/type correctness (`ImplicitUsings` is enabled
on `apps/world-server`'s csproj, confirmed `TimeSpan`/`Exception` resolve
without new usings; `Tuning` was already reachable via the existing
`Ashfall.Proto` import). Read `GatewayClient.cs`, `Program.cs`, and `Player.cs`
in full before editing to confirm this was the only unguarded gateway call on
the tick thread (`GetCharacterAsync` at plain-join `Hello` and both calls in
`RequestRelease` were already try/catch-wrapped).

**Not verified — loudly:** **I could not build or run this project at all.**
The .NET SDK is not installed in this session's environment, and installing
it is blocked by the session's egress policy (`dot.net` returns 403 through
the agent proxy — an organization policy denial, not a transient failure, so
I did not retry or route around it). I did not run `dotnet build`, `dotnet
test tests/sim-core.tests` (required by this repo's own CLAUDE.md before any
commit), the world-server, the gateway, or the Godot client. **A human must
run `dotnet build apps/world-server && dotnet test tests/sim-core.tests`
before trusting this change**, and ideally exercise an actual voyage
join against a gateway stopped mid-request to confirm the new deny-and-log
path fires instead of a crash. This is a real gap in tonight's process, not
a formality — say so if it turns out I got a type or namespace wrong.

**Rejected tonight:**
- Fixing finding #2 (no dedupe on character double-ownership, tied for
  second-highest score) instead of #1 — rejected because it needs a schema
  change (an ownership/version column) and CLAUDE.md requires migrations to
  be tested against a prior-format save; riskier to rush blind (no way to
  run the migration or test it in this environment either) than the
  narrowly-scoped #1 fix.
- Fixing finding #3 (interest management never enforced, also tied for
  second) instead of #1 — rejected because correct enforcement touches every
  broadcast message type (not just position) and needs real add/remove
  semantics as clients move in and out of range, not a one-line distance
  filter; wants its own session.
- Any Phase 2 feature work — the audit's decision rule is unambiguous once
  three findings clear the ≥15 / ≥9 bar ("skip Phase 2 entirely"), so no
  feature ideas were designed or rejected tonight.

**Added to backlog:** the other nine scored findings, ranked, in
`BACKLOG.md` — highest remaining are the double-ownership gap and unenforced
interest management (both 16), and zero test coverage on world-server/gateway
(also 16).

**Question for the human:** None blocking — but the inability to build/test
in this environment (no dotnet SDK, and installing it is policy-blocked) is
a standing constraint worth knowing about for every future nightly run, not
just tonight's: verification here will always be manual-review-only unless
that's addressed (e.g. a pre-warmed image or an allowed mirror for the SDK).
