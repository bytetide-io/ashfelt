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
