# Nightly backlog

Ranked ideas and findings not yet built, newest audit first. Score =
Severity(1-5) × Blast radius(1-5); anything ≥15, or ≥9 on a multiplayer-
correctness finding, is a mandatory same-night fix per the nightly brief —
everything below stayed under that bar tonight.

## 2026-08-23 audit findings

- **`SurvivalHud.cs` is a god script (~940 lines).** Builds every HUD system —
  meters, hotbar, gather/feed reticle, and the full action sheet (items,
  craft, build, travel tabs) — as one `Control` with no sub-scenes. Severity
  3 (real maintainability cost, nothing broken) × Blast radius 3 (every
  future HUD feature touches this file) = **9**. Suggested fix: split the
  action-sheet tabs into their own scenes/scripts (`CraftTab.cs`,
  `BuildTab.cs`, etc.) that `SurvivalHud` composes rather than builds inline.
- **`World3D.cs` is a god script (~735 lines).** World building, sky/clock,
  foliage bookkeeping, harvest reticle, and structure rendering all live on
  one node. Severity 2 × Blast radius 3 = **6**. Lower priority than the HUD
  since its private methods are already small and single-purpose — the size
  is breadth, not tangle.
- **`structure.health` is dead schema.** Column exists since
  `001_init.sql`, never read or written. Severity 2 (confusing, not
  dangerous) × Blast radius 2 (only touches anyone reading the schema) = **4**.
  Either wire up structure damage/decay against it or drop the column in a
  migration.
- **No test project for world-server or gateway.** Netcode framing and the
  voyage SQL are covered only by manual play-testing. Severity 3 × Blast
  radius 3 = **9** — real risk, but "add a test harness for a UDP server and
  a REST API" is a multi-night project of its own, not a single-night fix,
  and nothing concrete was observed broken. Candidate for a future night
  once a lightweight in-process test harness pattern is chosen (e.g. spin up
  `NetManager` loopback pairs in xUnit rather than a real socket).

## Feature backlog (ideas considered, not yet built)

- **Wall integrity from support** — a wall with nothing under/beside it
  should eventually collapse, reusing `Structure`/`TryPlace` the way fire
  fuel now reuses them. Rejected for tonight (see LOG.md) in favour of fire
  fuel because it touches building generally, a bigger surface to ship in
  one session.
- **Rain / weather extinguishing fires** — would make the fire-fuel system
  from tonight interact with a weather system that doesn't exist yet.
  Natural follow-up once `WorldClock` grows a weather axis.
- **Structure decay from the unused `health` column** — see debt item above;
  a decay-over-time or damage-from-harvesting-nearby-players mechanic would
  finally give that column a purpose.
