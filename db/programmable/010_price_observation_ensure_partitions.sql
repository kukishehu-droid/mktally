-- Creates monthly price_observation partitions from the current month through
-- `months_ahead` months ahead. Idempotent; safe to call every night.
-- Returns the names of partitions it created (empty when nothing was missing).
--
-- Called by the worker before each ingest run. DATA-MODEL.md §4: a missing
-- partition is a hard insert failure.
create or replace function price_observation_ensure_partitions(months_ahead int default 3)
returns setof text
language plpgsql
as $$
declare
    m      date := date_trunc('month', current_date)::date;
    i      int;
    p_from date;
    p_to   date;
    p_name text;
begin
    for i in 0..months_ahead loop
        p_from := (m + make_interval(months => i))::date;
        p_to   := (m + make_interval(months => i + 1))::date;
        p_name := 'price_observation_' || to_char(p_from, 'YYYY_MM');

        if to_regclass(p_name) is null then
            execute format(
                'create table %I partition of price_observation for values from (%L) to (%L)',
                p_name, p_from, p_to);
            return next p_name;
        end if;
    end loop;
end
$$;
