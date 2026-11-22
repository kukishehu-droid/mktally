-- Admin-tunable settings. DATA-MODEL.md §7.
-- Idempotent: inserts missing keys and refreshes type/description,
-- but never overwrites a value the admin has changed.
insert into app_setting (key, value, value_type, description) values
    ('search.radius_m',               '3000', 'int',     'Store search radius from the user, metres'),
    ('route.max_stops',               '2',    'int',     'Maximum stores in a recommended route'),
    ('route.detour_factor',           '1.3',  'decimal', 'Multiplier on straight-line distance for the origin leg'),
    ('route.walk_kmh',                '4.8',  'decimal', 'Walking speed, km/h'),
    ('route.drive_kmh',               '25',   'decimal', 'Town driving speed, km/h'),
    ('route.stop_overhead_min',       '12',   'int',     'Minutes added per store visited'),
    ('cost.value_of_time_per_hour',   '300',  'int',     'Value of time, denars per hour'),
    ('cost.fuel_price_per_litre',     '86',   'int',     'Fuel price, denars per litre'),
    ('cost.consumption_l_per_100km',  '6.2',  'decimal', 'Vehicle consumption, litres per 100 km'),
    ('match.auto_accept_threshold',   '0.95', 'decimal', 'Match confidence at or above which a match is accepted without review'),
    ('match.review_floor',            '0.60', 'decimal', 'Match confidence below which a candidate is discarded rather than reviewed')
on conflict (key) do update
    set value_type  = excluded.value_type,
        description = excluded.description;
