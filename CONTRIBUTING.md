# Contributing to Ashfall

Thanks for wanting to help build Ashfall — an open-source, **mobile-only** 2D
pixel top-down survival game. This guide gets you from a fresh clone to a merged
pull request, and points you at work that's ready to pick up.

New here? Read this page top to bottom once. It links out to everything else you
need, and the 15 minutes will save you a rejected PR.

- **Just want to build and run it?** → [Development setup](docs/development-setup.md)
- **Lost in the codebase?** → [Codebase tour](docs/codebase-tour.md)
- **Want the full design rationale?** → [`docs/architecture.md`](docs/architecture.md)
- **Looking for something to build?** → [What to work on](#what-to-work-on)

By participating you agree to our [Code of Conduct](CODE_OF_CONDUCT.md).

---

## The vision (read this before you write code)

Ashfall has a deliberate shape, and PRs that fight it get sent back no matter how
clean the code is. The five commitments below are the whole game in miniature:

1. **Survival first.** Gather, craft, build, endure. Not a shooter, not an MMO
   theme park. Every feature must answer *does this make surviving more
   interesting?*
2. **Mobile-only, touch-first.** One-thumb-reachable UI, short sessions, low
   bandwidth, modest CPU/GPU/battery. No keyboard/mouse-first design.
3. **Bounded worlds, not one seamless map.** Regions are separate world-servers
   linked by ocean voyages. There is no global map and there will not be one.
4. **Server-authoritative.** The client predicts; the server decides.
5. **Readable pixel art.** Legibility on a small screen beats detail.

If your idea conflicts with one of these, that's fine — say so in an issue and
propose an alternative that fits. Don't silently implement the conflicting
version.

## Load-bearing invariants (breaking these needs a recorded decision)

These are not style preferences. Breaking one changes the architecture, so it
needs an explicit decision written into [`docs/architecture.md`](docs/architecture.md)
**before** the code lands — not a surprise in a diff.

1. **Client input is a request, never a state change.** The world-server is the
   only source of truth for position, inventory, building and combat.
2. **World storage is seed + diffs.** Full chunks are never persisted — only the
   procgen seed and player-caused modifications.
3. **Character lives in the gateway.** Inventory, stats and skills are in the
   gateway DB; world-servers hold world state only.
4. **One shared simulation library.** Terrain, tile rules and crafting live in
   [`packages/sim-core`](packages/sim-core) and are referenced by both client and
   server. Never duplicate simulation logic across the boundary — if the client
   needs to predict, it calls `sim-core`.
5. **Determinism.** `sim-core` uses integer hashing (`Noise.Hash`) — never
   platform RNG, `float` accumulation across frames, `DateTime.Now`, or
   dictionary/iteration order. A changed hash changes *every existing world*.
6. **Interest management.** A client only ever receives entities and chunks near
   it.

The full reasoning behind each lives in
[`docs/architecture.md`](docs/architecture.md). Read it before any structural
change.

---

## Quickstart

Full, OS-by-OS instructions are in
[**docs/development-setup.md**](docs/development-setup.md). The short version:

```bash
# 1. Fork on GitHub, then clone your fork
git clone https://github.com/<you>/ashfelt.git
cd ashfelt

# 2. Run the tests — this is also your "is my toolchain healthy?" check
dotnet test tests/sim-core.tests

# 3. Run a world-server (udp/9050, seed 1337)
dotnet run --project apps/world-server

# 4. Open the client: apps/client in the Godot 4 .NET editor, run the boot scene
```

You need **.NET SDK 10** (it builds the net8.0 libraries too) and, for the
client, **Godot 4.3+ .NET/Mono**. Docker is only needed for Postgres. See the
setup guide for versions, PATH wrappers, and gotchas.

---

## The contribution workflow

We use a standard **fork → branch → pull request** flow.

### 1. Find or open an issue

Before writing code, make sure there's an issue describing the change, and that
you've said you're taking it. This avoids two people building the same thing and
gives maintainers a chance to flag a vision/invariant conflict *before* you've
spent an evening on it.

- Small, obvious fixes (typos, a broken link, an obviously wrong constant) don't
  need an issue — just open the PR.
- Anything that adds a feature, changes a rule, or touches the protocol needs an
  issue first.

### 2. Branch

Branch off `main`. Name it for the change:

```bash
git switch -c feat/cookable-food
git switch -c fix/warmth-bar-clamp
git switch -c docs/voyage-diagram
```

### 3. Build it

- Make the **smallest change that fully solves the problem**. No speculative
  abstraction for a single caller (second occurrence is a smell, third is a
  refactor).
- Match the surrounding style, naming and comment density.
- Follow the [coding standards](#coding-standards) below.
- Keep [documentation in sync](#documentation-duties) as you go — docs are part
  of the change, not a follow-up.

### 4. Test

```bash
dotnet test tests/sim-core.tests
```

**This must pass before you commit.** If you changed anything in `sim-core` that
affects generation or rules, add a test for it (see [Testing](#testing)).

### 5. Commit

Write clear, imperative commit messages scoped by area, matching the existing
history:

```
sim-core: add cooked-berry recipe and food value
client: show Eat button on edible forage
docs: record BerryBush generation change
```

- One logical change per commit where practical.
- The subject line says *what* changed; the body says *why*, if it isn't obvious.
- Don't commit `bin/`, `obj/`, `.godot/`, or editor cruft — `.gitignore` covers
  the usual suspects, but check `git status` before you add.

### 6. Open a pull request

Push your branch and open a PR against `main`. Fill in the
[PR template](.github/pull_request_template.md) — it asks what changed, why, how
you tested it, and which invariants/docs you touched.

- **Draft PRs are welcome** for early feedback.
- Keep PRs focused. A 2000-line PR that does five things is five reviews in a
  trench coat; split it.
- CI (build + `sim-core` tests + a headless Godot import) must be green. See
  [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
- Expect review. Ashfall is opinionated on purpose; a request for changes is
  about the game staying coherent, not about you.

---

## Coding standards

The rules the whole codebase lives by. The one-line version: **names carry the
meaning; comments explain *why*, never *what*.** If a comment restates the code,
rename instead.

### Self-documenting

- Intention-revealing names: `TryConsumeStamina`, `ChunkCoord` — not `dt2`,
  `mgr`.
- Small, single-purpose methods. If you need a comment to split a method into
  sections, those sections are methods.
- **No magic numbers.** Named `const` or config, with the unit in the name:
  `TickIntervalMs`, `ChunkTiles`, `WarmthRadiusMetres`.
- Prefer `readonly` / immutable structs for sim data; mutation is explicit.
- **Fail loudly** on server-side invariant violations. Never silently clamp bad
  input into a valid range — that hides bugs and cheats.
- Nullable reference types are on. Don't reach for `!` to silence the compiler
  without a reason worth writing down.

### Reusable, not duplicated

- Shared rules → [`packages/sim-core`](packages/sim-core). Wire shapes →
  [`packages/shared-proto`](packages/shared-proto). Anything both client and
  server know belongs in a package, not copied into an app.
- **Godot:** build small reusable scenes/components. No god-scenes, no gameplay
  logic in `_Process` that belongs in a system.
- Use existing packages for icons/assets/utilities rather than hand-rolling. In
  particular, **no hand-written SVG icons** — Ashfall's icons are generated from
  design-system pixel-grid data (`scripts/ui/PixelIcons.cs`).

### Target frameworks

Two runtimes, on purpose:

| Project | Target | Why |
|---|---|---|
| `sim-core`, `shared-proto`, `client` | **net8.0** | Godot 4's .NET runtime |
| `world-server`, `gateway` | **net10.0** | never load into Godot |

A net10 server referencing net8 libraries is supported and intentional. Don't
"fix" the mismatch.

---

## Testing

- Anything in `sim-core` that affects generation or rules **gets a test**. Tests
  live in [`tests/sim-core.tests`](tests/sim-core.tests).
- `dotnet test tests/sim-core.tests` must pass before every commit.

### The determinism contract

[`tests/sim-core.tests/DeterminismTests.cs`](tests/sim-core.tests/DeterminismTests.cs)
is a **compatibility contract**, not a normal test.

> Determinism tests are never "fixed" by updating expected values. If one fails,
> your change is a world-breaking change — the same seed now generates a
> different world. That is sometimes acceptable (no world has shipped yet), but
> it is always a **decision**, recorded in
> [`docs/architecture.md`](docs/architecture.md) under "Generation changes on
> record" — never a silent edit to make CI green.

If you're not sure whether your change is world-breaking, open the issue/PR and
ask. That's exactly what review is for.

---

## Documentation duties

Docs are part of the change. Before you mark a PR ready:

- **Structural or invariant change** → update
  [`docs/architecture.md`](docs/architecture.md).
- **Protocol / message change** → update
  [`packages/shared-proto`](packages/shared-proto) **and** the docs describing it.
  Keep wire docs concise and example-driven.
- **New or changed art** → record it in [`ASSETS.md`](ASSETS.md) *as you add it*,
  with its licence. An unlicensed asset is a legal problem, not a tidiness one.
- **Gateway API change** → update [`apps/gateway/API.md`](apps/gateway/API.md).
- **A roadmap phase completed** → update the **Status** section in
  [`README.md`](README.md) and the relevant note in
  [`docs/gameplay-roadmap.md`](docs/gameplay-roadmap.md).

---

## What to work on

Good ways in, roughly easiest first:

1. **`good first issue` / `help wanted`** — check the
   [issue tracker](https://github.com/bytetide-io/ashfelt/issues) for these
   labels. They're scoped so you can finish them in a sitting.
2. **Documentation.** Found something in this guide or the setup doc that was
   wrong, stale, or confusing when *you* followed it? Fixing it is a genuinely
   valuable first PR — you have the freshest eyes on it.
3. **Data-driven content.** Thanks to the content registry
   (`sim-core/ItemCatalog`), a lot of new content is *rows of data, not code*:
   a new gatherable, a new recipe, a new placeable. See
   [`docs/gameplay-roadmap.md`](docs/gameplay-roadmap.md) §3.1.
4. **The roadmap's open work.** [`docs/gameplay-roadmap.md`](docs/gameplay-roadmap.md)
   lists what's next and *why*. The "Remaining" notes under Phase A are
   real, scoped tasks (yield variance, cooking station, tool/node tiers).

If you want to build something not on the roadmap, open an issue and pitch it
against the [vision](#the-vision-read-this-before-you-write-code) first — it's
much nicer to hear "that fights invariant #3, here's how to reshape it" before
you've built it than after.

---

## Reporting bugs and requesting features

Use the issue templates — they prompt for the details that make an issue
actionable:

- **[Bug report](.github/ISSUE_TEMPLATE/bug_report.yml)** — what you did, what
  you expected, what happened, and which component (client / world-server /
  gateway / sim-core).
- **[Feature request](.github/ISSUE_TEMPLATE/feature_request.yml)** — the
  problem you're trying to solve and how it fits the survival-first vision.

For anything security-sensitive (an exploit, a way to cheat the authoritative
server, a data leak), **do not open a public issue** — follow
[`SECURITY.md`](SECURITY.md).

---

## Questions

If something here is unclear, that's a documentation bug — open an issue and
we'll fix the docs. Welcome aboard, and thanks for helping people survive the
night.
