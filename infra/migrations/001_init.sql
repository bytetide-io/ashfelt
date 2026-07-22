-- Ashfall initial schema.
--
-- Terrain is never stored: it is regenerated from (world seed, coordinate).
-- Only player-caused modifications are persisted.

CREATE TABLE IF NOT EXISTS world (
    id          TEXT PRIMARY KEY,
    seed        BIGINT      NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- One row per modified tile, keyed by chunk so a region loads with one query.
CREATE TABLE IF NOT EXISTS tile_diff (
    world_id    TEXT     NOT NULL REFERENCES world(id) ON DELETE CASCADE,
    chunk_x     INTEGER  NOT NULL,
    chunk_y     INTEGER  NOT NULL,
    local_x     SMALLINT NOT NULL,
    local_y     SMALLINT NOT NULL,
    tile        SMALLINT NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (world_id, chunk_x, chunk_y, local_x, local_y)
);

CREATE INDEX IF NOT EXISTS tile_diff_chunk_idx
    ON tile_diff (world_id, chunk_x, chunk_y);

CREATE TABLE IF NOT EXISTS structure (
    id          BIGSERIAL PRIMARY KEY,
    world_id    TEXT    NOT NULL REFERENCES world(id) ON DELETE CASCADE,
    chunk_x     INTEGER NOT NULL,
    chunk_y     INTEGER NOT NULL,
    tile_x      INTEGER NOT NULL,
    tile_y      INTEGER NOT NULL,
    kind        TEXT    NOT NULL,
    owner_id    UUID,
    health      INTEGER NOT NULL DEFAULT 100,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS structure_chunk_idx
    ON structure (world_id, chunk_x, chunk_y);
