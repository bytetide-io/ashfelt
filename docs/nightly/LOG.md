# Ashfall — nightly engineer log

## 2026-08-01 — AUDIT + FIX

**Chose:** Added shared-secret authentication (`X-Ashfall-Key`, env
`ASHFALL_GATEWAY_KEY`) to the gateway's `/characters/*` and `/voyage*`
endpoints; `/health` and `/worlds` stay open (the client's travel menu reads
`/worlds` directly).

**Because:** First-ever audit of this repo (`docs/nightly/{LOG,BACKLOG,ARCH}.md`
did not exist before tonight). Full ranked findings are in `BACKLOG.md`.
Highest-scoring finding: **no authentication on the gateway REST API**
(Severity 5 × Blast radius 4 = 20) — `apps/gateway/Program.cs` accepted any
`GET`/`PUT /characters/{id}` and any `POST /voyage`/`/voyage/claim` from
anyone who could reach the port, with no check that the caller was a trusted
world-server. That's a direct hole in invariant #3 (character state lives in
the gateway, world-servers only relay it) and in the voyage design doc's own
claim that "the client is never trusted with character state during handoff"
— true for the wire ticket, but nothing stopped a third party from hitting
the HTTP API directly and reading or overwriting *any* character's inventory
and survival meters by UUID, or minting/claiming voyage tickets for a
character they don't control. Two other findings tied at 16 (interest
management unimplemented; blocking gateway calls stalling the tick loop) but
scored lower than this one and the decision rule is fix exactly one thing —
this one has the largest blast radius (any character, not just a corner case
of the tick loop) and doesn't overlap with the other two, so future nights
can pick either of those up cleanly.

**Changed:**
- `apps/gateway/Program.cs` — auth middleware gating `/characters*` and
  `/voyage*` on a fixed-time-compared shared secret; `/health`/`/worlds`
  unauthenticated by design.
- `apps/world-server/GatewayClient.cs` — constructor now takes the key and
  sends it as `X-Ashfall-Key` on every request.
- `apps/world-server/Program.cs` — reads `ASHFALL_GATEWAY_KEY` (default
  `ashfall`, same convention as the existing `ASHFALL_CONNECT_KEY`) and
  passes it to `GatewayClient`.
- `apps/gateway/API.md` — documents the new required header and which
  endpoints it covers.
- `docs/nightly/ARCH.md`, `docs/nightly/BACKLOG.md` — created (did not exist
  before tonight); `ARCH.md` is an honest map of the repo as of tonight,
  `BACKLOG.md` has the full ranked audit list minus this fix.

**Risk:** Both sides default the key to `"ashfall"` for local dev, matching
the existing `ASHFALL_CONNECT_KEY` pattern — so a fresh `docker compose up` /
`dotnet run` setup keeps working unmodified. The real risk is operational:
anyone running the gateway and world-server in a shared or production
environment **must** set `ASHFALL_GATEWAY_KEY` to a real secret on both
processes (matching values) or the default is a known, public secret and
provides no protection at all — this is the same trade the UDP connect key
already made, not a new one, but it's worth a human confirming the deploy
config actually overrides both defaults before this goes anywhere
non-local. If the two processes' keys ever drift (typo, one redeployed
without the other), every character load/save and every voyage will start
returning 401 — loud and immediate, not a silent corruption, but it will
look like an outage. Watch world-server logs for `EnsureSuccessStatusCode`
exceptions from `GatewayClient` if that happens.

**Verified:** Read every touched file back after editing and traced the
control flow by hand: the middleware runs before route mapping and checks
`Path.StartsWithSegments`, which matches ASP.NET Core minimal-API convention
elsewhere in this file; `GatewayClient`'s only call site (`apps/world-server/Program.cs`)
was updated to match the new two-arg constructor; grepped the repo for any
other `GatewayClient` construction or direct calls to `/characters`/`/voyage*`
and found none outside `GatewayClient.cs` itself. `CryptographicOperations.FixedTimeEquals`
requires equal-length spans, so the length check before it is load-bearing,
not redundant — confirmed by reading the BCL contract by hand (couldn't run
it).

**Not verified:** **I could not build or run anything tonight** — this
sandbox has no `dotnet` SDK installed (`dotnet` is not on `PATH`, no
`/usr/share/dotnet`). I could not run `dotnet build`, `dotnet test
tests/sim-core.tests`, or start the gateway/world-server to smoke-test an
actual `Hello` → `GetCharacterAsync` round trip end to end, and could not
open the Godot client at all. This is a change to server-to-server C# code
only — the sim-core rules, client scripts, and shared-proto wire format are
untouched, so no determinism test should be affected — but a human (or CI)
needs to confirm `dotnet build apps/gateway` and `dotnet build
apps/world-server` actually compile before trusting this. **CI on the PR is
the first real verification this change gets** — I am watching it and will
fix anything it flags.

**Rejected tonight:**
- Requiring `ASHFALL_GATEWAY_KEY` with no default (fail loudly like
  `ASHFALL_DB`) — rejected because it would break the README's three-command
  local dev flow (`dotnet run --project apps/world-server` with no gateway
  key set) for a security property that's already no worse than the existing
  `ASHFALL_CONNECT_KEY` default; consistency with that existing convention
  won over stricter-by-default here.
- Protecting `/worlds` behind the same key — rejected because the client
  calls `GET /worlds` directly (`apps/client/scripts/WorldConnection.cs:238`)
  to populate the travel menu; locking it down would mean handing the client
  the server-to-server secret, which defeats the point.

**Added to backlog:** Full ranked list in `BACKLOG.md`. Highlights: interest
management (invariant #6) is unimplemented (score 16); the gateway handshake
blocks the world-server tick loop (score 16); voyage/character persistence
has zero test coverage (score 12); fire-and-forget DB writes swallow errors
silently (score 12); no migration version tracking (score 9);
`SurvivalHud.cs`/`World3D.cs` god-scripts (score 8 each).

**Question for the human:** None blocking — but please confirm CI is green
on the PR before merging, since I couldn't build locally tonight.
