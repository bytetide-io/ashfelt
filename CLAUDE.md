# Ashfall — working agreement

Open-source, **mobile-only** 2D pixel top-down survival game. Godot 4 client (C#),
C# authoritative world-servers, many bounded worlds linked by ocean voyages.

Read `docs/architecture.md` and `docs/voyage-transfer.md` before changing
anything structural. They are the source of truth; this file is how we work.

## Game vision (do not drift)

- **Survival first.** Gather, craft, build, endure. Not a shooter, not an MMO
  theme park. Every feature must answer: does this make surviving more
  interesting?
- **Mobile-only, touch-first.** One-thumb reachable UI, short sessions,
  low bandwidth, modest CPU/GPU/battery budget. No keyboard/mouse-first design.
- **Bounded worlds, not one seamless map.** Regions are separate world-servers
  linked by voyages. Never propose a single global map.
- **Server-authoritative.** The client predicts; the server decides.
- **Readable pixel art.** Legibility on a small screen beats detail.

When a request conflicts with the vision, say so and propose an alternative
that fits — don't silently implement the conflicting version.

## Load-bearing invariants

Breaking any of these needs an explicit decision recorded in `docs/`:

1. Client input is a **request**, never a state change.
2. World storage is **seed + diffs**. Full chunks are never persisted.
3. Character (inventory/stats/skills) lives in the **gateway** DB; world-servers
   hold world state only.
4. **One shared sim library** — `packages/sim-core`. Terrain, tile rules and
   crafting live there and are referenced by both client and server. Never
   duplicate simulation logic across the boundary.
5. **Determinism**: `sim-core` uses integer hashing (`Noise.Hash`), never
   platform RNG, `float` accumulation across frames, `DateTime.Now`, or
   dictionary/iteration order. `tests/sim-core.tests/DeterminismTests.cs` is a
   compatibility contract — a changed hash changes every existing world.
6. **Interest management**: a client only ever receives entities/chunks near it.

## Target frameworks

`sim-core`, `shared-proto`, `client` → **net8.0** (Godot 4 runtime).
`world-server`, `gateway` → **net10.0**. net10 referencing net8 is intentional.

## Code standards

**Self-documenting.** Names carry the meaning; comments explain *why*, never
*what*. If a comment restates the code, rename instead.

- Intention-revealing names: `TryConsumeStamina`, `ChunkCoord`, not `dt2`, `mgr`.
- Small, single-purpose methods. If you need a comment to split a method into
  sections, those sections are methods.
- No magic numbers. Named `const` or config, with the unit in the name
  (`TickIntervalMs`, `ChunkTiles`).
- Prefer `readonly` / immutable structs for sim data; mutation is explicit.
- Fail loudly on server-side invariant violations; never silently clamp.
- Nullable reference types on; no `!` to silence the compiler without a reason.

**Reusable, not duplicated.**

- Shared rules → `packages/sim-core`. Wire shapes → `packages/shared-proto`.
  Anything both client and server know belongs in a package, not in an app.
- Third rule of thumb: second occurrence is a smell, third is a refactor.
- Godot: build small reusable scenes/components; no god-scenes, no logic in
  `_Process` that belongs in a system.
- Use existing packages for icons/assets/utilities rather than hand-rolling
  (no hand-written SVG icons).

**Tests.**

- Anything in `sim-core` that affects generation or rules gets a test.
- Determinism tests are never "fixed" by updating expected values — if they
  fail, the change is a world-breaking change and must be justified.
- `dotnet test tests/sim-core.tests` must pass before any commit.

## Documentation duties

- Structural or invariant change → update `docs/architecture.md`.
- Protocol/message change → update `packages/shared-proto` **and** the docs
  describing it; keep wire docs concise and example-driven.
- Phase completed → update the **Status** section in `README.md`.

## Workflow

- Match surrounding style, naming and comment density.
- Prefer the smallest change that fully solves the problem; no speculative
  abstraction for a single caller.
- Don't add dependencies without saying why; mobile binary size matters.
- Commit only when asked; branch off `main`.

## Commands

```bash
dotnet test tests/sim-core.tests
dotnet run --project apps/world-server        # udp/9050, seed 1337
docker compose -f infra/docker/docker-compose.yml up
# client: open apps/client in the Godot .NET editor, run Main.tscn
```
