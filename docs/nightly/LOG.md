# Ashfall — nightly log

One entry per unsupervised nightly run, newest first.

---

## 2026-08-25 — AUDIT-ONLY

**Chose:** Full read-only audit of the repo (first nightly run — no prior
`docs/nightly/*` existed); created `LOG.md`/`BACKLOG.md`/`ARCH.md`. No `.cs`
files touched.

**Because:** The audit's one clearly-≥15 finding — `apps/world-server` and
`apps/gateway` have zero automated tests, unlike every rule in `sim-core` —
would normally be fixed the same night per the audit's own decision rule.
It wasn't, because **this sandbox has no working .NET SDK**: `dotnet` isn't
on `PATH`, and `apt-get install dotnet-sdk-{8,10}.0` and
`curl https://dot.net/v1/dotnet-install.sh` both failed (404s through the
package mirror, 403 from the proxy on the install script). `CLAUDE.md`
requires `dotnet test tests/sim-core.tests` to pass before any commit, and
that command cannot be run here. Shipping unverified C# — even additive
test code — risks leaving the repo unbuildable for whoever opens it next,
which is a worse outcome than one quiet audit-only night. See `ARCH.md` →
Toolchain note and `BACKLOG.md` #1 for the exact test plan to run the first
night a toolchain is available.

**Changed:** `docs/nightly/LOG.md`, `docs/nightly/BACKLOG.md`,
`docs/nightly/ARCH.md` (all new, docs-only).

**Risk:** None — no source files changed. The only risk is these notes
going stale if a future night doesn't update them.

**Revert:** `git revert <this-commit-sha>` (docs-only, trivially safe).

**Verified:** Read every file listed in `ARCH.md`'s repo map; grepped the
whole tree for `DateTime.Now`/`new Random(`/`GetNode(` misuse (none outside
`sim-core`, none of the GDScript-style string-path coupling the checklist
warns about — this project is C#, not GDScript); read the movement,
persistence, voyage-handoff and message-dispatch code paths end to end;
confirmed no prior nightly history exists (`git log` back to the first
commit). Confirmed, three separate ways, that no .NET toolchain is reachable
in this sandbox.

**Not verified:** Nothing was compiled, tested, or run — not `dotnet build`,
not `dotnet test`, not the client in Godot. Everything in `ARCH.md` and
`BACKLOG.md` is a static-reading judgment, not a measured one (explicitly
flagged where that matters, e.g. mobile perf numbers). A human should run
`dotnet test tests/sim-core.tests` and open the client in Godot before
trusting anything here beyond "this compiled and passed as of the last
human commit."

**Rejected tonight:** Writing `tests/world-server.tests` anyway and hoping
it compiles — rejected because an unverified commit that might not build is
a worse morning surprise than an audit-only night. Fixing #3 (Hello
handshake blocking the tick loop) — rejected because it's a documented,
deliberate tradeoff (`Program.cs:114-118`), not a silent bug, and
undoing/restructuring a past decision without new evidence violates this
routine's own rule against silently reverting prior work.

**Added to backlog:** All 7 items in `docs/nightly/BACKLOG.md` — top of the
list is the world-server/gateway test gap (score 15, blocked on toolchain
only), then gateway integration-test coverage and the Hello-blocking
tradeoff (12 each), then the two client god-scripts (6, 4 — already known
to `docs/gameplay-roadmap.md` §3.4, just re-scored and re-measured since
both have grown), then unverified mobile perf (6) and two unused DB columns
(2).

**Question for the human:** Can the nightly sandbox image be given a working
.NET SDK (vendored into the image, or point at a mirror that actually
serves the `dotnet-sdk-10.0` package `apt-cache` already knows about)?
Without one, every future audit-only night that finds a ≥15 code fix will
hit the same wall and can only document, not ship.
