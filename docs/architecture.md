# Ashfall architecture

## Shape

Many independent, bounded **world-servers** (one per region/continent), plus a
**gateway** that owns accounts and routes players between them. There is no
single seamless map, and there will not be one.

```
mobile client ──auth──▶ gateway ──▶ postgres (accounts, characters)
      │                    │
      │  connection info ◀─┘
      ▼
 world-server A (udp)      world-server B (udp)
      └── postgres (world diffs, structures) ──┘
```

## Projection: 3D

The game is third-person 3D with physics-based movement. The client renders
terrain as a mesh built from `sim-core` heights; the tile grid still exists
underneath as the unit of world data (chunks, diffs, biomes, harvesting), but
movement is no longer grid-constrained.

`TerrainGenerator.HeightAt` is the bridge: one continuous elevation field
drives both the surface classification and the mesh, so the two can never
disagree. It is in `sim-core` precisely because the server will need identical
heights for collision.

### Movement authority: the client simulates, the server validates

**Decided.** The client runs physics and reports where it ended up. The server
runs no physics engine; it checks each reported position against the height
field it already computes, and corrects anything implausible
(`MovementRules.Check` in sim-core, shared by both sides).

Why not run physics on the server: it costs an engine dependency and a second
implementation that will disagree with the first at the margins — and
disagreement surfaces as rubber-banding on slopes, exactly where players
notice. Bounding what physics can *possibly* produce is far cheaper and
rejects every cheat that matters: speed hacks, teleports, flight, and moving
through terrain all violate a speed bound or the height field.

What this does not catch: a client moving legally but in ways a human could
not, such as perfect aim-walking or subtly favourable collision resolution.
That is an accepted trade. If PvP ever makes it matter, a headless Godot
world-server sharing the client's physics engine is the upgrade path, and
nothing in the protocol has to change.

The rules live in `sim-core` so the client can check itself against the same
bounds before sending, and a legitimate player is never corrected.

## Invariants

1. **Server-authoritative.** The client predicts movement for responsiveness.
   The world-server is the only source of truth for position, inventory,
   building and combat. Client input is a request, never a state change.
2. **World storage = seed + diffs.** Full chunks are never persisted. Each
   world-server stores its procgen seed and only player-caused modifications,
   keyed by chunk coordinate (`tile_diff`, `structure`).
3. **Character is global, map is not.** Inventory, stats and skills live in the
   gateway database. World-servers hold world state only.
4. **One shared simulation library.** `packages/sim-core` defines terrain
   generation, tile rules and (later) crafting. Client and world-server both
   reference it. Logic is never duplicated across the boundary — if the client
   needs to predict, it calls `sim-core`.
5. **Interest management.** A world-server sends each client only the entities
   and chunks near them.

## Determinism

`sim-core` uses integer hashing (`Noise.Hash`) rather than any platform RNG, so
the same seed and coordinate yield the same tile on every device. This is
covered by `tests/sim-core.tests/DeterminismTests.cs`; treat those tests as a
compatibility contract — changing generation changes every existing world.

## Target frameworks

- `sim-core`, `shared-proto`, `client` → **net8.0** (Godot 4's runtime).
- `world-server`, `gateway` → **net10.0** (they never load into Godot).

A net10 server referencing net8 libraries is supported and intentional.
