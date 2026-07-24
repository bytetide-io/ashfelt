# Ashfall — gameplay & foundation roadmap

The path from the current build to the full game: a mobile survival world of
countless materials, crafted gear and weapons, living creatures, authored cities
to loot, clans with shared bases, karma-governed PvP, vehicles, and a world that
keeps growing toward real-world scale.

Companion to `architecture.md` (which owns the invariants) and `voxel-terrain.md`
(which owns the cube/digging model). This doc owns **what to build, why, and in
what order.** Read the vision and invariants in `CLAUDE.md` and `architecture.md`
first — nothing here is allowed to break them without a recorded decision.

The organising principle has not changed: **spend each unit of effort making
survival a loop the player can win and want to repeat, and build the data-driven
foundations that make every subsequent mechanic a few rows of data instead of a
cross-cutting code change.** "Unlimited possibilities" is not a slogan here — it
is a direct consequence of getting four foundations right (content registry,
item instances, authored-content stamps, and the entity system) so that a new
gun, animal, ore, building or garment is *content*, never new engine code.

---

## 1. Where the game stands (2026-07)

The **plumbing is excellent and the game is thin, but thickening.** The
load-bearing problems are solved well:

- Server-authoritative loop, client prediction + reconciliation, deterministic
  `sim-core` shared across the boundary.
- Seed + diffs world storage; structures persisted and backfilled on join.
- Gateway-owned characters that survive reconnect; voyage handoff with
  single-ownership tickets.
- A data-driven **item catalog** (`ItemCatalog`): one `ItemDef` table is the
  single source of item behaviour, and `HarvestRules`/`PlacementRules` are
  lookups over it, not switches — adding content is adding data.
- The **core survival loop closes**: forage berries → eat (`EatRequest` →
  `SurvivalRules.Eat`); night drains a **warmth** meter unless you shelter by a
  lit campfire; a matching **tool** speeds and enriches harvest; nodes have
  durability and visibly wear down.
- Presentation has moved to **3D, low-resolution pixel-art** (surface-texel
  materials + a low-res `SubViewport` upscale) with the Ashfall design system and
  runtime-generated pixel icons. (Note: `CLAUDE.md`/`README` still say "2D" in
  places — that is stale; the client is 3D and stays 3D. See §2.3.)

What is still true: today's *content* is small. A handful of tiles, ten items,
six recipes, no creatures, no authored places, no gear depth, no social layer.
The rest of this document is the ladder from here to the full vision — and,
crucially, the foundations that make climbing it cheap.

---

## 2. The endgame vision — and the three tensions it creates

The target (from the project owner):

> Countless materials for construction, tools and weapons · animals & cooking
> ingredients · crafting stations (cooking stove, gunpowder lab, gun workbench) ·
> backpacks and clothing with capabilities · an authored, pre-built world that
> keeps extending toward real-world size · real cities/houses/abandoned
> constructions to loot for resources and gear · clans that team up and build a
> shared base · karma that discourages slaughtering the weak, with real effects ·
> better (still low-res, "roblox-kind") graphics · players spawn from a personal
> underground shelter that is their first safe store · the ability to grief/tear
> down buildings · a wide range of guns · cars and boats.

Three parts of this conflict with the written vision. Per `CLAUDE.md` we resolve
each explicitly rather than drift.

### 2.1 Guns vs. "not a shooter"

`CLAUDE.md`: *"Survival first. Not a shooter, not an MMO theme park."* Guns,
gunpowder labs and gun workbenches are in the endgoal. **Resolution: guns are a
scarce, late, expensive survival capability, not the core loop.** They sit at the
top of a long tech tree (ore → metalworking → machining → gunpowder chemistry →
a gun workbench), fire ammunition that must itself be crafted and is always
scarce, and degrade. The moment-to-moment game is still gather/craft/build/endure;
a firearm is a hard-won tool for defending a base or a voyage, and **karma (§ Phase
K) is the systemic counterweight** that keeps the world from becoming a deathmatch.
This keeps "survival first" honest while delivering the arsenal.

### 2.2 A pre-built world "the size of the real world" vs. seed + diffs and bounded worlds

`architecture.md` invariant #2 (world = seed + diffs, full chunks never
persisted) and the vision (*"bounded worlds, not one seamless map"*). The endgoal
wants an authored world with real cities that keeps extending to real-world scale.
**Resolution — authored content becomes deterministic *input* to generation, not
persisted chunks:**

