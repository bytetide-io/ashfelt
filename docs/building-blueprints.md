# Ashfall — blueprint building

A sophisticated, creative building system: the player designs a structure in a
free-camera **architect view**, commits it as a private **blueprint** anchored
in the world (a translucent "hallucination" only they see), then **builds it for
real** by feeding materials into a **buildground** that stockpiles them on site.
Others never see your plans — but they watch your building materialise as it is
constructed.

Companion to `architecture.md` (which owns the invariants) and
`gameplay-roadmap.md` (which owns the phased gameplay plan). This doc owns *how
building works and why it is shaped this way*. Single-player experience is the
priority; multiplayer visibility falls out of the existing interest-management
path almost for free and is not the focus.

## 1. Why this shape

The build system today is one item → one tile-locked `Structure`: flat, no
height, no rotation, no material variation, and `PlacementRules.Blocks` is a
hand-written switch. That is fine for a campfire and a wall; it cannot express a
*building*. Creative building needs modular pieces that snap together, tiers of
material, and a reason to invest effort over several sessions.

The design goal, per `CLAUDE.md`: every feature must make *surviving more
interesting*. A building is not decoration — a finished, enclosed, roofed
structure is **shelter**, and shelter is the answer to the existing night /
warmth pressure. Blueprints make the payoff *deliberate*: you plan the shelter
you need, then work toward it.

## 2. The three states of a piece (the visibility model)

Every building **piece** is in exactly one state, and the state decides who
sees it. The player's whole flow — *"only I see my plan, everyone sees the
progress"* — falls straight out of this table:

| State           | What it is                                   | Persisted?           | Who receives it                 |
| --------------- | -------------------------------------------- | -------------------- | ------------------------------- |
| **Draft**       | Being placed in architect mode (ghosts)      | No — client only     | The designer's client only      |
| **Pending**     | Committed hologram, not yet built            | Yes (blueprint rows) | **Owner only** (+ clan, future) |
| **Built**       | Real, solid geometry                         | Yes (world diff)     | **Everyone** in interest range  |

Consequences:

- Draft pieces never touch the wire, so a private plan *cannot* leak and the
  server carries no per-ghost churn.
- Pending pieces are streamed only to the owner. To a passer-by, the building
  **grows out of nothing** as pieces flip to Built — the owner has seen the full
  translucent plan the whole time.
