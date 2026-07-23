-- Ashfall warmth meter (Phase A: night survival).
--
-- Warmth is the fourth survival meter: it drains when a player is exposed at
-- night and recovers in daylight or near a heat source (a campfire). An empty
-- warmth meter bleeds health like starvation, so night is now a real threat and
-- the campfire has a purpose. Stored as display points (0..100), like the other
-- meters. Existing characters default to full warmth.

ALTER TABLE character
    ADD COLUMN IF NOT EXISTS warmth INTEGER NOT NULL DEFAULT 100;
