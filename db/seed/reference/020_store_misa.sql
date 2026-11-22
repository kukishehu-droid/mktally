-- Misa's Struga stores. REQUIREMENTS.md §4.3 / §4.4.
-- Misa's feed carries no store list and no coordinates, so this is the curated roster.
-- Coordinates and opening hours still need capture; left null until then
-- (store is excluded from radius filtering while latitude is null).
--
-- Only Struga is in v1 scope. The Ohrid/Resen orgs (2, 9?, 11, 12?, 13) are known to the
-- adapter but deliberately not seeded. 9 and 12 have no city in the published name;
-- confirm before adding either.
--
-- Idempotent: inserts missing stores, never overwrites admin-curated fields.
insert into store (chain_id, external_id, name, city, municipality)
select c.id, v.external_id, v.name, v.city, v.municipality
from chain c
cross join (values
    ('3', 'Продавница 1 Пазар Струга', 'Струга',     'Струга'),
    ('7', 'Продавница Струга 2 ЈНА',   'Струга',     'Струга'),
    ('4', 'Продавница 2 Мислешево',    'Мислешево',  'Струга'),
    ('8', 'Продавница Драслајца',      'Драслајца',  'Струга')
) as v(external_id, name, city, municipality)
where c.code = 'misa'
on conflict (chain_id, external_id) do nothing;
