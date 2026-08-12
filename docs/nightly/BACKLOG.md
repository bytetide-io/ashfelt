# Nightly backlog

Findings not fixed yet, ranked by `Severity(1-5) × Blast radius(1-5)`. Pick
from the top on a future night; log why if you skip one or disagree with a
score.

## Open

### 1. No automated tests for world-server or gateway — Severity 4 × Blast 4 = 16
`tests/sim-core.tests` is thorough, but the UDP protocol handling
(`apps/world-server/Program.cs`), the ownership-claim/release/staleness logic
added tonight, and the voyage handshake (`apps/gateway/Program.cs`) have zero
automated coverage. A regression in any of these — e.g. the claim `WHERE`
guard, the voyage ticket TTL self-heal — would currently only be caught by a
human noticing a dupe or a stuck reconnect in manual testing. Suggest: an
integration test project that spins up the gateway in-process
(`WebApplicationFactory`) against a test Postgres (or a fake `NpgsqlDataSource`
if that's too heavy), and exercises claim/release/voyage races directly —
this is exactly the kind of race that's cheap to write a test for and
expensive to debug from a player report.

### 2. `SurvivalHud.cs` is a 940-line god-script — Severity 3 × Blast 3 = 9
`apps/client/scripts/world3d/SurvivalHud.cs` owns survival meters, the
hotbar, the crafting/building/items/travel tab sheet, and touch input for all
of them. It's the client's largest file by a wide margin and knows about
more than three systems (survival, inventory, crafting, building, voyage).
Not urgent — it works, and correctness risk is low since it's presentation
over server-authoritative state — but it's the first place a UI change gets
expensive. Suggest splitting into a HUD-meters component and a separate
craft/build sheet component, both driven by the same design-system tokens.

### 3. `CharacterState` / world-save payloads have no version field — Severity 3 × Blast 3 = 9
Neither the JSON `CharacterState` (gateway REST body) nor the persisted
`tile_diff`/`structure` rows carry a schema version. Migrations so far have
all been additive (`ADD COLUMN IF NOT EXISTS`) so this hasn't bitten yet, but
there's no mechanism today to handle a genuine shape change (e.g. renaming or
restructuring an inventory field) without either a silent data loss on old
saves or a manual one-off migration script written under pressure. Suggest:
add a `SchemaVersion` int to `CharacterState` now, while it's cheap, even if
nothing reads it yet.

### 4. `World3D.cs` mixes input, camera, networking and spawning — Severity 2 × Blast 3 = 6
735 lines covering touch input routing, the orbit camera rig, the harvest
reticle raycast, and handling every inbound message type
(`PlayerStates`, `InventoryUpdate`, `StructurePlaced`, ...). Lower urgency
than SurvivalHud since it's more naturally a scene root, but the network
message handling in particular could move to a thin dispatcher so this file
shrinks to scene wiring.

### 5. World registry is a hardcoded `Dictionary` in gateway `Program.cs` — Severity 2 × Blast 2 = 4
`worlds` (continent-a/continent-b, fixed host:port) is a static in-memory
table, explicitly flagged in its own comment as a Phase 3+ stub for live
world-server registration. Fine for one dev box; will not survive a second
gateway instance or a world-server that isn't always at the same address.
Already tracked as a known stub, listed here only so it doesn't get
forgotten once more than one world-server exists outside local dev.

### 6. Save-on-disconnect doesn't check the ownership claim is still held — Severity 3 × Blast 2 = 6
Follows from tonight's fix. A normal join that starts with the gateway
unreachable degrades to an unpersisted, in-memory character (deliberate —
see LOG.md 2026-08-12). But `PeerDisconnectedEvent`'s save-on-leave still
fires unconditionally on any session with a non-empty `CharacterId`, whether
or not that session ever actually held the ownership claim. If the gateway
comes back mid-session and a second world-server legitimately claims the
same character in the meantime, the first (unowned, degraded) session's
final save could still land and clobber real state — `PUT /characters/{id}`
was deliberately left ownership-agnostic (see its comment) so this isn't a
new hole tonight's fix introduced, but tonight's fix also didn't close it.
Narrow in practice: requires the gateway to be down specifically at Hello and
recover mid-session while a second world-server claims the same character —
worth closing properly (e.g. only save if this world's claim is confirmed
still held) rather than extending tonight's change further.

## Resolved

### ~~Character ownership not enforced on normal join (dupe hole)~~ — Severity 5 × Blast 5 = 25
Fixed 2026-08-12. See LOG.md and `infra/migrations/005_ownership_claim.sql`.
