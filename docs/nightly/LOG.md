# Nightly log

First run of the nightly-engineer process on this repo — `docs/nightly/`
didn't exist yet, so this session created it (`LOG.md`, `BACKLOG.md`,
`ARCH.md`) alongside tonight's fix.

## 2026-08-15 — AUDIT-FIX

**Chose:** Server-side interest management for `PlayerStates`: each player's
15 Hz position snapshot is now filtered to peers within 64m
(`Player.IsWithinInterestOf`, `apps/world-server/Player.cs`), instead of every
player's position being broadcast to every other player every tick. Client-side
(`RemotePlayers.cs`) now removes a remote player's body when it drops out of a
snapshot, since "moved out of range" is a legitimate way to lose a peer now,
not just an explicit `PlayerLeft`.

**Because:** Phase 1 audit. `docs/architecture.md` and `CLAUDE.md` both declare
interest management a load-bearing invariant ("a client only ever receives
entities/chunks near it"), but the actual `PlayerStates` broadcast in
`apps/world-server/Program.cs` sent every connected player's position to every
other connected player, unconditionally, 15 times a second — an undocumented
gap against a declared invariant, not a recorded decision to break it.
Scored Severity 4 × Blast radius 5 = 20 (every world-server, every tick, every
player, and it gets worse — unboundedly, O(playerCount²) bandwidth — as a
world fills up): comfortably over the "fix it tonight, skip Phase 2" threshold,
and squarely a multiplayer-correctness / bandwidth finding regardless of how
it's scored.

**Changed:**
- `apps/world-server/Player.cs` — added `IsWithinInterestOf`, using the same
  horizontal-distance pattern as the existing `IsWithinReach`, radius derived
  from the already-declared-but-unused `Tuning.InterestRadiusChunks` (64m).
- `apps/world-server/Program.cs` — replaced the single global
  `Broadcast(PlayerStates)` with `SendPlayerStates`, which builds one filtered
  snapshot per recipient.
- `apps/client/scripts/world3d/RemotePlayers.cs` — `Apply` now diffs the
  incoming snapshot against currently-tracked bodies and removes anyone
  missing, so a player who walks out of range disappears instead of freezing
  in place forever.
- `docs/architecture.md` — updated invariant 5 to say what's actually
  implemented (`PlayerStates`) vs. still a gap (structures, tiles, backfill).
- `docs/nightly/{LOG,BACKLOG,ARCH}.md` — created.

**Risk:** Low, and easy to spot if wrong. If the interest radius or the
per-recipient filtering were broken, the symptom is either "you can't see
nearby players" (radius too small / filter inverted) or "distant players'
capsules never disappear" (client-side removal not wired) — both immediately
visible in a 2-player session, nothing subtle or delayed. Revert is a single
commit revert; no persisted data or wire format changed (`PlayerStates`'
byte layout is unchanged, only which players are included).

**Verified:**
- `dotnet test tests/sim-core.tests` — 84/84 passed, unaffected by this change
  (confirms the change didn't disturb shared sim-core rules).
- `dotnet build` clean (0 warnings, 0 errors) on all four projects:
  `apps/world-server`, `apps/client` (Godot.NET.Sdk, without the Godot editor
  — confirms the C# compiles; does not confirm the scene/editor side), `apps/gateway`.
  Note: this sandbox had no .NET SDK installed at session start; installed
  `dotnet-sdk-8.0` and `dotnet-sdk-10.0` via apt (Ubuntu noble-updates) to be
  able to verify any of this at all.
- Live 2-client functional test: wrote a throwaway LiteNetLib harness (not
  committed — lived in the session scratchpad) that connects two simulated
  clients to a running `world-server` instance (in-memory, no Postgres, no
  gateway) and does the full `Hello`/`Welcome` handshake. Confirmed (a) two
  co-located players each see the other in their `PlayerStates` snapshot, and
  (b) after walking one player ~110m away (via a stream of legal `ClientState`
  updates, so `MovementRules` never rejected the *distance*, only occasional
  ground-clearance edge cases from moving purely along X) both players'
  snapshots drop to themselves-only. Server log showed no exceptions, clean
  connect/disconnect.

**Not verified:**
- The actual Godot client running on-device or in the editor — no Godot
  editor/runtime available in this environment, only `dotnet build` of the
  C# project. The rendering and input side of `RemotePlayers.cs` (the tween,
  `QueueFree` on removal) is untested beyond code review.
- Behaviour with Postgres attached (`ASHFALL_DB` set) — tonight's test ran the
  world-server in memory-only mode. This change doesn't touch persistence, so
  risk here is low, but it wasn't exercised.
- Multi-world-server / voyage interaction with the fix — only tested a single
  world-server with two clients, not a voyage handoff.
- A human should eyeball two real devices/editor instances moving apart and
  confirm remote players visually vanish cleanly (no pop/flicker) rather than
  just trusting the packet-level assertions above.

**Rejected tonight:** N/A — the audit crossed the "must fix, skip Phase 2"
threshold, so no feature ideas were designed or rejected tonight.

**Added to backlog:** see `docs/nightly/BACKLOG.md` — five more findings from
the same audit pass, top two being the same interest-management gap applied to
`StructurePlaced`/`TileChanged`/structure backfill (score 12 and 9), plus the
missing world-server test project, no rate limiting on request messages, and
two lower-priority maintainability notes.

**Question for the human:** None blocking. One judgment call worth a look
when you have a minute: I used the existing-but-previously-unused
`Tuning.InterestRadiusChunks = 1` (→ 64m) as the interest radius rather than
inventing a new constant, on the theory that it was clearly declared for
exactly this purpose and just never wired up. If 64m feels too tight or too
generous once you're actually walking around, it's a one-line tuning change,
not a design change.
