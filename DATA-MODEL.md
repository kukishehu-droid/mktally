# Tally — Data Model and API Surface

Companion to `REQUIREMENTS.md`. Read that first for the product decisions and the
verified data-source details.

PostgreSQL 16+. All DDL is plain SQL run by DbUp or Grate. There is no ORM and no
code-first migration generation.

---

## 1. Conventions

- `snake_case` everywhere. Tables singular.
- `id bigint generated always as identity primary key` unless stated otherwise.
- Timestamps are `timestamptz`, always UTC. Local time is a presentation concern.
- Money is `integer`, in **whole denars**. MKD has no circulating subunit in retail
  pricing and every source publishes integers. Never use floating point for money.
- Sizes normalise to `integer` grams or millilitres, with the original preserved.

---

## 2. Reference data

```sql
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

-- Hand-curated. 10 Struga stores = 90 ordered pairs (45 pairs, both directions). See REQUIREMENTS.md §7.3.
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
```

> Store the matrix **both directions**. One-way streets and the Crni Drim crossings mean
> A→B and B→A are not always equal.

---

## 3. Catalogue

```sql
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
    display_name_en text,                          -- LLM-generated, see §6.5 of spec
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
```

### Equivalence groups

What list items actually resolve against. See `REQUIREMENTS.md` §6.1.

```sql
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
```

---

## 4. Source data and prices

A `source_product` is **per chain**, not per store: the same SKU across all KAM stores is
one row. Prices are per store.

```sql
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
```

### Price observations

The big table. Append-only, one row per product per store per publication date.

```sql
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
```

> **Partition by month.** Nine Struga stores at ~5,700 products (Misa) is on the order of
> 30–40k rows per day. Create partitions ahead in a scheduled job; a missing partition is a
> hard insert failure.

### Current price

```sql
create view current_price as
select distinct on (po.source_product_id, po.store_id)
       po.*
from   price_observation po
order  by po.source_product_id, po.store_id, po.observed_on desc;
```

If this becomes a bottleneck, promote it to a materialised view refreshed at the end of
each ingest run. Do not reach for that until measurement says so.

---

## 5. Matching

```sql
-- Permanent verdict cache. Never ask the model about a pair twice. Spec §6.2 rule 2.
create table match_verdict (
    id            bigint generated always as identity primary key,
    left_key      text not null,                   -- normalised string, lexically smaller
    right_key     text not null,
    same_product  boolean not null,
    confidence    numeric(4,3),
    reason        text,
    decided_by    text not null,                   -- 'llm:claude-opus-5' | 'human:<user>'
    decided_at    timestamptz not null default now(),
    unique (left_key, right_key)
);

create table match_review_queue (
    id                   bigint generated always as identity primary key,
    source_product_id    bigint not null references source_product(id),
    candidate_product_id bigint references canonical_product(id),
    confidence           numeric(4,3),
    basket_frequency     integer not null default 0,   -- prioritise what people actually buy
    status               text not null default 'pending',
    reviewed_by          text,
    reviewed_at          timestamptz
);
```

> `left_key` / `right_key` must be stored in a canonical order (lexically smaller first) or
> the unique constraint will not deduplicate `(A,B)` against `(B,A)`.

---

## 6. Ingestion

```sql
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
```

---

## 7. Admin and configuration

```sql
create table admin_user (
    id            bigint generated always as identity primary key,
    username      text not null unique,
    password_hash text not null,                   -- PasswordHasher<T>, no EF
    created_at    timestamptz not null default now(),
    last_login_at timestamptz
);

-- Everything the admin page tunes. Seeded idempotently.
create table app_setting (
    key         text primary key,
    value       text not null,
    value_type  text not null,                     -- int|decimal|bool|string|cron
    description text,
    updated_at  timestamptz not null default now()
);
```

Seed values:

| key | default | type |
|---|---|---|
| `search.radius_m` | 3000 | int |
| `route.max_stops` | 2 | int |
| `route.detour_factor` | 1.3 | decimal |
| `route.walk_kmh` | 4.8 | decimal |
| `route.drive_kmh` | 25 | decimal |
| `route.stop_overhead_min` | 12 | int |
| `cost.value_of_time_per_hour` | 300 | int |
| `cost.fuel_price_per_litre` | 86 | int |
| `cost.consumption_l_per_100km` | 6.2 | decimal |
| `match.auto_accept_threshold` | 0.95 | decimal |
| `match.review_floor` | 0.60 | decimal |

---

## 8. API surface

Public endpoints. No auth in v1 — lists are device-local and nothing is user-specific.