- The owner→everyone flip on the Built state is a single predicate on the
  existing interest-management path (invariant #6). Clan sharing later is the
  *same* predicate widened from `owner` to `owner ∪ clan` — no new subsystem.

## 3. The building model

Move from tile-locked single structures to a **modular piece grid**
(Valheim/Rust-style, mobile-tuned).

- **Cell coordinate**: `BuildCoord(int X, int Y, int Level)`. `Level` is the
  storey. **v1 ships single-level** (`Level` always 0) so the snapping and
  support rules stay simple; the axis exists from day one so multi-storey is a
  later data + UX unlock, not a schema change.
- **Piece** = `PieceKind` × `Rotation` (0/90/180/270) × `Material`.
  - `PieceKind`: `Foundation`, `Floor`, `Wall`, `Doorway`, `Window`, `Pillar`,
    `Roof`, `Ramp` (v1 can ship a subset: Foundation, Wall, Doorway, Roof).
  - `Material`: `Thatch`, `Wood`, `Plank`, `Stone`, `Brick`, … (see §5).
- **Snapping** keeps touch building legible: walls snap to cell *edges*,
  foundations/floors/roofs to cell *faces*. A piece only previews where it can
  legally attach.
- **Support rules** (deterministic, in `sim-core`, tested): a wall needs a
  foundation/floor beneath its edge; a roof needs walls or pillars under it; an
  upper floor (later) needs support below. These validate live in architect mode
  (unsupported = red) **and** are enforced server-side at build time, which is
  what forces a sane build order.

### `StructureDef` — the new content type

All piece behaviour becomes one declarative table in `sim-core`, mirroring how
`ItemCatalog` already works for items (invariant #4). Adding a buildable piece =
one row.

```
StructureDef(
    PieceKind Kind,
    Material  Material,
    Footprint Footprint,        // 1x1 for v1
    bool      Solid,            // blocks movement (walls, closed doors)
    bool      ProvidesShelter,  // contributes to the enclosure/warmth check
    bool      IsStation,        // workbench/kiln/forge — gates recipes
    IReadOnlyList<MaterialCost> BuildCost,  // what building it consumes
    int       BuildStrikes,     // taps to raise it (reuses harvest strikes)
    Support   Requires,         // what must exist beneath/adjacent
    SnapType  Snap,             // Edge | Face | Cell
    Rotations Allowed,          // which rotations are legal
    int       Tier)
```

`PlacementRules.Blocks` collapses into `StructureDef.Solid`; the legacy Wall and
Campfire become rows here. Determinism contract: the table is static, ordered,
integer-valued, and covered by a test asserting every `BuildCost` / `Requires`
references real items and real pieces.

## 4. The buildground & construction loop

1. **Design** in architect view (client-only draft).
2. **Commit**: one `CommitBlueprint` message carries the whole plan (anchor +
   list of `(Kind, Material, X, Y, Level, Rotation)`). The server validates the
   footprint and support graph, computes the **bill of materials** (sum of every
   piece's `StructureDef.BuildCost`), and creates the build site with its own
   on-site **storage** and the pending pieces (owner-visible holograms).
3. **Deposit**: `DepositRequest` moves materials from your inventory into the
   buildground's storage — a stockpile that lives *at the building*, so you can
   haul over several trips and it persists.
4. **Build**: `BuildRequest` while standing in reach raises the next affordable
   piece in support order. This **reuses the harvest strike mechanic exactly** —
   a piece takes `BuildStrikes` taps, wears *up* visually, and only the
   completing strike writes a diff and flips the piece to Built. `BuildProgress`
   broadcasts like `HarvestProgress` does today (owner sees pending→built,
   everyone sees the built result appear).

Active tap-to-build (not a passive timer) keeps the satisfying one-thumb loop
and makes the buildground a place worth returning to.

**Cancel / refund**: `CancelBlueprint` removes pending pieces and returns
deposited materials to the site storage for pickup. Built pieces remain (they are
real world state). Editing an existing blueprint = adding/removing pending pieces
before they are built.

## 5. Resource & material depth

The build loop is only as rich as what feeds it, so material depth ships
*before* the blueprint UI. Each addition is data, not code, once the registry
(§6) exists.

**New raw resources & gathering methods** — each a `HarvestNodeDef` row:

- **Ore veins** — mining, gated by pickaxe tier → Copper, Iron ore.
- **Clay** — dug near water. **Sand** — dug on beaches.
- **Reeds** — wetland grass → thatch.
- **Hardwood vs. softwood** trees — axe-tier gated, feed different tiers of plank.

**Refining stations** — each a `StructureDef` with `IsStation = true` that gates
recipes (the crafting-station upgrade `RecipeDef` already wants):

- **Workbench** — Wood → Plank → Beam.
- **Kiln** — Clay → Brick; Sand → Glass.
- **Forge** — Ore → Ingot → Nails / Fittings.
- **Loom** — Fiber → Rope → Thatch panel.

**Building materials tier the buildings** (this *is* the progression curve):
Thatch (cheap, flimsy) → Wood / Plank → Stone / Brick (durable) → Glass windows,
metal-reinforced doors.

## 6. Foundations that make it cheap (build these first)

Adding pieces/materials/recipes must not mean editing a switch in five files.
Three foundations, built before the gameplay:

1. **`StructureDef` registry** (§3) in `sim-core`; refactor Wall + Campfire onto
   it. `RecipeDef` gains a required crafting station.
2. **Typed protocol read/write helpers** in `shared-proto` (roadmap §3.2). The
   blueprint messages carry *lists of pieces*; a hand-written `Put`/`Get` scavenger
   hunt on both sides is a silent-wire-bug factory. Define each message's layout
   once, reused by client and server.
3. **Client decomposition** (roadmap §3.4). `World3D.cs` is already ~616 lines;
   architect mode will break it without splitting net-dispatch / terrain / local
   player / interaction first, and a message-dispatch table keyed by `MessageId`
   instead of a growing switch. Reuse the item-slot widget for the piece palette.

## 7. Protocol additions

Append-only (never renumber), bump `ProtocolVersion`. All using the §6.2 helpers.

- `CommitBlueprint` (C→S): anchor + piece list. Server validates, computes BoM,
  creates the build site.
- `BlueprintState` (S→owner): full pending plan + per-piece build state + storage
  manifest. Backfilled on join, interest-filtered to the owner.
- `DepositRequest` (C→S): move materials inventory → site storage.
- `BuildRequest` (C→S): advance construction at a targeted piece.
- `BuildProgress` (S→range): a piece progressed / completed. Pending-piece
  geometry only to the owner; the built result to everyone.
- `CancelBlueprint` (C→S): tear down pending pieces, refund to site storage.

## 8. Persistence

Additive tables only — terrain hashing untouched, so `DeterminismTests` stays
green (invariant #2/#5).

- `blueprint` — id, world_id, owner_character_id, anchor, status, created_at.
- `blueprint_piece` — blueprint_id, x, y, level, kind, material, rotation, built,
  chunk_x/chunk_y (for interest streaming).
- `build_site_storage` — blueprint_id, item_id, qty.

Built pieces are the world's real structures. v1 can extend the existing
`structure` table with material/level/rotation/kind, or introduce a
`building_piece` table and fold the legacy quick-place Wall/Campfire into it over
time. Pieces are chunk-indexed and streamed by proximity like structures today;
pending pieces get the owner-only filter.

## 9. Invariant fit

- **Server-authoritative** — commit, deposit, build are all validated requests;
  the client predicts, the server decides. ✅
- **Seed + diffs** — built pieces are diffs; blueprints are additive tables; full
  chunks are never persisted. ✅
- **One shared sim-core** — snapping, support, BoM live in `sim-core`, referenced
  by both sides. ✅
- **Interest management** — pieces are chunk-indexed and streamed by proximity;
  the owner-only rule is one predicate. ✅
- **Mobile, one-thumb** — architect mode is drag-pan / pinch-zoom / tap-place-
  rotate; commit sends the plan once, not per-ghost. ✅

## 10. Phasing (each phase shippable)

- **Phase 0 — Foundations, no new gameplay.** `StructureDef` registry + Wall/
  Campfire refactor; `RecipeDef` station gating; proto read/write helpers; client
  decomposition + message-dispatch table + reusable widgets. Tests for every def.
- **Phase 1 — Resource & material depth.** New nodes/methods, refining stations,
  new building materials (§5). Balance numbers into one `Tuning`-style config.
- **Phase 2 — Architect mode (client, single-player).** Piece grid + snapping +
  support validation; free camera + palette + ghost place/rotate/line-drag/
  delete/undo + live BoM tally. Pure client draft — design and see the hologram,
  no server yet.
- **Phase 3 — Commit, buildground, construction.** The loop closes:
  `CommitBlueprint`, deposit, tap-to-build, `BuildProgress`, persistence, backfill,
  cancel/refund, edit-in-place.
- **Phase 4 — Payoff & polish.** Enclosure/roof detection → warmth/insulation
  bonus (ties buildings to night pressure); doors (open/close + collision);
  storage-container pieces; mobile UX pass on architect mode.

### Deferred (not now)

Clan-shared visibility (widen the owner predicate), saved/shareable blueprint
templates, cosmetic pieces, structure decay/repair.

## 11. Open questions to settle before Phase 2

- **Build reach vs. camera**: does tap-to-build require the *player body* in
  reach (consistent with harvest) while the architect camera roams free? (Assumed
  yes.)
- **Griefing limits** (multiplayer future, not v1): build-site count per player,
  overlap rules near others' structures.
- **Snap ergonomics on a small screen**: line-drag placement and rotation gestures
  need a real device pass in Phase 2.
