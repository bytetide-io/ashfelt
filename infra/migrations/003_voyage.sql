-- Ashfall voyage coordination (Phase 3b: instant transfer between world-servers).
--
-- A character is owned by exactly one world-server at a time. Moving it is a
-- three-party handshake with the gateway as the single source of truth:
--
--   1. World-server A saves the character (PUT /characters), then POST /voyage
--      marks it in-transit and mints a single-use ticket.
--   2. A hands the ticket to the client and removes the entity.
--   3. World-server B, on the client's arrival, POST /voyage/claim validates and
--      consumes the ticket and takes ownership.
--
-- Ownership lives in character.owner_world_id. While in-transit it is NULL: no
-- world owns the live character, so a crash mid-transfer can never duplicate it.
-- A ticket that expires unclaimed self-heals: ownership is returned to the
-- world it left (voyage_ticket.from_world_id) with no operator intervention.

ALTER TABLE character
    ADD COLUMN IF NOT EXISTS owner_world_id TEXT;

-- One in-transit voyage per character (the PK). Re-minting overwrites a stale
-- ticket; claiming deletes the row so a replayed claim can never load twice.
CREATE TABLE IF NOT EXISTS voyage_ticket (
    character_id    UUID PRIMARY KEY REFERENCES character(id) ON DELETE CASCADE,
    ticket          UUID        NOT NULL,
    from_world_id   TEXT        NOT NULL,
    target_world_id TEXT        NOT NULL,
    expires_at      TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
