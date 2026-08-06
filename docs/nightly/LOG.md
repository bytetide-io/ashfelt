# Nightly log

## 2026-08-06 — AUDIT-ONLY (fix applied)

**Chose:** Filtered the world-server's per-tick `PlayerStates` position/yaw
broadcast so each client only receives players within
`Tuning.InterestRadiusMetres` (64m — derived from the existing but unused
`InterestRadiusChunks = 1`), instead of every connected player in the world.
Added a client-side stale-entry timeout (1.5s) so a remote player who exits
interest range is despawned even though no explicit "left" message exists.

**Because:** Audit found the `PlayerStates` broadcast (every tick, 15Hz,
unreliable, sent to every connected player) ignored distance entirely — it
sent the full player list to every recipient regardless of position. This
directly contradicts a load-bearing invariant stated in both
`docs/architecture.md` (#5) and `CLAUDE.md` (#6): "a client only ever
receives entities/chunks near it." Corroborating evidence: `shared-proto`
already had `Tuning.InterestRadiusChunks = 1` defined with a doc comment
("Radius... of the area a client is kept informed about") but it was never
referenced anywhere in the codebase — the intent existed, the wiring didn't.
Scored Severity 4 × Blast radius 4 = 16 (≥15 auto-fix threshold; also clears
the ≥9 multiplayer-correctness threshold on its own), so per the decision
rule this was fixed tonight and Phase 2 (new feature) was skipped.

**Bandwidth, estimated** (message = 1 byte id + 1 byte count + 20
bytes/player entry, 15 Hz):
- **Before**, N players in one world-server: total server egress for this
  message alone ≈ `N × (2 + 20N) × 15` bytes/sec. At N=30 (plausible for a
  busy continent hub) that's ≈264 KB/s total, ≈8.8 KB/s per connected
  client — scaling with total world population.
- **After**, same N but local density k within 64m (typically single
  digits away from a shared build site): per-client egress ≈
  `(2 + 20k) × 15` bytes/sec, independent of N. At k=8, ≈2.4 KB/s per
  client regardless of whether the world holds 10 players or 500.
- These are back-of-envelope estimates from the wire format, not measured —
  I have no running server/client pair in this sandbox to trace real
  traffic. Flagging as an estimate, not a measurement.

**Changed:**
- `packages/shared-proto/Protocol.cs` — added `Tuning.InterestRadiusMetres`
  (new constant, no wire format change).
- `apps/world-server/Program.cs` — `PlayerStates` broadcast now builds and
  unicasts a distance-filtered snapshot per recipient instead of one shared
  broadcast packet. Wire format of the `PlayerStates` message itself is
  unchanged (same message id, same field layout) — only which players are
  included per recipient changed, so no protocol version bump was needed.
- `apps/client/scripts/world3d/RemotePlayers.cs` — tracks last-seen time per
  remote player; `_Process` despawns anyone not seen for 1.5s. Needed
  because remote players can now legitimately stop appearing in snapshots
  (interest-range exit), not just on an explicit `PlayerLeft`.

**Risk:** The tick loop now does O(N²) distance checks per tick instead of
building one shared packet — a CPU/bandwidth tradeoff that's exactly the
point (this game's scaling story is "many bounded worlds," not "one huge
world," so per-world N is expected to stay small; flagged in ARCH.md if that
assumption ever changes). The 1.5s client-side despawn timeout is a new
timing constant with no test coverage — if it's too short, players near the
edge of interest range could flicker in/out during normal movement; if too
long, a disconnected/out-of-range player lingers visibly for up to 1.5s.
Picked 1.5s as ~22 ticks of tolerance against packet loss, but this is a
judgment call, not a measured one. `TileChanged`/`HarvestProgress`/
`StructurePlaced`/`PlayerLeft` broadcasts still ignore interest range
entirely — same underlying gap, not fixed tonight, logged to BACKLOG.md #1.

**Revert:** `git log --oneline -1` on this branch after the commit lands,
then `git revert <that-sha>`. The change is fully contained to the three
files listed above; reverting restores the previous unconditional broadcast
exactly.

**Verified:** Hand-reviewed the full diff line-by-line against the existing
`Vec3.HorizontalDistanceTo`, `NetDataWriter`/`NetPeer.Send` usage elsewhere
in the same file, and the `PlayerState` wire-read on the client
(`WorldConnection.cs`) to confirm the field order/types still match
byte-for-byte. Confirmed `Tuning` is not referenced by any test in
`tests/sim-core.tests` (so this change can't affect that suite). Confirmed
`Broadcast()` is still used by the other four message types so no unused-code
warning. Traced project references (`shared-proto` has none, `sim-core` →
`shared-proto`) to confirm the new constant couldn't be derived from
`TerrainGenerator.ChunkSize`/`TileMetres` without a circular reference, hence
the literal-with-derivation-comment.

**Not verified:** **This sandbox has no .NET SDK installed at all** (`dotnet`
not found) — I could not run `dotnet build`, `dotnet test
tests/sim-core.tests`, or launch the world-server/client to check for
compiler errors, warnings, or runtime behavior. Everything above is a manual
code review, not a build/test/runtime verification. A human needs to run
`dotnet build` and `dotnet test tests/sim-core.tests` on this branch before
trusting it, then ideally start two client instances against one
world-server (one joining late) to visually confirm: (1) remote players
still appear/move correctly within range, (2) a remote player leaving
interest range despawns cleanly within ~1.5s rather than flickering, (3) no
new Godot console errors/warnings. None of that was possible here.

**Rejected tonight:**
- Filtering the event broadcasts (`TileChanged`, `HarvestProgress`,
  `StructurePlaced`, `PlayerLeft`) in the same pass — same invariant gap, but
  quadruples the surface area of a single-night change for a much smaller
  bandwidth win (these aren't per-tick). Logged to BACKLOG.md #1.
- Splitting `SurvivalHud.cs` (940 lines) — real god-script debt, but a
  400+-line mechanical refactor with zero test backing is exactly the wrong
  shape of risk for an unsupervised night. Logged to BACKLOG.md #3.
- New feature work (Phase 2) — skipped per the decision rule once the
  interest-management finding scored above threshold.

**Added to backlog:** Event-broadcast interest gap (9), no world-server test
coverage (12), `SurvivalHud.cs` god script (4), `World3D.cs` god script (4),
unverified migration bootstrap (unscored, needs a human), unmeasured mobile
performance (unscored, needs a human). Full detail in `BACKLOG.md`.

**Question for the human:** None blocking — but please run `dotnet build` +
`dotnet test tests/sim-core.tests` on this branch before merging; I could not
verify the code compiles.
