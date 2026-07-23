-- Ashfall character storage (Phase 3a).
--
-- A character is global: inventory, stats and (later) skills live here in the
-- gateway database, never in a world-server. World-servers hold world state
-- only and load/save a character over the gateway's REST API when a player
-- joins or leaves.
--
-- Identity is a device UUID the client generates once and persists locally;
-- there is no login. The character row is created on the first save.
--
-- State shape: inventory is a JSONB object of { ItemId-name: count }, keyed by
-- the stable ItemId enum names so a wire renumber never corrupts stored stacks.
-- The three survival meters are stored as their display points (0..100), the
-- same integers the protocol puts on the wire.

CREATE TABLE IF NOT EXISTS character (
    id            UUID PRIMARY KEY,
    inventory     JSONB       NOT NULL DEFAULT '{}'::jsonb,
    hunger        INTEGER     NOT NULL DEFAULT 100,
    stamina       INTEGER     NOT NULL DEFAULT 100,
    health        INTEGER     NOT NULL DEFAULT 100,
    last_world_id TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
