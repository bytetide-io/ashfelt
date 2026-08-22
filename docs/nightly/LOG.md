# Ashfall — nightly log

One entry per session, newest first.

---

## 2026-08-22 — AUDIT-ONLY (fix forced by score)

**Chose:** Closed an unauthenticated gateway endpoint that let any caller who
knows a character's device UUID — which every player already knows, it's their
own — directly set that character's inventory and survival meters via
`PUT /characters/{id}`, completely bypassing the world-server and every
harvest/craft/eat rule it enforces.

**Because:** Phase 1 audit finding, scored Severity 5 × Blast radius 4 = 20
(also ≥ 9 as a standalone multiplayer-correctness finding either way), which
this workflow's decision rule makes mandatory to fix tonight, skipping the
Phase 2 feature. The gateway's `/characters/{id}`, `/voyage` and
`/voyage/claim` routes had no authentication at all — they trusted every
caller to be a well-behaved world-server. But the client stores its own
character UUID locally (`user://character_id`, see `WorldConnection.cs`) by
design, and the gateway's own `API.md` documents the exact request shape
needed to call `PUT` on it. Any player could have opened the docs, read their
own UUID off their device, and given themselves infinite resources or maxed
survival stats with a single `curl` command — a total, trivial break of
invariant #1 (server-authoritative) and of the entire survival design pillar,
requiring no client reverse-engineering at all.

**Changed:**
- `apps/gateway/Program.cs` — added middleware requiring an `X-Ashfall-Key`
  header (matching `ASHFALL_GATEWAY_KEY`, default `"ashfall"` for local dev)
  on every route except `/health` and `/worlds`. Logs a warning on startup if
  running with the default key.
- `apps/world-server/GatewayClient.cs` — constructor now takes the gateway key
  and sends it as a default request header on every call.
- `apps/world-server/Program.cs` — reads `ASHFALL_GATEWAY_KEY` (same default,
  so local dev needs no new env var) and passes it to `GatewayClient`.
- `apps/gateway/API.md` — documents the new required header and the 401.
- `docs/architecture.md` — one paragraph under invariant #3 pointing at the
  trust boundary and the doc that explains it.
- `docs/nightly/{ARCH.md,BACKLOG.md,LOG.md}` — created (first nightly session;
  no prior state files existed).

**Risk:** Low for local dev — both sides default to the same key, so
`dotnet run --project apps/world-server` + `dotnet run --project apps/gateway`
keeps working unchanged. Real risk is anywhere the gateway is reachable
outside localhost with the default key still set: the fix only helps once an
operator sets a real `ASHFALL_GATEWAY_KEY` (the gateway now warns loudly on
boot if it isn't). Middleware ordering: `app.Use(...)` is registered before
the `app.MapGet`/`MapPost` calls, which is the standard, correct place for
gate-everything-after-this-point middleware in ASP.NET Core minimal APIs. If
this breaks, the symptom is every world-server failing to load or save
characters (`GatewayClient` calls throwing on a non-success status) — check
for a `401` in the gateway's logs first; it almost certainly means the two
processes have different `ASHFALL_GATEWAY_KEY` values (or one is unset).

**Revert:** `git revert` the commit on this branch, or by hand: drop the
`app.Use(...)` block and the `gatewayKey` lines in `apps/gateway/Program.cs`,
drop the `gatewayKey` parameter from `GatewayClient`'s constructor and the
`_http.DefaultRequestHeaders.Add(...)` line, and revert the one-line call site
in `apps/world-server/Program.cs`. No schema change, no wire-protocol version
bump — safe to revert independently of everything else.

**Verified:** Read every changed file back end to end and traced the
middleware/constructor/call-site wiring by hand (single call site for `new
GatewayClient(...)`, confirmed via grep). The ASP.NET Core `app.Use(async
(context, next) => ...)` middleware idiom, `PathString.StartsWithSegments`,
and `IHeaderDictionary.TryGetValue` usage all match well-known, standard
signatures. Read the full diff for scope: it touches exactly the files listed
above, nothing else.

**Not verified — could not verify, full stop:** **This sandbox has no .NET SDK
installed** (`dotnet` is not on `PATH`). I could not run `dotnet build`,
`dotnet test tests/sim-core.tests` (required before any commit per
`CLAUDE.md`), start the gateway or world-server, or confirm with a live
request that an unauthenticated call now gets `401` and an authenticated one
still succeeds. I did not touch anything in `sim-core`, so the determinism
tests are very unlikely to be affected by this change, but "unlikely" is not
"verified" — a human needs to run `dotnet test tests/sim-core.tests` and
`dotnet build` on this branch, and ideally exercise a real join/leave/voyage
against a local Postgres, before this is trusted in any real deployment.

**Rejected tonight:**
- Fixing the missing `owner_world_id` check on `PUT /characters/{id}` in the
  same change — related, but a strictly smaller hole (needs a second bug, a
  compromised/misbehaving world-server, to matter) and it's cleaner to keep
  tonight's diff to exactly the one thing that was actually exploitable by an
  ordinary player. Logged to `BACKLOG.md` #4.
- Adding per-action rate limiting for Chop/Craft/Place requests — a real gap
  (`BACKLOG.md` #1, scores 12) but requires a custom client to exploit, not a
  documented public API and a stock `curl`. Lower priority than the one fixed.
- Building out a full xunit test project for the gateway so this middleware
  had automated coverage — no gateway test project exists yet, and standing
  one up plus a `WebApplicationFactory` harness is a bigger, separate piece of
  work than tonight's fix; also I have no way to run it in this sandbox to
  confirm it's even green. Logged as a natural follow-up, not a numbered
  backlog item on its own (folds into whoever picks up #7 below).

**Added to backlog:** see `docs/nightly/BACKLOG.md` — no per-action rate
limit (12), god-scripts in the client (9), no server-side interest management
(9), missing `owner_world_id` check on character save (8), migrations don't
auto-apply to an existing deployment (6), `CharacterState` has no version
field (4), and this sandbox's missing .NET SDK blocking all verification
(unscored — it's an environment gap, not a codebase one).

**Question for the human:** Please run `dotnet test tests/sim-core.tests` and
`dotnet build` on `claude/relaxed-pascal-11i17g` before merging — I could not
run either tonight, and this is a security-relevant change to the gateway's
auth surface that I'd rather you double-check than trust on my read-through
alone.
