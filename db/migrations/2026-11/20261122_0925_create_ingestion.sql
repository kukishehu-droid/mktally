-- Ingestion bookkeeping. DATA-MODEL.md §6.

create table source_fingerprint (
    chain_id        smallint not null references chain(id),
    store_id        bigint not null references store(id),
    fingerprint     text not null,
    last_checked_at timestamptz not null,
    last_parsed_at  timestamptz,
    primary key (chain_id, store_id)
);

create table ingest_run (
    id              bigint generated always as identity primary key,
    chain_id        smallint not null references chain(id),
    store_id        bigint references store(id),
    started_at      timestamptz not null default now(),
    finished_at     timestamptz,
    status          text not null default 'running',  -- running|succeeded|skipped|failed
    trigger         text not null default 'schedule', -- schedule|manual|import
    records_seen    integer,
    records_changed integer,
    error           text
);
