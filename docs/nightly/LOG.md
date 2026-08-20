# Ashfall — nightly log

Chronological record of what each unsupervised night did and why. Newest
entry on top.

---

## 2026-08-20 — FEATURE

**Chose:** Campfires now require tending — a placed campfire starts unlit;
feeding it Wood (`FeedFireRequest`) catches it alight for a few minutes per
log, and only a lit fire counts toward night-warmth (`World.HasWarmthNear`).
An unattended fire burns out and stops warming.

**Because:** Ran the audit first (see `docs/nightly/BACKLOG.md` for the full
scored table). Nothing crossed the mandatory-fix bar (≥15, or ≥9 on a
multiplayer-correctness finding) — the codebase is genuinely solid;
`docs/gameplay-roadmap.md` already had scoped, unexecuted plans for the
maintainability debt I found, so re-deciding those plans myself uninstructed
felt like second-guessing a call already made rather than fixing a live bug.
Moved to Phase 2.

For the feature: `docs/gameplay-roadmap.md` Phase A already named "functional
campfire...acts as a cooking station" as the next unclaimed slice, with
"night as pressure" and "tools matter" both already shipped. But a campfire
today is placed once and is warm forever — it's decoration wearing a warmth
number, not a system. Tending it: **increases freedom** (a new verb — spend a
held resource on a structure you don't own outright, reusing the exact
"spend an item near a target" shape `EatRequest`/`PlaceRequest` already use,
not a new mechanic bolted on); **increases realism through simulation** (a
fire that doesn't need fuel isn't simulated, it's decorated — this is the
routine's own "wet wood shouldn't burn" test, applied); **is
multiplayer-interesting** (one shared fire, finite wood, whoever's watching
it has to decide whether to feed it or keep gathering — cooperation and
mild tension that a solo player never experiences the same way, since a solo
player has to make the same tradeoff against their own idle time instead);
**is mobile-appropriate** (one new HUD button, shown only when relevant,
one tap); **composes with what exists** (crafting for the campfire itself,
inventory for the Wood cost, the existing warmth-radius/night-drain system
for the payoff — no parallel system introduced).

Rejected two other ideas before this one — see `docs/nightly/BACKLOG.md`
"Feature ideas considered and rejected tonight" for the full reasoning
(shared storage needed UI I can't verify without Godot; cooking has no raw
food to cook yet and tending is its actual prerequisite).

**Changed:**
- `packages/sim-core/FireRules.cs` (new) — deterministic fuel math (ticks per
  Wood, advance-by-one-tick, floored at zero). Tick-based, integer, no float
  accumulation — same determinism discipline as `SurvivalRules`.
- `packages/sim-core/World.cs` — `_fuel` dictionary keyed by structure id
  (transient, **not persisted** — same precedent as `_harvestStrikes`, see
  "Not verified" below for why), `FeedFuel`/`AdvanceFuel`/`IsLit`/
  `TryGetStructure`; `HasWarmthNear` now requires `IsLit`.
- `packages/shared-proto/Protocol.cs` — `FeedFireRequest` (client→server),
  `StructureFuel` (server→client, transition-only, never per-tick).
  **`ProtocolVersion.Current` bumped 10 → 11** (breaking wire change; a
  mismatched client is rejected at Hello, as designed).
- `apps/world-server/Program.cs` — handles `FeedFireRequest` (reach + target +
  Wood check, mirrors `PlaceRequest`'s shape); tick loop burns fuel down via
  `World.AdvanceFuel()` and broadcasts extinguish transitions; join backfill
  now also sends `StructureFuel` for any structure already lit, so a late
  joiner sees the true state, not just "a campfire exists."
- `apps/client/scripts/WorldConnection.cs` — `SendFeedFire`,
  `StructureFuelChanged` event, `StructureFuel` read in `OnReceive`.
- `apps/client/scripts/world3d/World3D.cs` — tracks tile/kind per structure id
  (`_structureInfo`, needed because the existing `_structures` dict only held
  the rendered node); resolves the nearest feedable structure in reach each
  physics tick (deliberately a **separate** target from the gather reticle —
  see "Risk" below for why); toggles a campfire's light/emission on the
  lit/unlit transition. Campfire now starts unlit (was always-lit before).
- `apps/client/scripts/world3d/SurvivalHud.cs` — new fixed-position "FEED
  FIRE" button above the hotbar, dimmed when the player holds no Wood.
- Tests: `tests/sim-core.tests/FireRulesTests.cs` (new, 3 tests), 5 new tests
  in `PlacementTests.cs` (unlit-on-place, catch-alight transition reporting,
  feeding a non-warmth structure is a no-op, extinguish-stops-warmth,
  advancing an unfed world reports nothing), updated
  `ItemCatalogTests.cs`'s existing campfire-warmth test (it placed a campfire
  via `LoadStructure` and asserted immediate warmth — now feeds it first,
  since that assumption changed). 92 tests total, 84 → 92.
- `README.md`, `docs/gameplay-roadmap.md` — Status/progress updated per the
  project's own documentation-duties rule.

**Risk:** The new "FEED FIRE" button is a fixed HUD element, not a shared
tap gesture with the existing "tap to gather" reticle — deliberate, to avoid
touching the gather/tap dispatch code (`World3D.cs` `_harvestQueued`/
`_gatherTarget`) that I have no way to visually verify. If that turns out
wrong in practice (e.g. the button overlaps the hotbar on a real device, or
its dimmed state doesn't read clearly against the design system's palette),
it's an isolated addition — delete `BuildFeedButton`/`ShowFeedPrompt`/
`HideFeedPrompt` and the two call sites in `World3D.cs`, nothing else
depends on it. The `ProtocolVersion` bump means an old client and a new
world-server (or vice versa) refuse each other outright at Hello rather than
desyncing silently — by design, but worth knowing if a deploy mixes versions.
Fire-fuel state is **not persisted** (matches the existing harvest-strike
precedent): a server restart mid-burn resets every campfire to unlit. This
is a deliberate scope cut, not an oversight — see "Not verified" for the
justification — but it's a real behavior a player could notice.

**Revert:** `git revert` the commit(s) on this branch, or `git diff main --
packages/sim-core/FireRules.cs packages/sim-core/World.cs
packages/shared-proto/Protocol.cs apps/world-server/Program.cs
apps/client/scripts/WorldConnection.cs
apps/client/scripts/world3d/World3D.cs
apps/client/scripts/world3d/SurvivalHud.cs
tests/sim-core.tests/FireRulesTests.cs tests/sim-core.tests/PlacementTests.cs
tests/sim-core.tests/ItemCatalogTests.cs README.md docs/gameplay-roadmap.md`
to see the exact surface before reverting by hand. Single, self-contained
change — nothing else in the tree depends on any of it.

**Verified:**
- `dotnet test tests/sim-core.tests` — 92/92 passing (was 84; +8 new).
- `dotnet build` on `world-server`, `gateway`, and `apps/client/AshfallClient.csproj`
  (the last one compiles headlessly via the `Godot.NET.Sdk` NuGet package —
  no Godot editor needed for this) — all clean, 0 warnings, 0 errors.
- **Real end-to-end multiplayer smoke test**, not just unit tests: built a
  throwaway LiteNetLib console harness (same networking library the real
  client uses) and ran it against the actual `world-server` binary over real
  UDP. Client A joined, gathered real Wood and Stone from terrain generated
  by the actual shared `TerrainGenerator`, crafted a campfire, placed it,
  confirmed feeding with no Wood is silently refused, walked back, fed it,
  and received the `StructureFuel(lit: true)` broadcast. Client B then
  joined **late** and received the campfire as already-lit purely from the
  join-backfill path (not the live broadcast) — proving the backfill code,
  not just the common-path broadcast. All checks passed. Harness was scratch
  and has been deleted, not committed.
- Manually re-verified every one of the ~19 wire messages in
  `shared-proto/Protocol.cs` (17 pre-existing + 2 new) byte-for-byte between
  writer and reader while doing the audit and again after this change — no
  mismatch found.

**Not verified:**
- **Nothing about the actual client UI was visually checked.** This sandbox
  has no Godot editor and no device — I cannot see the FEED FIRE button, its
  position, its dimmed-vs-enabled contrast against the design system's
  palette, or the campfire's lit/unlit visual transition (light + emission
  toggle) actually render. A human needs to open the project in Godot, place
  a campfire, and confirm: the button appears/disappears at the right
  distance, doesn't visually collide with the hotbar or other HUD elements
  on a real phone aspect ratio, and the campfire visibly goes from dark to
  lit when fed.
