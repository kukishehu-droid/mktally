-- Canonical catalogue and equivalence groups. DATA-MODEL.md §3.

create table category (
    id        integer generated always as identity primary key,
    parent_id integer references category(id),
    code      text not null unique,
    name_en   text not null
);

create table canonical_product (
    id              bigint generated always as identity primary key,
    brand           text,
    descriptor      text not null,
    size_value      numeric(10,3),
    size_unit       text,                          -- 'g' | 'ml' | 'pc'
    base_size       integer,                       -- normalised to g / ml / pc
    display_name_mk text not null,
    display_name_sq text,
    display_name_en text,                          -- LLM-generated (enrichment)
    category_id     integer references category(id),
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now()
);

-- Everything searchable collapses here: MK, SQ, EN, common misspellings.
create table product_alias (
    id                   bigint generated always as identity primary key,
    canonical_product_id bigint not null references canonical_product(id) on delete cascade,
    alias                text not null,
    locale               text,                     -- 'mk' | 'sq' | 'en' | null
    unique (canonical_product_id, alias)
);

create index product_alias_trgm on product_alias using gin (alias gin_trgm_ops);

-- What list items resolve against. REQUIREMENTS.md §6.1.
create table equivalence_group (
    id          bigint generated always as identity primary key,
    name_en     text not null,                     -- 'Milk, whole, 1 L'
    category_id integer references category(id),
    size_value  numeric(10,3),
    size_unit   text,
    attributes  jsonb not null default '{}'        -- {"fat_min":3.0,"organic":false}
);

create table equivalence_member (
    equivalence_group_id bigint not null references equivalence_group(id) on delete cascade,
    canonical_product_id bigint not null references canonical_product(id) on delete cascade,
    primary key (equivalence_group_id, canonical_product_id)
);
