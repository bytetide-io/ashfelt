# Ashfall — gameplay & foundation roadmap

Evaluation of the current build plus a phased plan for engaging gameplay and the
reusable systems that make future content cheap to add. Companion to
`architecture.md` (which owns the invariants); this doc owns *what to build and
why*.

## 1. Where the game stands

The **plumbing is excellent and the game is thin.** The hard, load-bearing
problems are already solved well:

- Server-authoritative loop, client prediction + reconciliation, deterministic
  `sim-core` shared across the boundary.
- Seed + diffs world storage; structures persisted and backfilled on join.
- Gateway-owned characters that survive reconnect; voyage handoff with
  single-ownership tickets.
- A clean survival-meter model that is integer-deterministic and predictable.

What is missing is **the game loop that makes those systems matter.** Today the
entire player experience is:

> tap a tree/rock/shrub → get wood/stone/fiber → craft one of 6 things → place a
> wall or campfire → watch hunger drain to zero and die with nothing you can do
> about it.

### The engagement-critical holes

1. **No food loop.** Hunger drains and `SurvivalRules.Eat` exists, but *nothing
   in the game produces food or calls Eat*. The core survival tension has no
   resolution — the only outcome is starvation. This is the single most
   important gap.
2. **Tools are dead content.** `Axe` and `Pickaxe` are craftable but confer no
   benefit; `HarvestRules.Evaluate` neither requires nor rewards them. Crafting
   them is a pure resource sink with no payoff.
3. **Structures are inert.** A placed `Campfire` or `Wall` is scenery. No
   warmth, no light, no cooking, no crafting-station gating, no collision on
   walls. Building has no consequence.
4. **Day/night has no stakes.** The sun moves; nothing else changes. Night
   should be *the* pressure that gives the campfire and walls a reason to exist.
5. **No goals or progression.** No skills (despite the pitch), no unlocks, no
   reason to return to a session. Nothing to be *getting better at*.
6. **Harvest lacks depth.** One tap, one resource, instant, fixed yield. No
   node variety, no yield variance, no tool tiers, no timing/interaction.

None of these need architecture changes — they need content and a few new
shared rules. But adding them naively means more hardcoded `switch` statements,
and that's the second half of this plan.

## 2. Gameplay plan — build the survival loop, then deepen it

Design filter (from `CLAUDE.md`): every feature must make *surviving more
interesting*. Ordered so each phase is independently shippable and the earliest
work closes the biggest engagement gap.

### Phase A — Close the core loop (make survival winnable)

The goal: a player can perceive a threat, act against it, and succeed. This is
the minimum that turns the demo into a game.

> **Progress.** Food & eating ✅ (BerryBush → Berry → EatRequest), functional
> campfire as a warmth source ✅, night pressure ✅ (a warmth meter that drains
> when exposed after dark and bleeds health at zero), and tools boosting harvest
> ✅ (a tool matching a node's `PreferredTool` adds its tier to the yield; bare
> hands still work so tools stay bootstrappable). **Remaining:** the campfire as
> a *cooking* station (needs a raw→cooked food pair) — a natural bridge into
> Phase B/C, since raw meat arrives with creatures.

- **Food & eating.** Add gatherable food (berries from a new `Berry`/bush node,
  and cooking raw → cooked at a campfire). Wire `SurvivalRules.Eat` to an
  `EatRequest`. Food value is a per-item data field (see §3.1), not a switch.
- **Functional campfire.** Placing/lighting a campfire creates a warmth + light
  aura and acts as a **cooking station**. This is what makes night survivable
  and gives building a point.
- **Night as pressure.** At night, add a cold/exposure drain unless near a
  warmth source (campfire). Day/night now drives behaviour: gather by day,
  hunker by night. Reuses the existing `WorldClock` broadcast.
- **Tools matter.** Gate/accelerate harvest by held tool: bare hands are slow or
  yield less; an axe makes forests fast, a pickaxe makes rock viable. Encode as
  data on the harvest rule (tool → yield/speed multiplier), not new branches.

Exit criteria: a new player can survive their first night by gathering food,
building a fire, and cooking — and *feel* why each step mattered.

### Phase B — Depth & progression (make it worth returning)