- Touch feel / one-thumb reachability of the new button — untested by
  construction (no touch input available here).
- Whether 3 minutes of fuel per Wood (`FireRules.FuelTicksPerWood`) is a
  *good* number for actual play pacing — chosen for "a fire wants occasional
  feeding, not one log lasting the whole night" but never played against a
  real night length (`Tuning.SecondsPerGameDay` = 600s = 10 minutes, so a
  full night is roughly half that — 5 minutes if night is half the cycle —
  meaning one log covers well over half a night unattended; this is a
  reasonable first guess, not a tuned number). Flagged, not fixed — CLAUDE.md
  says not to touch balance numbers unless balance is the explicit task.
- Fire-fuel persistence-on-restart tradeoff (see Risk) was a deliberate
  choice, not something I could load-test against a real Postgres restart
  scenario in this sandbox (no Postgres was running; `WorldStore` ran in its
  memory-only mode for the smoke test, which is exactly the code path that
  proves diffs/structures round-trip but says nothing about fuel, since fuel
  intentionally never reaches the DB).

**Rejected tonight:**
- Shared storage/chest structure — needs a new container-UI panel I can't
  visually verify here; better suited to a night with device access.
- Cooking (raw→cooked at the campfire) — no raw food exists yet to cook
  (creatures are Phase C); tending the fire is the actual prerequisite, so
  building it first is the correct order, not a dodge.
- "Add N new craftable items" — rejected on sight per the routine's own
  filter: content dressed as a system.

**Added to backlog:** Full scored table in `docs/nightly/BACKLOG.md`. Highest
open item: persistence has no version field and `TileType`'s enum ordinal is
stored raw with no "append-only" guard (unlike `ItemId`/`MessageId`, which
already have one) — cheap to fix now, expensive after a world ships.

**Question for the human:** None blocking. One flag: this sandbox has no
Godot editor or device, so every client-visual claim above is genuinely
unverified — worth a quick real playtest of the FEED FIRE flow before this
lands anywhere real players will see it.
