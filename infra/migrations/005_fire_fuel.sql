-- Ashfall campfire fuel (nightly feature: fire needs feeding).
--
-- A campfire no longer burns forever once placed. It starts alight with the
-- wood spent crafting it (FireRules.InitialFuelTicks) and drains one tick of
-- fuel per server tick while lit; feeding it a Wood log
-- (FireRules.FuelTicksPerLog) adds more, up to FireRules.MaxFuelTicks.
-- World.HasWarmthNear only counts a campfire with fuel remaining, so an
-- untended fire goes cold and a group must keep tending it to survive the
-- night — the same tension the other survival meters already create.
--
-- Existing structures default to FireRules.InitialFuelTicks (15 ticks/s * 90s
-- * 3 logs = 4050): a real fuel level was never recorded before this column
-- existed, so every already-placed campfire is treated as freshly lit rather
-- than silently going dark the moment this ships.

ALTER TABLE structure
    ADD COLUMN IF NOT EXISTS fuel_ticks INTEGER NOT NULL DEFAULT 4050;
