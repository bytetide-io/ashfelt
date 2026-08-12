-- Ashfall ownership claim on normal join (fixes a same-character dupe hole).
--
-- character.owner_world_id already exists for the voyage flow, but until now
-- it was only read/written by /voyage and /voyage/claim. A normal join (Hello
-- with no voyage ticket) went straight to GET /characters/{id} and never
-- touched ownership at all — so nothing stopped the same character UUID being
-- loaded by two world-servers at once (two client instances, or a reconnect
-- racing a still-live session). Each server then held an independent in-memory
-- copy of the inventory, and anything placed into world state directly (a
-- built structure) persisted from both, duplicating whatever resources paid
-- for it. This violates the invariant already documented in
-- docs/voyage-transfer.md: "a character is owned by exactly one world-server
-- at a time."
--
-- owner_claimed_at backs a staleness self-heal, the same shape as the voyage
-- ticket TTL: a world-server that crashes without ever disconnecting its
-- players never calls /characters/{id}/release, so without a timeout the
-- character would be locked out of every world forever.

ALTER TABLE character
    ADD COLUMN IF NOT EXISTS owner_claimed_at TIMESTAMPTZ;
