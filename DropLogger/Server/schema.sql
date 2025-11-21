-- Complete Schema with ID support

CREATE TABLE IF NOT EXISTS drops (
    id BIGSERIAL PRIMARY KEY,
    zone_id INTEGER NOT NULL,
    
    -- Names (for human readability)
    item_name VARCHAR(150) NOT NULL,
    source_mob VARCHAR(150),

    -- IDs (for logic and joining)
    item_id INTEGER NOT NULL DEFAULT 0,
    source_mob_id INTEGER DEFAULT 0,

    quantity INTEGER NOT NULL,
    is_hq BOOLEAN NOT NULL DEFAULT FALSE,
    reporter_hash VARCHAR(64) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_drops_item_id ON drops (item_id);
CREATE INDEX IF NOT EXISTS idx_drops_source_id ON drops (source_mob_id);
CREATE INDEX IF NOT EXISTS idx_drops_reporter ON drops (reporter_hash);

CREATE MATERIALIZED VIEW drop_stats AS
SELECT 
    zone_id,
    item_id,
    source_mob_id,
    MAX(item_name) as item_name,
    MAX(source_mob) as source_mob,
    COUNT(*) as drop_count,
    SUM(quantity) as total_quantity
FROM drops
GROUP BY zone_id, item_id, source_mob_id;

CREATE INDEX idx_stats_lookup ON drop_stats (zone_id, item_id);