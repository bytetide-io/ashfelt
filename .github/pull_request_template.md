<!--
Thanks for contributing to Ashfall! Fill in the sections below.
Keep PRs focused — one logical change is much easier to review than five.
See CONTRIBUTING.md for the full workflow and standards.
-->

## What & why

<!-- What does this change do, and what problem does it solve?
     Link the issue it closes, e.g. "Closes #123". -->

## How I tested it

<!-- Commands you ran, scenes you played, platforms you checked.
     `dotnet test tests/sim-core.tests` must pass. -->

- [ ] `dotnet test tests/sim-core.tests` passes
- [ ] Built the affected app(s) locally

## Vision & invariants

<!-- Ashfall has a deliberate shape. Confirm this change respects it,
     or explain the recorded decision if it changes something structural. -->

- [ ] Fits the survival-first, mobile-only, server-authoritative vision
      (see `CONTRIBUTING.md`)
- [ ] Does **not** break a load-bearing invariant — or, if it does, the decision
      is recorded in `docs/architecture.md`
- [ ] No simulation logic duplicated across the client/server boundary
      (shared rules live in `sim-core`)
- [ ] If terrain generation changed, the determinism impact is understood and
      recorded (I did **not** just update expected test values)

## Docs

<!-- Docs are part of the change. Tick what applies; delete what doesn't. -->

- [ ] Updated `docs/architecture.md` (structural/invariant change)
- [ ] Updated `shared-proto` + wire docs (protocol/message change)
- [ ] Updated `apps/gateway/API.md` (gateway API change)
- [ ] Recorded new art + licence in `ASSETS.md`
- [ ] Updated README **Status** / roadmap (phase progress)
- [ ] No docs needed

## Notes for reviewers

<!-- Anything you're unsure about, trade-offs you made, or areas to look at
     closely. Optional. -->