```
GET  /api/catalog/search?q={text}&limit={n}
     → { groups: [...], products: [...] }
     Groups first. Matches MK, SQ and EN aliases via trigram.

GET  /api/catalog/products/{id}
     → product + current price at every in-range store, with published_on per row

GET  /api/catalog/groups/{id}
     → group + the canonical products in it

GET  /api/stores?lat={}&lon={}&radiusM={}
     → stores in range with distance, coordinates, opening hours, open_now

POST /api/baskets/compare
     → THE endpoint. Request and response below.

GET  /api/meta/settings
     → public defaults the app seeds its preferences screen from
```

### POST /api/baskets/compare

```jsonc
// request
{
  "origin": { "lat": 41.1772, "lon": 20.6735 },
  "items": [
    { "equivalenceGroupId": 120, "quantity": 1 },
    { "canonicalProductId": 4411, "quantity": 2 }   // brand-pinned
  ],
  "preferences": {
    "radiusM": 3000,
    "maxStops": 2,
    "mode": "car",                                  // "car" | "walk"
    "valueOfTimePerHour": 300,
    "fuelPricePerLitre": 86,
    "consumptionLPer100Km": 6.2
  }
}
```

```jsonc
// response
{
  "generatedAt": "2026-11-22T08:14:00Z",
  "priceDataRange": { "oldestPublishedOn": "2026-11-20", "newestPublishedOn": "2026-11-22" },
  "options": [
    {
      "kind": "multi",                              // "single" | "multi"
      "recommended": true,
      "basketTotal": 964,
      "itemsCovered": 8,
      "itemsRequested": 8,
      "missing": [],
      "totalMinutes": 31,
      "fuelCost": 24,
      "timeCost": 75,
      "netBenefitVsBest": 49,
      "explanation": "Two stops cost 15 minutes and 13 den more fuel than staying at Миса, and save 73 den.",
      "stops": [
        {
          "sequence": 1,
          "storeId": 64,
          "legMetres": 450,
          "legSeconds": 360,
          "subtotal": 528,
          "closesAt": "21:00",
          "lines": [
            { "canonicalProductId": 4411, "displayName": "ЛЕБ ТОНУС 450Г",
              "quantity": 1, "price": 75, "unitPrice": 16.66, "unitBasis": "100G",
              "isPromo": false, "publishedOn": "2026-11-20" }
          ]
        }
      ]
    }
  ]
}
```

**Response invariants** the app depends on:

- `options` always includes the best complete single-store basket when one exists.
- An incomplete option is never omitted; it carries a populated `missing` array.
- Every price line carries `publishedOn`. The UI must render it (`frontend/CLAUDE.md`).
- `explanation` is server-generated prose, not assembled client-side, so the reasoning
  stays consistent with the scoring.

### Admin endpoints

```
POST   /api/admin/login
GET    /api/admin/settings            PUT /api/admin/settings
GET    /api/admin/stores              PUT /api/admin/stores/{id}
GET    /api/admin/distances           PUT /api/admin/distances
GET    /api/admin/review-queue        POST /api/admin/review-queue/{id}
GET    /api/admin/ingest-runs         POST /api/admin/ingest/run
POST   /api/admin/import              -- the CSV/JSON fallback, spec §5.5
```

---

## 9. Versions and dependencies

Pin the actual versions at project init rather than copying numbers from here. The
constraints that matter:

| | Requirement | Why |
|---|---|---|
| .NET | 9 or later | Built-in `Microsoft.AspNetCore.OpenApi`, so no Swashbuckle |
| PostgreSQL | 16 or later | Partitioning ergonomics, `pg_trgm` |
| Node | Current LTS | React Native toolchain |
| React Native | Latest stable at init | No map SDK needed, so fewer native constraints |

**Backend packages:** `Npgsql`, `Dapper`, `PdfPig` (KAM PDFs), `AngleSharp` (Misa HTML),
`Anthropic` SDK, `Quartz` or `Microsoft.Extensions.Hosting` for scheduling,
`DbUp` or `Grate` for migrations.

**Required Postgres extensions:** `pg_trgm`. Create it in the first migration.

---

## 10. What this document does not decide

- Index tuning beyond the obvious lookups. Measure first.
- Whether `current_price` becomes materialised. Measure first.
- The category taxonomy's actual contents. Derive it from the chains' own
  `product_subgroup` / `Опис на стока` values once the first two adapters have run.
- Retention policy for `price_observation`. Keep everything until storage argues otherwise;
  history is cheap and enables "is this actually a good deal".
