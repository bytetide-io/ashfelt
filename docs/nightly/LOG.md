# Nightly log

What each night did, and why. Newest first.

---

## 2026-08-23 — FEATURE (campfire fuel)

**Chose:** Campfires now burn fuel instead of providing warmth forever once
placed — they start lit with the wood spent crafting them, drain a tick at a
time, and a player taps a cold one (holding Wood) to feed and relight it,
using the same tap-to-interact gesture as harvesting.

**Because:** First-ever run of this nightly process on this repo, so Phase 1
was a from-scratch audit (`docs/nightly/{LOG,BACKLOG,ARCH}.md` didn't exist —
created this session; see `ARCH.md` for the map and `BACKLOG.md` for every
finding and its score). Nothing scored ≥15, and nothing multiplayer-touching
scored ≥9, so the brief's mandatory-fix threshold wasn't hit and I moved to
Phase 2. The codebase itself is unusually disciplined for a project this
size — server-authoritative movement, atomic voyage-ownership SQL, a shared
sim-core, real determinism tests — so the audit surfaced maintainability debt
(two client god-scripts, a dead `structure.health` column, no netcode test
harness) rather than a bug worth an emergency fix. All three are in
`BACKLOG.md` with scores for a future night.

For the feature: a permanent, free campfire was the biggest "systemic
realism" gap next to the warmth meter that already exists — night is
supposed to be a real threat, but once anyone places one campfire the threat
is solved forever, alone or in a crowd. Making fire a resource that runs out
gives players a new verb (feed/stoke), makes an existing verb (build
campfire) meaningfully persistent-cost rather than one-time, and turns a
shared campfire into the game's first real cooperation-or-free-ride
tension — everyone sheltering by one fire benefits, but only if someone
keeps feeding it.

**Rejected tonight:**
- *A storage chest structure* — new placeable, but it only talks to the
  inventory system that already exists; a content addition wearing a
  system's clothes, exactly what the brief says to reject.
- *Wall integrity from support* — a genuinely systemic idea (a wall with
  nothing under it should eventually fail) that reuses `Structure` the same
  way fire fuel does, but it touches building broadly enough that doing it
  properly in one session risked a half-finished mechanic. Logged to
  `BACKLOG.md` as a natural next night once fire fuel has proven the pattern.

**Changed:**
- `packages/sim-core/FireRules.cs` (new) — burn/fuel constants and which
  items burn.
- `packages/sim-core/World.cs` — `Structure` gains `FuelTicks` (+ computed
  `Lit`); `TryPlace` seeds fuel on a burning structure; new `TryFeed`,
  `AdvanceFires`; `HasWarmthNear` now requires a burning structure to be lit.
- `packages/shared-proto/Protocol.cs` — new `FeedFireRequest` (client→server)
  and `StructureFuelChanged` (server→client, sent only on a lit/unlit
  transition — never per tick); `StructurePlaced` gains a trailing `lit`
  byte. `ProtocolVersion.Current` 10 → 11.
- `apps/world-server/Program.cs` — handles `FeedFireRequest`; tick loop
  drains fuel via `World.AdvanceFires` and broadcasts extinguish events;
  `WriteStructurePlaced` sends the new `lit` byte.
- `apps/world-server/WorldStore.cs` — persists `fuel_ticks` on structure
  insert; new `SaveStructureFuelAsync` for a feed or an extinguish (not
  written every tick — only on those two transitions).
- `infra/migrations/005_fire_fuel.sql` (new) — `structure.fuel_ticks`
  column, default `FireRules.InitialFuelTicks` so already-placed campfires
  come back lit rather than silently dark the moment this ships.
- `apps/client/scripts/WorldConnection.cs` — `SendFeedFire`;
  `StructurePlaced` event gains `lit`; new `StructureFuelChanged` event.
- `apps/client/scripts/world3d/World3D.cs` — tracks campfires by id
  (tile + lit state); the existing tap-to-gather reticle now also offers
  "TAP TO STOKE" on the nearest unlit campfire in reach when nothing
  harvestable is closer; toggles the campfire's light/flame emission on a
  lit/unlit transition.
- `apps/client/scripts/world3d/SurvivalHud.cs` — `ShowGatherPrompt` takes a
  `label` parameter so the reticle can say "TAP TO STOKE" instead of "TAP TO
  GATHER" for the same UI element.
- `tests/sim-core.tests/FireTests.cs` (new) — placement seeds fuel, feed
  adds fuel and caps at `MaxFuelTicks`, feeding a non-burning or absent
  structure is refused, `AdvanceFires` drains and reports an extinguish
  exactly once, reignition is reported, `HasWarmthNear` tracks lit state.
- `tests/sim-core.tests/ItemCatalogTests.cs` — one existing test
  (`Campfire_ProvidesWarmth_...`) constructed a `Structure` directly with the
  new `FuelTicks` field defaulting to 0 (unlit), which would have failed
  under the new rule; fixed to load it lit, since the test is about the
  warmth radius, not fuel — this is a real behavior change (a campfire is no
  longer unconditionally warm), not a determinism-test-style workaround.

**Risk:** The `StructurePlaced` and `FeedFireRequest`/`StructureFuelChanged`
wire changes require both ends on the same build — that's what the
`ProtocolVersion` bump to 11 is for; a stale client or server gets rejected
at `Hello` instead of desyncing silently. `SaveStructureFuelAsync` is
fire-and-forget like every other world write, so a fuel level can be up to
one feed-or-extinguish stale after an ungraceful crash — acceptable, same
trade-off the harvest-diff and inventory writes already make. Feature-flag:
there isn't a boolean flag, but the change is confined to `FireRules.Burns`
(only `ItemId.Campfire`) and is trivially revertible by reverting this
commit plus the migration (the column is additive and unused by old code, so
even leaving the migration applied after a revert is harmless).

**Verified:** Read every changed file end-to-end after editing and traced
each new code path by hand: `TryPlace` → `Structure` construction → wire
write → client parse → HUD reticle → tap → `FeedFireRequest` →
`World.TryFeed`/`AdvanceFires` → broadcast → client visual toggle. Found and
fixed two real bugs this way before they could ship: a `record with`
expression used on a plain value tuple (`_campfires`) — tuples don't support
`with`, that would not have compiled — and the pre-existing
`ItemCatalogTests` test above that the new rule would have broken. Checked
every other call site of `Structure(...)`, `StructurePlaced`,
`ShowGatherPrompt`, and `HasWarmthNear` across the repo for the same class of
break; found none.

**Not verified — a human needs to check this on a real device/build:**
**This sandbox has no .NET SDK installed and no outbound network to install
one** (the HTTPS proxy returned 403 for `dot.net`), so **nothing in this
change has been compiled, let alone run** — not `dotnet test
tests/sim-core.tests`, not `dotnet build` on any project, not the client in
the Godot editor, not two simulated clients joining a shared campfire. Before
trusting this: run `dotnet test tests/sim-core.tests` (should show the new
`FireTests` passing and `ItemCatalogTests` still green), run two world-server
clients against one server, place a campfire, let it burn out, confirm the
warmth meter actually starts draining once cold and that "TAP TO STOKE"
appears and works from a second client, and confirm a restart replays the
persisted fuel level correctly via `LoadStructuresAsync`.

**Question for the human:** None blocking — the one thing worth flagging
proactively is that this session could not build or run anything, so the
usual "build and launch for the mobile target" verification in the brief
did not happen; treat this diff as reviewed-by-reading only until CI or a
local build confirms it compiles.
