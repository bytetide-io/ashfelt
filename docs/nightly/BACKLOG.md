# Nightly backlog

Ranked by `Severity (1-5) × Blast radius (1-5)`. Pick from the top unless you
have a specific reason not to — write that reason down here if you skip one.

## 1. Interest management is unimplemented — score 12 (Severity 3 × Blast 4)

`Tuning.InterestRadiusChunks = 1` exists in `packages/shared-proto/Protocol.cs`
but is never read. Every broadcast in `apps/world-server/Program.cs`
(`PlayerStates` every tick, `TileChanged`, `HarvestProgress`, `StructurePlaced`,
`StatsUpdate` every 2s) goes to **every** connected peer, unconditionally,
regardless of distance. This is `architecture.md` invariant #6, currently
unmet.

Not scored ≥15 because nothing is *wrong* yet — no desync, no dupes, no
exploit, and worlds are small during dev/testing — but it's an O(n) fan-out
per broadcast (so O(n²) total traffic as a world's population grows), and it's
an explicitly named architectural invariant, not an incidental gap.

Rough bandwidth today, unreliable `PlayerStates` only, 15 Hz: header (2B) +
20B/player. At N players broadcasting to N peers, each peer receives
`(2 + 20N)` bytes 15×/sec. N=10 → ~3 KB/s/player (trivial on cellular).
N=50 → ~15 KB/s/player. N=200 in one world (well beyond what "many bounded
worlds" implies any one region should hold) → ~60 KB/s/player just for
positions, before harvest/structure/stats traffic. Likely fine for the
population one bounded world is meant to hold, worth fixing before that
assumption is tested.

**Fix shape**: server already tracks `player.Position` per player; filtering
`Broadcast` calls to peers within `InterestRadiusChunks` (converted to metres)
of the event's tile/player is a `Program.cs`-local change, no protocol change
needed — the wire format doesn't care who receives it.

## 2. World-server & gateway have near-zero test coverage — score 9 (Severity 3 × Blast 3)

See `ARCH.md`'s coverage table. `Player.cs`'s invariants (`ConsumeOne`,
`ApplyCraft` — both `throw` on over-spend, by design, so a regression would be
a crash, not a silent bug, but still uncaught), `WorldStore`'s diff/structure
round-trip, and the gateway's voyage ticket mint/claim/expire state machine
(`apps/gateway/Program.cs:106-206`) are the highest-value untested surfaces.
`tests/world-server.tests` was created tonight (currently just
`PendingSaveTrackerTests`) — extending it is now zero-setup-cost for the next
person who picks this up.

## 3. `world-server/Program.cs` message dispatch is trending toward a god-script — score 6 (Severity 2 × Blast 3)

468 lines, one `switch` over `MessageId` in a single top-level-statements
file, touching networking, persistence, and four separate gameplay rule sets
(harvest, craft, place, survival) directly inline. Not yet a problem — each
case is short and each rule set is still called through `sim-core`, so there's
no duplicated logic — but every new message type grows one file that already
knows about most of the server's systems. If the message count keeps growing,
extract each case into a handler method (or a small per-message handler
class) before it does.

## 4. `World3D.Build()` renders a fixed radius around the world origin, not around the player — score 4 (Severity 2 × Blast 2)

`World3D.cs:18,256-266`: `Radius` chunks are built once, centered on chunk
(0,0), when the world is entered. Walking far enough from spawn walks off the
edge of generated terrain. Explicitly commented as a first-slice limitation
("Chunks generated around the origin for this first slice"), and `RequestChunk`
/`ChunkReceived` already exist in the protocol and `WorldConnection` unused —
the plumbing for real streaming is half-built. Low urgency while worlds are
tested near spawn; becomes a real player-facing bug the moment someone walks
a few hundred metres.

## 5. `SurvivalHud.cs` is 940 lines — score 4 (Severity 2 × Blast 2)

Entirely procedural UI construction, one `Build*` method per HUD region
(meters, top-right, action sheet, hotbar, gather prompt, touch controls) —
already decomposed, not tangled, so this is a soft flag on size alone rather
than a coupling problem. Worth splitting into a few files (e.g. one per tab:
craft/build/items/travel) if more HUD surface is added, not urgent today.

## Rejected as tonight's Phase-2 feature work

Not applicable — Phase 1 found a ≥15-scored, multiplayer-correctness finding
(the reconnect save race, see LOG.md), which per the standing instructions
means stop at one fix and skip Phase 2 entirely.
