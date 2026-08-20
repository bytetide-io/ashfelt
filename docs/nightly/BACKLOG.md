# Ashfall — nightly backlog

Ranked ideas and audit findings not yet built. Score = Severity(1-5) ×
Blast radius(1-5). Threshold for a mandatory same-night fix is score ≥ 15,
or any multiplayer-correctness finding ≥ 9 — nothing below crossed that
tonight (2026-08-20), which is why the session moved to Phase 2 (feature)
after the audit. Re-score on pickup; these are one session's read, not law.

## Audit findings (2026-08-20)

| # | Finding | Sev | Blast | Score | Category |
|---|---|---|---|---|---|
| 1 | `World3D.cs` (809 lines) is a god-script: net dispatch, terrain streaming, local player, remote players, interaction/targeting all in one class. `docs/gameplay-roadmap.md` §3.4 already has a decomposition plan (split by responsibility, message-dispatch table). | 3 | 3 | 9 | maintainability |
| 2 | `SurvivalHud.cs` (983 lines) is a god-script: meters, hotbar, action sheet, travel panel, gather/feed prompts all in one `Control`. Same §3.4 plan covers a reusable widget kit. | 3 | 2 | 6 | maintainability |
| 3 | Hand-paired `NetDataWriter.Put`/`NetDataReader.Get` calls for every message, duplicated between `world-server/Program.cs` and `client/WorldConnection.cs`. Verified by hand tonight that all ~19 message types (17 existing + 2 added) currently match byte-for-byte — no active bug — but a future message add/edit has no compiler check that the two sides agree. `docs/gameplay-roadmap.md` §3.2 already proposes a shared codec/helper. | 2 | 4 | 8 | multiplayer-correctness (latent, not active) |
| 4 | No data-driven registry beyond `ItemCatalog`/`HarvestRules`/`CraftingRules` — adding a new structure behaviour still touches multiple files. §3.1 already scoped. Tonight's fire feature added one more `TryGet(...).WarmthRadiusMetres > 0` gate rather than a new special case, which stayed consistent with the existing pattern rather than adding a new axis of debt. | 2 | 3 | 6 | maintainability |
| 5 | Persistence has no version field: `character.inventory` is JSONB keyed by `ItemId.ToString()` (safe to reorder, unsafe to rename), `tile_diff.tile` is a bare `short` cast of the `TileType` enum ordinal (**unsafe to reorder** — silently corrupts every persisted diff with no error). `ItemId`/`MessageId` already carry "append-only, never renumber" doc comments; `TileType` does not. No world has shipped yet, so blast radius is currently theoretical. | 3 | 4 | 12 | persistence/data |
| 6 | `tests/sim-core.tests` is the only test project. World-server, gateway and client logic (reach checks, gateway HTTP endpoints, message dispatch) have zero automated coverage — verified only by the ad-hoc LiteNetLib harness used tonight, which wasn't kept (thrown away per scratch-file convention). | 2 | 3 | 6 | testing |
| 7 | `PlayerBody.cs` movement is "currently client-side only; making it server-authoritative is the open architectural question" (the code's own comment). The server bounds the *result* via `MovementRules` but never runs physics itself — accepted tradeoff per `docs/architecture.md`'s "Movement authority" section, not a new finding, just flagging it's still open. | 2 | 3 | 6 | multiplayer-correctness (accepted tradeoff) |
| 8 | No entity/actor system yet (§3.3) — creatures, dropped items, NPCs have nowhere to live. Blocks Phase C entirely. Not urgent until Phase C starts. | 2 | 3 | 6 | architecture (future-blocking) |

None reached the ≥15 / ≥9-multiplayer bar. #5 (persistence versioning) is
the one worth picking up soonest — cheap to fix now (add a `TileType`
append-only comment + a `schema_version` column) versus expensive once a
world has actually shipped and reordering becomes truly unsafe in practice,
not just in theory.

## Feature ideas considered and rejected tonight

- **Shared storage / chest.** New placeable structure with a server-held
  inventory, deposit/withdraw UI. Rejected: needs a whole new container-UI
  panel I can't visually verify without Godot, and doesn't reuse an existing
  interaction pattern the way fire-feeding reuses "spend an item on a nearby
  structure." Good candidate for a night with device access.
- **Cooking (raw→cooked food at the campfire).** This is the literal next
  item in `docs/gameplay-roadmap.md` Phase A, but there's no raw food to cook
  yet (`Berry` is eaten raw; raw meat arrives with creatures in Phase C) — it
  would need inventing a new raw item with no gather source of its own, which
  is closer to "content dressed as a system" than a real composition. Feeding
  the fire is a load-bearing prerequisite for cooking to matter anyway (a
  station that's always on has no cooking urgency); doing it first is the
  right order.
- **"5 new craftable items."** Rejected on sight per the routine's own
  instruction — content dressed as a system, doesn't touch multiplayer
  dynamics or simulation depth.

## Not-yet-scored ideas (from `docs/gameplay-roadmap.md`, for context)

Skills/proficiency, health regen, tool tiers, wall collision, creatures/combat,
biome identity, onboarding prompts, item icon polish, audio/balance pass. See
that doc for the full phased plan — this backlog only tracks nightly-audit
findings and rejected-tonight ideas, not the whole roadmap.
