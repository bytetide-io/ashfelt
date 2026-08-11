# Ashfall nightly log

Newest entry first. See `BACKLOG.md` for ranked open findings and `ARCH.md`
for the current honest architecture map.

---

## 2026-08-11 — AUDIT + FIX

**Chose:** Added periodic character autosave in the world-server tick loop
(`Tuning.CharacterAutosaveTicks`, every 60s), alongside the existing
disconnect/voyage-release save paths.

**Because:** Full audit (see scored table below) turned up two findings tied
at score 20 (Severity × Blast radius) and two more at score 16 that are also
multiplayer-correctness findings ≥9 — both conditions independently trigger
"must fix tonight, skip the new-feature phase" per the nightly process. Of
the two score-20 findings, missing character autosave was the one safely
shippable and verifiable in a single unsupervised session: it's additive
(new save path alongside existing ones, no protocol/schema change), low
blast-radius-if-wrong (worst case is a redundant PUT), and directly closes a
real data-loss gap — previously, character inventory/survival state for a
still-connected player was **only** persisted on graceful disconnect or
explicit voyage release, so any ungraceful crash (OOM, `kill -9`, host
failure) lost every online player's progress since they joined, even though
world-side tile diffs and structures already survive a restart. The other
score-20 finding (interest management never implemented, invariant #6
violated outright) needs a real netcode redesign — per-player
subscription/dirty-tracking across every `Broadcast()` call — that isn't safe
to design, build, and self-verify with no multi-client testing rig and no
human review before morning. It's logged in `BACKLOG.md` at the top of the
list instead of attempted half-done.

**Full audit — top findings (score = Severity × Blast radius):**

| Score | Finding |
|---|---|
| 20 | Interest management not implemented (invariant #6 violated) — **not fixed tonight, see above** |
| 20 | No periodic character autosave — **fixed tonight** |
| 16 | `ChopRequest` has no rate limit — scriptable resource-farming exploit |
| 16 | Synchronous gateway HTTP calls in the tick loop stall every player on any one player's join |
| 12 | Placed structures never block movement (dead `PlacementRules.Blocks`) |
| 12 | No death/respawn handling (`IsDead` never read) |
| 12 | Confirmed god scripts: `World3D.cs` (735 lines), `SurvivalHud.cs` (940 lines) |
| 12 | No chunk streaming — client world is a fixed radius around the origin |
| 12 | No DB migration runner for already-provisioned deployments |
| 9 | Fire-and-forget world writes (diffs/structures) swallow exceptions silently |
| 9 | Full inventory-UI rebuild on every harvest strike (mobile perf) |
| 9 | No proof-of-possession beyond a raw character GUID |
| 6 | `HarvestRules`/`Noise`/`PlacementRules.Blocks` have no direct test coverage |
| 6 | Stamina system built but never spent (0 callers) |
| 4 | Item stack caps declared but never enforced |
| 4 | `Player.Rejections` tracked but never read |

Full list with file:line citations and severity/blast justification is in the
audit transcript; the actionable subset is carried forward into
`BACKLOG.md`. Sim-core (the deterministic rules layer) came out of the audit
as the healthiest part of the codebase — well-tested, no float/RNG drift
found.

**Changed:**
- `packages/shared-proto/Protocol.cs` — new `Tuning.CharacterAutosaveTicks`
  constant (`TicksPerSecond * 60`).
- `apps/world-server/Player.cs` — new `SaveInFlight` flag so a slow gateway
  can't pile up overlapping saves for the same character.
- `apps/world-server/Program.cs` — extracted the existing disconnect-save
  logic into a shared `AutosaveCharacter(player, reason)` local function;
  called it from both the disconnect path (unchanged behavior, just
  deduplicated) and a new periodic call in the tick loop, gated on
  `tick % Tuning.CharacterAutosaveTicks == 0` and `players.Count > 0`.
- `docs/nightly/{LOG.md,BACKLOG.md,ARCH.md}` — created (didn't exist before
  tonight); `ARCH.md` is an honest current-state architecture map, separate
  from and narrower than `docs/architecture.md`'s decided-invariants doc.

**Risk:** Very low. The change is purely additive — a new call site into an
existing, already-proven save path (`GatewayClient.SaveCharacterAsync`,
already used by the disconnect and release paths). Worst case on a bug here:
an extra/duplicate save PUT (idempotent upsert, harmless) or a save silently
failing and logging to stderr exactly like the pre-existing disconnect-save
failure path already does — no new failure mode, no protocol change, no
schema change. The `SaveInFlight` guard prevents autosave calls from piling
up if the gateway is slow or down. To spot a problem: watch world-server
stderr for `character autosave save failed for {id}` on a healthy gateway (it
should never appear), or watch for the gateway not receiving a PUT roughly
once a minute per connected character.

**Revert:** `git revert` the commit on `claude/relaxed-pascal-4svrd2` titled
"world-server: add periodic character autosave" — it is the only commit
touching game logic tonight, isolated from the docs/nightly/ commit.

**Verified:**
- Read the full diff by hand; the new local function follows the exact
  snapshot-then-fire-and-forget-with-catch pattern already used by the
  pre-existing disconnect-save code, so it inherits that path's correctness
  (already exercised in this codebase, per `docs/voyage-transfer.md`'s
  recoverability requirement for character handoff).
- Confirmed via `grep` that `Tuning.CharacterAutosaveTicks` is the only new
  constant referenced, and that both call sites (disconnect, tick loop) are
  reachable and gated correctly by reading the surrounding control flow.
- Confirmed the CI workflow (`.github/workflows/ci.yml`) runs
  `dotnet build apps/world-server` and `dotnet test tests/sim-core.tests` on
  every push/PR, so the pushed branch gets a real compiler pass this session
  couldn't run locally.

**Not verified — a human must check this on a real device/session:**
- **The change was never compiled or run.** This sandbox has no `dotnet` SDK
  installed, and the outbound network policy blocks the domains the
  official install script needs (`dot.net`, `builds.dotnet.microsoft.com`
  both returned proxy 403s when I tried). I could not run
  `dotnet build apps/world-server`, `dotnet test tests/sim-core.tests`, or
  launch the world-server at all. I reviewed the diff by hand for syntax and
  type correctness against the existing patterns in the file, but this is
  not a substitute for a compiler. **Do not treat this as shipped until CI
  is green on the PR, or until you've built it locally.**
- No multi-client test (2+ simulated clients, one joining late) was run —
  same reason. The autosave path reuses an already-tested save call, but the
  new periodic-call wiring in the tick loop itself is unexercised.
- Mobile client build/launch not touched or verified tonight (no client code
  changed).

**Rejected tonight:**
- *Fixing interest management (the other score-20 finding) instead.* Higher
  raw score, but it's a multi-file netcode redesign (every `Broadcast()`
  call needs per-player scoping) that I cannot safely verify without a
  multi-client rig and human review before morning. Logged at the top of
  `BACKLOG.md` instead of shipped half-verified.
- *Fixing the chop-request rate limit as well, since it's small.* The
  process is explicit — "fix exactly one thing" — and chop-spam farming,
  while real, is a lesser immediate risk than crash data-loss (requires an
  actively malicious client, whereas the autosave gap loses data on any
  ordinary crash). Left it as the top recommended pick for the next night.

**Added to backlog:** All 14 remaining audit findings not fixed tonight, with
scores and file:line citations — see `BACKLOG.md`.

**Question for the human:** None blocking. One FYI: I could not verify this
compiles (no dotnet SDK / no network access to install one in this sandbox)
— please confirm CI is green on the PR, or build locally, before trusting
this change.
