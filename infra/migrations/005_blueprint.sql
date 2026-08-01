-- Ashfall blueprint building (Foundation/Phase A: architect-designed structures).
--
-- A blueprint is a player-designed building committed as a build site: the owner
-- supplies materials into on-site storage and raises the pieces one by one. Only
-- the owner sees the pending (unbuilt) hologram; built pieces are public. Like
-- everything else here, this is player-caused state layered over generated
-- terrain — the seed+diffs invariant is untouched.

CREATE TABLE IF NOT EXISTS blueprint (
    id          BIGINT      PRIMARY KEY,
    world_id    TEXT        NOT NULL REFERENCES world(id) ON DELETE CASCADE,
    owner_id    UUID        NOT NULL,
    chunk_x     INTEGER     NOT NULL,
    chunk_y     INTEGER     NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS blueprint_chunk_idx
    ON blueprint (world_id, chunk_x, chunk_y);

-- One row per piece. The canonical slot (x, y, level, layer) is the key, so the
-- wall shared by two cells can never be stored twice. `built` flips false->true
-- on the completing strike; partial strike progress is transient and never
-- written.
CREATE TABLE IF NOT EXISTS blueprint_piece (
    blueprint_id BIGINT   NOT NULL REFERENCES blueprint(id) ON DELETE CASCADE,
    x            INTEGER  NOT NULL,
    y            INTEGER  NOT NULL,
    level        INTEGER  NOT NULL,
    layer        SMALLINT NOT NULL,
    kind         SMALLINT NOT NULL,
    material     SMALLINT NOT NULL,
    built        BOOLEAN  NOT NULL DEFAULT false,
    chunk_x      INTEGER  NOT NULL,
    chunk_y      INTEGER  NOT NULL,
    PRIMARY KEY (blueprint_id, x, y, level, layer)
);

CREATE INDEX IF NOT EXISTS blueprint_piece_chunk_idx
    ON blueprint_piece (chunk_x, chunk_y);

-- Materials stockpiled on site, awaiting the pieces they will pay for.
CREATE TABLE IF NOT EXISTS build_site_storage (
    blueprint_id BIGINT   NOT NULL REFERENCES blueprint(id) ON DELETE CASCADE,
    item         SMALLINT NOT NULL,
    amount       INTEGER  NOT NULL,
    PRIMARY KEY (blueprint_id, item)
);
