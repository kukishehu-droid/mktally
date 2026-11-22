-- Matching: permanent verdict cache and human review queue. DATA-MODEL.md §5.

-- Never ask the model about a pair twice. REQUIREMENTS.md §6.2 rule 2.
create table match_verdict (
    id            bigint generated always as identity primary key,
    left_key      text not null,                   -- normalised string, lexically smaller
    right_key     text not null,
    same_product  boolean not null,
    confidence    numeric(4,3),
    reason        text,
    decided_by    text not null,                   -- 'llm:<model>' | 'human:<user>'
    decided_at    timestamptz not null default now(),
    unique (left_key, right_key),
    -- Enforces the canonical ordering the unique constraint relies on, so (B,A) can
    -- never be inserted alongside (A,B). Byte order, so it does not depend on the
    -- database collation; application code must compare with ordinal semantics too.
    constraint match_verdict_key_order check (left_key collate "C" < right_key collate "C")
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
