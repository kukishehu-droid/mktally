-- Reference data: chains, stores, curated store-to-store distances.
-- DATA-MODEL.md §2.

create table chain (
    id          smallint primary key,
    code        text not null unique,              -- 'kipper' | 'kam' | 'misa'
    name        text not null,
    crawl_cron  text not null default '0 4 * * *',
    is_enabled  boolean not null default true
);

create table store (
    id                   bigint generated always as identity primary key,
    chain_id             smallint not null references chain(id),
    external_id          text not null,            -- KAM Id, Misa org, Kipper slug
    name                 text not null,
    address              text,
    city                 text,
    municipality         text,
    latitude             double precision,
    longitude            double precision,
    working_hours        text,                     -- as published, free text
    sunday_working_hours text,
    phone                text,
    is_active            boolean not null default true,
    created_at           timestamptz not null default now(),
    updated_at           timestamptz not null default now(),
    unique (chain_id, external_id)
);

-- Hand-curated, both directions (one-way streets, Crni Drim crossings).
-- REQUIREMENTS.md §7.3.
create table store_distance (
    from_store_id bigint not null references store(id),
    to_store_id   bigint not null references store(id),
    metres        integer not null,
    walk_seconds  integer not null,
    drive_seconds integer not null,
    source        text not null default 'manual',
    updated_at    timestamptz not null default now(),
    primary key (from_store_id, to_store_id),
    check (from_store_id <> to_store_id)
);
