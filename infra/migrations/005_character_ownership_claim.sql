-- Ashfall character ownership on normal join (Phase 3 hardening).
--
-- character.owner_world_id (migration 003) was only ever set/checked by the
-- voyage flow. A normal join (no voyage ticket — the common case) called
-- GET /characters/{id} without claiming or checking ownership at all, so the
-- same character could be loaded into two world-servers at once: each would
-- gather/craft against its own in-memory copy and independently save on
-- disconnect, with the last save silently discarding the other world's
-- changes. This is exactly the duplication/loss the ownership column exists
-- to prevent.
--
-- owner_claimed_at records when ownership was last granted, so a claim can
-- self-heal the same way an expired voyage ticket does: if the world holding
-- a character crashes without ever saving (and so without ever clearing
-- owner_world_id back to NULL), the character would otherwise be locked out
-- forever. A claim older than the staleness window is treated as abandoned
-- and may be taken by another world.

ALTER TABLE character
    ADD COLUMN IF NOT EXISTS owner_claimed_at TIMESTAMPTZ;
