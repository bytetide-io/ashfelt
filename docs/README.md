# Ashfall documentation

The map to everything written down. Start wherever your question sits.

## New contributor? Start here

1. [**../CONTRIBUTING.md**](../CONTRIBUTING.md) — the vision, the invariants, and
   the fork → PR workflow. Read this first.
2. [**development-setup.md**](development-setup.md) — build, run, and test it
   locally, with per-OS gotchas and troubleshooting.
3. [**codebase-tour.md**](codebase-tour.md) — what each project is and *where to
   look when you want to change X*.
4. [**../CODE_OF_CONDUCT.md**](../CODE_OF_CONDUCT.md) — how we treat each other.

## Design & architecture (the "why")

- [**architecture.md**](architecture.md) — the shape of the system and the
  load-bearing invariants. **The source of truth.** Read before any structural
  change.
- [**gameplay-roadmap.md**](gameplay-roadmap.md) — an honest assessment of where
  the game stands, the phased plan for gameplay, and the reusable foundations
  that make new content cheap. Owns *what to build and why*.
- [**voyage-transfer.md**](voyage-transfer.md) — how a character moves between
  bounded world-servers.
- [**voxel-terrain.md**](voxel-terrain.md) — terrain and rendering notes.

## Reference

- [**../apps/gateway/API.md**](../apps/gateway/API.md) — the gateway HTTP API
  (health, worlds, characters).
- [**../ASSETS.md**](../ASSETS.md) — the asset pipeline, the atlas contract, and
  the licensing rules for art.
- [**../README.md**](../README.md) — project overview, layout, requirements, run
  commands, controls, and current status.
- [**../SECURITY.md**](../SECURITY.md) — how to report a vulnerability or a
  server-authority exploit.

## Keeping docs honest

Docs are part of every change, not a follow-up. When you change the system,
update the doc that describes it in the same PR — the
[documentation duties](../CONTRIBUTING.md#documentation-duties) list which doc
owns what. If a doc was wrong or stale when *you* followed it, fixing it is a
genuinely valuable PR.
