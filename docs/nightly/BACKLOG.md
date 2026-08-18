# Ashfall — nightly backlog

Ranked by `Severity(1-5) × Blast radius(1-5)`. Anything ≥15, or any
multiplayer-correctness item ≥9, is a "fix it tonight" candidate per the
nightly-engineer rules — items below already cleared that bar and were left
for later nights, or were judged not to clear it and are recorded so nobody
re-discovers them from scratch.

## Open

### 1. Gateway character endpoints have no caller authentication — score 12 (3×4), design tradeoff, not a bug
`apps/gateway/Program.cs`: `GET/PUT /characters/{id}` and `POST /voyage*` trust
whoever calls them — no shared secret, no mTLS, nothing beyond "you know the
GUID." This is a documented, intentional trust boundary ("world-server A, the
trusted caller — never the client") for Phase 3a, not an oversight like
tonight's harvest bug — so it wasn't treated as tonight's must-fix. But it
means the gateway's HTTP surface is only as safe as network topology keeps it
off the public internet. **Before the gateway is ever reachable from outside
a private network**, add a shared secret header (or mTLS) that only
world-server processes hold, and reject requests without it. Cheap fix,
high value, but only urgent once deployment topology changes — flagging now
so it isn't forgotten.

### 2. No test project for `apps/world-server` / `apps/gateway` — score 12 (3×4)
Only `sim-core` has automated tests. The tick loop, message dispatch, the
voyage claim race (`DELETE ... RETURNING` in `gateway/Program.cs`), and
character load/save are exercised only by hand or by throwaway harnesses like
the one used to verify tonight's fix (see `LOG.md`). A `WorldServer.tests`
project using an in-process `NetManager` pair (client + server, no real
sockets) would let the harness-style verification from tonight become a
permanent regression test instead of a one-off script. Suggested first
targets: harvest pacing (promote tonight's manual harness), voyage claim
race (two concurrent claims → exactly one wins), and reconnect-with-stale-
inventory.

### 3. No schema version / migration path on `tile_diff` / `structure` — score 9 (3×3)
`infra/migrations` defines these tables with no version column. "World
storage = seed + diffs" (invariant #2) means a future tile-type renumbering
or a structure schema change has no migration story today — it'd require a
manual backfill script written under pressure instead of a planned migration.
Not urgent (no world has shipped), but worth a version column before one
does.

### 4. `CraftRequest` / `EatRequest` have no time pacing — score 6 (2×3)
Unlike `ChopRequest` (fixed tonight), crafting and eating have no cooldown.
Not currently exploitable for free resources — every craft/eat still consumes
real inventory 1:1, so spamming just burns through what you hold instantly
with no intended "this takes a moment" pacing. Low priority unless a future
recipe or food item makes instant-consumption itself the exploit (e.g., a
food item with a strong instant-heal that should have a cast time).

### 5. `SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines) — score 6 (2×3) each
Both exceed the audit's 400-line god-script bar. Neither shows the worse
smell (coupling to >3 systems via direct node paths) — they're long because
each is genuinely one system's entire client-side surface (HUD-and-menus,
world-render-and-input). Still worth a decomposition pass: `SurvivalHud`
could split its four menu tabs (Craft/Build/Items/Travel) into their own
scenes/components; `World3D` could separate terrain-mesh-build from
foliage-instancing from input-handling. See `ARCH.md` for detail.

### 6. `RequestChunk` has no rate limit — score 4 (2×2)
A client can request the same or arbitrary chunks repeatedly; chunk
generation is deterministic and cheap (32×32 tiles) so this isn't a real DoS
today, but it's the same missing-time-budget shape as tonight's `ChopRequest`
bug. Worth a pass once there's evidence it matters (e.g., generation gets
more expensive, or telemetry shows abuse).

### 7. No process supervision for `world-server` — score 4 (2×2)
`docker-compose` runs one instance with no restart policy visible in
`infra/docker/docker-compose.yml` beyond whatever Docker's default is, and no
health-check/liveness wiring. A crashed world-server currently just stops
accepting connections silently. Fine for local dev; needs an answer before
any real deployment.

## Feature ideas (not audited tonight — Phase 2 skipped per the audit
threshold rule; these are here so the next feature-night doesn't start cold)

- **Structure removal has no client-facing message.** `World.RemoveStructure`
  exists in `sim-core` but nothing calls it — a placed Wall/Campfire is
  permanent today. Reclaiming a placed structure (spend a tool, get some
  material back) would be a good "increases freedom, composes with existing
  systems" candidate for a future feature night.
- **Warmth radius and campfire cooperation** (`World.HasWarmthNear`) already
  makes campfires a shared resource between players standing near one —
  that's real multiplayer-interesting behavior already shipped. A natural
  extension: fuel consumption, so a campfire needs feeding and can be a
  point of cooperation or contest between players. Flagging as a strong
  Phase-2-style feature-night candidate; not scoped or designed tonight.