- A city, house or ruin is a **prefab**: authored, versioned, content-addressed
  data compiled into `sim-core`. The generator *stamps* prefabs onto the terrain
  at authored anchor points. The prefab atlas is, in effect, **part of the seed** —
  the same anchors produce the same city on every device, so the invariant holds:
  we still persist only the seed (now: procedural seed + prefab atlas) plus sparse
  player diffs. No authored chunk is ever stored per-world; it is re-derived.
- "Real-world size" is reached by **adding bounded regions/world-servers over
  time**, linked by voyages — never by one seamless global map. The world grows by
  authoring and standing up new regions, exactly the shape the architecture
  already has. `docs/architecture.md` records this decision (see the note added
  there); `voxel-terrain.md` gains a short "authored world" reconciliation.

### 2.3 "2D pixel top-down" vs. the 3D client

`CLAUDE.md`/`README` still describe a *"2D pixel top-down"* game; the client
pivoted to third-person **3D** (see `architecture.md` §Projection). The endgoal's
*"better graphics, still low resolution, roblox-kind, highly optimized"* confirms
3D low-res pixel-art as the direction. **Resolution: 3D is the projection; the
stale "2D" language is corrected in `CLAUDE.md`/`README`.** The optimization
mandate (low-res render, surface texels, interest management, mobile budget) is
non-negotiable and shapes every art and content decision below.

---

## 3. Foundations — the four things that make "unlimited" cheap

Build/extend these *ahead of* the content phases that need them. Every content
phase below is expressed as data over one of these.

### 3.1 The content registry, scaled up (extends today's `ItemCatalog`)

