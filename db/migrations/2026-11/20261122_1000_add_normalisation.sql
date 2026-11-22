-- Stage 0 normalisation output and the brand lexicon. REQUIREMENTS.md §6.2.
-- Additions to DATA-MODEL.md §3/§4, which had no home for pack counts, the exact key,
-- the reasons a row was kept out of exact matching, or the brand list.

-- Brands and their spellings across scripts ("7DAYS", "7 DAYS", "7ДЕЈС").
-- Curated; seeded from db/seed/reference and grown in the admin page.
create table brand (
    id   integer generated always as identity primary key,
    name text not null unique                      -- canonical, folded Latin: 'ZDRAVJE'
);

create table brand_alias (
    alias    text primary key,                     -- as written; folded in code, so one alias maps to one brand
    brand_id integer not null references brand(id) on delete cascade
);

create index brand_alias_brand on brand_alias (brand_id);

-- norm_pack_count: 6 for "6 x 0.5 L". norm_base_size stays the total content in g/ml/pc.
-- norm_key:        BRAND|DESCRIPTOR|SIZE, null when the row is not safe for exact matching.
-- norm_flags:      why it is not safe (size_ambiguous, size_unit_price_mismatch, ...).
-- norm_version:    ProductNormaliser.Version that produced the norm_* columns; rows with an
--                  older version are re-normalised when the rules change.
alter table source_product
    add column norm_pack_count smallint,
    add column norm_key        text,
    add column norm_flags      text[] not null default '{}',
    add column norm_version    smallint;

-- Stage 1 deterministic match is an equality join on this.
create index source_product_norm_key on source_product (norm_key) where norm_key is not null;

-- Rows needing (re-)normalisation.
create index source_product_norm_stale on source_product (norm_version);

alter table canonical_product
    add column pack_count smallint not null default 1;
