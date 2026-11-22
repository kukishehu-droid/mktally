-- Chains. Idempotent: inserts missing rows, refreshes names, leaves
-- admin-tuned crawl_cron / is_enabled alone.
-- Crawl times are staggered (REQUIREMENTS.md §5.3: do not fire simultaneously).
insert into chain (id, code, name, crawl_cron) values
    (1, 'kipper', 'Кипер', '0 4 * * *'),
    (2, 'kam',    'КАМ',   '20 4 * * *'),
    (3, 'misa',   'Миса',  '40 4 * * *')
on conflict (id) do update
    set code = excluded.code,
        name = excluded.name;
