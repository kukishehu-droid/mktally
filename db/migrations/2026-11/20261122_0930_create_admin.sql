-- Admin account and tunable settings. DATA-MODEL.md §7.
-- app_setting values are seeded idempotently from db/seed/reference.

create table admin_user (
    id            bigint generated always as identity primary key,
    username      text not null unique,
    password_hash text not null,                   -- PasswordHasher<T>, no EF
    created_at    timestamptz not null default now(),
    last_login_at timestamptz
);

create table app_setting (
    key         text primary key,
    value       text not null,
    value_type  text not null,                     -- int|decimal|bool|string|cron
    description text,
    updated_at  timestamptz not null default now()
);