- **Skills / proficiency.** Gathering and crafting raise a proficiency stored on
  the gateway character (invariant #3). Higher proficiency → better yield, new
  recipes. Gives a reason to keep playing and a natural difficulty curve.
- **Health regen & injury.** Health currently only *falls* (starvation). Add
  regen when well-fed and rested so combat/hazards later have counterplay.
- **More recipes & tiers.** Tool tiers (stone → metal), storage containers,
  better shelter pieces, a bed/respawn point. All data-driven.
- **Wall collision & real shelter.** Walls become solid in the shared movement
  rules so an enclosure actually keeps things out — prerequisite for threats.

### Phase C — Living world (make it tense)

- **Creatures/entities.** A server-side entity system (see §3.3) for passive
  animals (huntable food + hide) and night hostiles (the reason for walls).
  This is the first thing needing an actor abstraction beyond tiles/structures.
- **Simple combat.** Melee against creatures, server-authoritative, reusing the
  reach check that already gates harvesting.
- **Hazards & biomes with identity.** Biomes offer different resources/threats,
  giving voyages between worlds a *reason* (invariant: bounded worlds, linked by
  ocean).

### Phase D — Polish & onboarding (make it intuitive)

- **Onboarding / first-session guidance.** Contextual prompts ("you're cold —
  build a fire"), not a wall of tutorial. Mobile players won't read.
- **Item icons & feedback.** Icon package for inventory/craft/build (per global
  guideline: no hand-rolled SVG). Harvest/craft/eat feedback (particles, sfx,
  numbers). Legibility on a small screen.
- **UI pass.** One-thumb reachable, short-session friendly. Consolidate the HUD.
- **Audio & balancing.** Tune drain rates, yields, night length against real
  play.

## 3. Foundation — reusable systems that make the above cheap

The current content is small enough that hardcoding was right. But every Phase-A
feature above wants to add *items, nodes, recipes, structures, effects*. If each
addition means editing a `switch` in `HarvestRules`, an array in
`CraftingRules`, an enum in `Protocol`, a branch in `World3D.cs`, and a HUD
tweak, velocity collapses. Build these four foundations **first, alongside Phase
A**, and content becomes data.

### 3.1 A data-driven content registry in `sim-core` (highest leverage)

Today item behaviour is scattered: harvest yields in one switch, recipes in an
array, food value nowhere, tool bonuses nowhere. Consolidate into **one
declarative definition table per content type**, all in `sim-core` so client and
server read identical data (invariant #4):

- `ItemDef` — display name, stack size, category, **food value**, **tool class &
  tier**, icon id. Keyed by the existing wire-stable `ItemId`.
- `HarvestNodeDef` — what a node yields, what it becomes, required/preferred
  tool, base yield & variance, harvest time.
- `RecipeDef` — already close (`CraftingRules.Recipes`); extend with a required
  **crafting station** and unlock condition, keep the ordered-list determinism.
- `StructureDef` — footprint, solid?, provides-warmth?, is-station?, light
  radius.

Rule functions (`HarvestRules.Evaluate`, `CraftingRules.Evaluate`) become thin
lookups over these tables instead of switches. **Adding a new gatherable food
then means one `ItemDef` + one `HarvestNodeDef` row — no server, protocol, or
client code change.** Keep the determinism contract: tables are static, ordered,
integer-valued, and covered by a test asserting every `ItemId` has a def.

### 3.2 A typed protocol serialization helper in `shared-proto`

`Program.cs` and the client hand-write `writer.Put`/`reader.Get` in matching
order for every message; a mismatch is a silent wire bug and bumps
`ProtocolVersion` constantly. Add small **read/write helpers** (e.g. a
`MessageWriter`/`MessageReader` wrapper, or per-message static
`Write(...)`/`Read(...)` methods co-located with the `MessageId`). Benefits:
one definition of each message's layout, reused by both sides; new messages are
a method, not a scavenger hunt. Vector/inventory/struct read-write get shared
helpers (they're already duplicated). This directly de-risks every future
feature that adds a message (EatRequest, AttackRequest, skill updates…).

### 3.3 A server-side entity/actor system with interest management

Creatures, dropped items, and future NPCs don't fit tiles-or-structures. Before
Phase C, introduce a lightweight **authoritative entity** abstraction in the
world-server: an id, position, kind, and per-tick behaviour, broadcast through
the **interest-management path that already exists** for players (invariant #5 —
extend it, don't fork it). Design it so players become just one entity kind.
Keep behaviour rules (spawn, wander, aggro) in `sim-core` where they can be
deterministic and tested. This is the one genuinely new subsystem; scope it only
when Phase C needs it, but design the item-drop case in Phase A so harvested
loot can later be a world entity rather than teleporting into inventory.

### 3.4 Decompose the client into systems + a shared HUD/widget kit

`World3D.cs` is a 616-line god-script and `SurvivalHud.cs` is 419. Per the Godot
guideline (no god-scenes, systems not `_Process` soup):

- Split `World3D.cs` by responsibility: net dispatch, terrain streaming, local
  player, remote players, interaction/targeting. The message dispatch should be
  a table keyed by `MessageId` (mirrors §3.2), not a growing `switch`.
- Build a **reusable touch-UI kit**: a meter/bar widget (hunger/stamina/health
  share one), an item-slot widget (inventory/craft/build/hotbar reuse one), a
  contextual action button. Every Phase-A/D UI addition then composes existing
  widgets instead of new bespoke scenes. Pull icons from an icon package.
- A single client-side **inventory model** that the HUD, craft panel, and build
  panel all observe, rather than each re-reading packets.

### 3.5 Test & tuning scaffolding

- Keep the rule-in-`sim-core`-gets-a-test discipline; add tests for the new defs
  (every item has a def, every recipe's inputs/outputs are real items, every
  node becomes a real tile).
- Move all balance numbers into one **`Tuning`-style config** (rates, yields,
  night cost, food values) so balancing is one file, not a hunt. Some already
  live in `Tuning`; make that the home for all of it.

## 4. Suggested sequencing

1. **Foundation-first slice:** §3.1 registry + §3.2 proto helpers + §3.4 message
   dispatch table. Refactor existing harvest/craft/place onto them — no new
   gameplay yet, but proves the abstractions against known content.
2. **Phase A on top:** food, campfire function, night pressure, tools — each now
   a data addition. This is the release that makes Ashfall *a game*.
3. **§3.4 UI kit + Phase D onboarding** in parallel with A's client work.
4. **Phase B** progression; **§3.3 entity system** then **Phase C** creatures.
5. Balancing pass throughout, against real mobile play.

The through-line: **spend the next unit of effort making survival a loop the
player can win and want to repeat, and build the content registry that makes
every subsequent survival mechanic a few rows of data instead of a cross-cutting
code change.**
