-- Per-chain source products and per-store price observations. DATA-MODEL.md §4.

-- One row per chain SKU, not per store.
create table source_product (
    id                    bigint generated always as identity primary key,
    chain_id              smallint not null references chain(id),
    source_key            text not null,           -- chain's own article code or hashed name
    raw_name_primary      text not null,           -- Macedonian
    raw_name_alt          text,                    -- Albanian (Kipper) or description
    raw_category          text,
    norm_brand            text,
    norm_descriptor       text,
    norm_size_value       numeric(10,3),
    norm_size_unit        text,
    norm_base_size        integer,
    canonical_product_id  bigint references canonical_product(id),
    match_method          text,                    -- 'barcode'|'exact_key'|'llm'|'manual'
    match_confidence      numeric(4,3),
    first_seen_at         timestamptz not null default now(),
    last_seen_at          timestamptz not null default now(),
    unique (chain_id, source_key)
);

create index source_product_unmatched on source_product (chain_id)
    where canonical_product_id is null;

-- Append-only. One row per product per store per ingest date.
-- Partitioned by month on observed_on. A missing partition is a hard insert failure,
-- so partitions are created ahead of time by price_observation_ensure_partitions()
-- (db/programmable), called from the worker on a schedule.
create table price_observation (
    id                bigint generated always as identity,
    source_product_id bigint not null references source_product(id),
    store_id          bigint not null references store(id),
    observed_on       date not null,               -- the date WE ingested
    published_on      date,                        -- the date the CHAIN says (KAM lags ~2d)
    price             integer not null,            -- selling price, whole denars
    regular_price     integer,
    promo_price       integer,
    discount_pct      numeric(5,2),
    unit_price        numeric(10,2),               -- may be null (Misa often omits)
    unit_basis        text,                        -- '1KG' | '1L' | '100G' | '1PC'
    is_available      boolean,                     -- KAM and Misa publish this; Kipper does not
    promo_from        timestamptz,
    promo_to          timestamptz,
    promo_type        text,
    ingest_run_id     bigint,
    primary key (id, observed_on)
) partition by range (observed_on);

create index price_obs_lookup on price_observation (source_product_id, store_id, observed_on desc);

-- Initial partitions: the current month plus the next six. The programmable function
-- takes over from here; this block exists so the very first deploy can accept inserts
-- before the worker has ever run. Inlined rather than calling the function because
-- migrations run before programmable objects.
do $$
declare
    m date := date_trunc('month', current_date)::date;
    i int;
    p_from date;
    p_to date;
begin
    for i in 0..6 loop
        p_from := (m + make_interval(months => i))::date;
        p_to   := (m + make_interval(months => i + 1))::date;
        execute format(
            'create table if not exists %I partition of price_observation for values from (%L) to (%L)',
            'price_observation_' || to_char(p_from, 'YYYY_MM'), p_from, p_to);
    end loop;
end
$$;