`ItemCatalog` already proves the model: one declarative `ItemDef` table, rules as
lookups. Scale it to carry every content type the vision needs, all in `sim-core`
so client and server read identical facts (invariant #4), all covered by the
"every id has a def" test discipline:

- `ItemDef` grows fields as phases need them (equip slot, insulation, carry
  bonus, weapon stats, ammo type, decay rate) — **additive, never a new switch.**
- `HarvestNodeDef`, `RecipeDef` (with a **required crafting station** and unlock
  condition), `StructureDef` (footprint, solid?, station?, warmth/light, HP),
  `CubeMaterialDef` (from `voxel-terrain.md`: dig tool/tier, drop, placeable).
- New: `CreatureDef`, `LootTableDef`, `PrefabDef`, `VehicleDef` (each introduced
  by its phase). Determinism rules apply to all: static, ordered, integer-valued.

"Countless materials" is this table getting long. That must stay *cheap and safe*
— hence the test that every referenced id resolves and every recipe's inputs and
node's outputs are real.

### 3.2 An item-*instance* model (new; unblocks gear, weapons, decay)

Today inventory is `ItemId → count`: fine for fungible resources, wrong for a
worn axe, a scoped rifle, or a backpack with contents. Introduce an **item
instance**: a stable instance id, its `ItemId`, and per-instance state
(durability, quality tier, attachments, container contents). Stacks stay counts;
anything with individuality becomes an instance. Lives on the **gateway
character** (invariant #3), wire-encoded once in `shared-proto`. This is the
single prerequisite for clothing, backpacks, guns with attachments, and gear
degradation — all of Phases F and I ride it.

### 3.3 The authored-content / prefab system (new; unblocks the world)

Per §2.2: a `PrefabDef` format (a bounded volume of cubes + structures + loot
anchors), an authoring pipeline (source format → compiled deterministic data), and
a **generator stamp pass** that places prefabs at deterministic anchors after
terrain and before diffs. Riding invariant #2: prefabs are seed-side, player
changes to them are ordinary diffs, so a looted house restocks predictably and a
raided one stays raided. Enables Phase G (cities/ruins) and, later, clan-authored
blueprints.

### 3.4 The server-side entity/actor system (already scoped in the old roadmap)

Creatures, dropped items, vehicles and future NPCs don't fit tiles-or-structures.
An authoritative **entity** abstraction (id, position, kind, per-tick behaviour)
broadcast through the **existing interest-management path** (invariant #5 — extend,
don't fork), with behaviour rules kept deterministic in `sim-core`. Players become
one entity kind; dropped loot, animals, and vehicles are others. Unblocks Phases
H (creatures/cooking), I (projectiles), and L (vehicles).

### 3.5 Protocol & client-decomposition hygiene (carried from the old roadmap)

- `shared-proto` read/write helpers so a new message is a method, not a wire
  scavenger hunt — every phase below adds messages.
- Split `World3D.cs`/`SurvivalHud.cs` into systems; a `MessageId`-keyed dispatch
  table; a reusable touch-UI widget kit (meter, item-slot, action button) and one
  client-side inventory model the HUD/craft/build/gear panels all observe.
- One `Tuning` config for all balance numbers.

---

## 4. The phase ladder

Phases A–D are the near-term survival loop (A largely done). E onward is the
endgame vision. Phases are ordered by dependency, **not strictly serial** — art,
balancing and onboarding run continuously, and several content phases parallelise
once their foundation exists. Each phase names the foundation it rides and its
exit criterion.

### Phase A — Close the core loop ✅ (largely complete)

Food & eating, functional campfire (warmth + cooking station), night pressure,
tools that matter, harvest durability. **Remaining:** campfire as a true *cooking*
station (raw→cooked pair) — folded into Phase H where raw meat arrives.
*Exit: a new player survives their first night and feels why each step mattered.*

### Phase B — Depth & progression (make it worth returning)

- **Skills/proficiency** on the gateway character (invariant #3): gathering and
  crafting raise proficiency → better yield, new recipes, a difficulty curve.
- **Health regen & injury** so later combat/hazards have counterplay.
- **Wall collision & real shelter** in the shared movement rules — prerequisite
  for threats and for bases.
- **Digging & ore** (implements `voxel-terrain.md`): the cube/column model, dig
  diffs, `DigRules`, ore tiers. This is where **tools finally gate depth** and
  "countless materials" starts — the first ores enter the registry.
*Exit: there is something to get better at, walls keep things out, and digging
down reveals materials.*

### Phase C — Living world v1 (make it tense)  ·  rides §3.4 entity system

- **Creatures**: passive animals (huntable food + hide) and first night hostiles
  (the reason walls exist). First real use of the entity system.
- **Simple melee combat**, server-authoritative, reusing the reach check that
  already gates harvesting.
- **Dropped items as entities** (design the harvest-loot case now so loot can be
  a world entity, not a teleport-to-inventory).
*Exit: the night has teeth and animals are a reason to leave the fire.*

### Phase D — Polish & onboarding (make it intuitive) · continuous

Contextual first-session guidance (mobile players won't read walls of text), item
icons & feedback (particles/sfx/numbers), one-thumb HUD consolidation, audio, and
balancing against real play. Runs alongside every phase, not just once.

---

Everything below is the endgame vision. Foundations §3.1–3.4 are the gate; build
each foundation immediately before the first phase that needs it.

### Phase E — Materials, stations & the tech tree  ·  rides §3.1 registry

The backbone that makes "countless materials & crafting stations" tractable.

- **Material tiers & processing chains.** Ore → smelted metal → parts; plant →
  fiber → cloth; the chains that later feed tools, gear and guns. Each link is an
  `ItemDef` + `RecipeDef` row.
- **Crafting stations as gated structures.** Extend `StructureDef`/`RecipeDef` so
  a recipe can require a station: **campfire → cooking stove → gunpowder lab → gun
  workbench**, plus workbench/forge/tanning rack. Placing and using a station is
  the existing placement + a recipe-station check — no new subsystem.
- **Storage containers** as structures with instance-backed contents (§3.2).
*Exit: a legible progression of stations unlocks tiers of materials and recipes,
all data-driven.*

### Phase F — Gear: backpacks, clothing & the underground shelter  ·  rides §3.2 instances

- **Equipment slots & clothing** with capabilities: insulation (feeds the warmth
  meter), protection, carry modifiers, environment resistance — all `ItemDef`
  fields, all per-instance state.
- **Backpacks** expand carry capacity; a backpack is a container instance.
- **The underground shelter.** Every player spawns from a **personal underground
  shelter** — their first safe place and overflow store before they build a base.
  Modeled as an owned, access-controlled space with storage and the spawn/respawn
  anchor. Implemented via §3.2 (storage), §3.3 (a prefab per player), and the
  gateway (ownership + spawn point).
*Exit: what you wear and carry matters, and every player has a safe home to
return to and stash beyond their backpack.*

### Phase G — The authored world: cities, houses, ruins & growth  ·  rides §3.3 prefabs

- **Authored POIs**: real cities, houses and abandoned constructions stamped into
  the world as prefabs (§2.2), populated with **lootable containers** (`LootTableDef`,
  §3.1) so exploration yields resources and gear.
- **The shift to an authored world** (§2.2): the generator gains the prefab stamp
  pass; procedural terrain becomes the *fill between* authored places.
- **World growth to scale**: the region/server model and tooling to author and
  stand up new regions over time, linked by voyages — the concrete path toward
  "real-world size" without one seamless map.
*Exit: the world has real places worth travelling to and looting, and can be
extended region by region.*

### Phase H — Creatures, cooking & the food web  ·  rides §3.4 entity system

- **Animals & cooking ingredients**: a richer creature roster yielding meat,
  hide, fat, ingredients. Hunting deepens; hides feed Phase F clothing; the food
  web ties Phases C, E and F together.
- **Cooking stations** (finishes Phase A's raw→cooked): the stove/fire turns raw
  ingredients into meals with better food/health/buff values — recipes gated on a
  cooking station (§Phase E).
*Exit: food is a system (hunt → butcher → cook → eat/buff), not a berry.*

### Phase I — Combat depth, firearms & ballistics  ·  rides §3.2 + §3.4

The scarce top of the tree (§2.1). Sequenced deliberately after E (materials),
F (gear) and H (a reason to fight): melee → bows/thrown → **firearms**.

- **Server-authoritative ranged combat**: projectiles as entities (§3.4) or
  hitscan with server validation; damage vs. the protection from Phase F gear.
  Revisit the movement-authority note in `architecture.md` — competitive PvP may
  justify the headless-Godot upgrade path there.
- **The firearm tech tree**: gunpowder lab + gun workbench (§Phase E stations),
  a **range of guns** as instance items with **attachments and durability**
  (§3.2), and **scarce, crafted ammunition**. Guns are expensive, degrade, and
  hunger for ammo — by design (§2.1).
*Exit: a defended base and a stocked voyage are possible, and violence is
expensive enough that karma (Phase K) can govern it.*

### Phase J — Clans & shared bases  ·  rides gateway + §3.3

- **Clans** as a gateway-owned entity (like the character; invariant #3):
  membership, roles, shared identity.
- **Shared bases**: base ownership and build/access permissions over structures
  and cube edits, shared clan storage, and a claimed territory. Structures and
  diffs gain an owner (player or clan).
*Exit: people can team up, hold ground together, and share a base and its stores.*

### Phase K — Karma, PvP law & destructibility  ·  rides Phases I + J

- **Karma** on the gateway character: killing a lower-ranked player, or an
  unclanned player while you are clanned, costs karma. **Low karma has real
  effects** — e.g. hostile-flagged to others, barred from safe zones, worse NPC
  prices, visible mark. This is the systemic answer to §2.1's "not a
  slaughterhouse."
- **Griefing & tearing down buildings**: structures and cubes become
  **destructible** (HP, raid tools/explosives from Phase I). Raiding is
  legitimate but **karma- and clan-gated** — raiding your own strengthens a clan
  war; raiding the weak is punished. Base decay/upkeep keeps the world from
  freezing into abandoned forts.
*Exit: PvP and raiding exist, but the world nudges players away from slaughtering
the weak, and consequences are felt.*

### Phase L — Vehicles: cars & boats  ·  rides §3.4 entity system

- **Vehicles as driveable entities** (`VehicleDef`, §3.1): cars for land, boats
  for water — server-authoritative movement bounded like player movement
  (`MovementRules`), fuel/condition as survival costs.
- **Boats integrate with voyages**: the boat is the fiction and the vehicle for
  the ocean crossing between regions (`voyage-transfer.md`), closing the loop with
  the world-growth model.
*Exit: players travel the growing world by land and sea under their own power.*

### Continuous — Art, optimization & balancing

The "better graphics, still low-res, highly optimized" mandate (§2.3) is a
standing pass, not a phase: extend the surface-texel/low-res-render pipeline as
content grows, hold the mobile budget (interest management, LOD, entity caps), and
rebalance drain rates, yields, loot and combat against real mobile play every
phase.

---

## 5. Sequencing & the through-line

1. **Finish the near-term loop**: Phase B (skills, wall collision, digging & ore),
   Phase C (creatures + melee), with Phase D polish continuous.
2. **Lay the two content foundations** the vision hinges on: §3.1 registry scale-up
   and §3.2 item instances. Almost everything below is blocked on these.
3. **Phase E** (materials/stations/tech tree) — the spine the rest hangs from.
4. **Phase F** (gear + underground shelter) and **Phase G** (authored world) — F
   rides §3.2, G rides §3.3; they parallelise once their foundations land.
5. **Phase H** (creatures/cooking), then **Phase I** (combat/firearms) — H gives I
   a reason and materials; I stays deliberately late and scarce.
6. **Phase J** (clans/bases) → **Phase K** (karma/PvP/destructibility): social
   ownership before the consequences system that governs it.
7. **Phase L** (vehicles) once the world is big enough to be worth crossing.

The through-line, unchanged from day one: **make survival a loop worth repeating,
and turn every new material, animal, building, garment and gun into content —
rows of data over four solid foundations — so the world's possibilities really can
be unlimited without the codebase fighting back.**
