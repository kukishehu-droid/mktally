-- Kipper's Struga stores. Three, from kipper.mk's sitemap on 2026-11-22 (REQUIREMENTS.md
-- §4.4 listed two). Addresses and coordinates are from each store page; the coordinates
-- are the map link's destination. Opening hours are not published in structured form
-- and must be entered by hand. Which bank of the Crni Drim each is on: still to record.
--
-- Idempotent: inserts missing stores, never overwrites admin-curated fields.
insert into store (chain_id, external_id, name, address, city, municipality, latitude, longitude)
select c.id, v.external_id, v.name, v.address, 'Струга', 'Струга', v.latitude, v.longitude
from chain c
cross join (values
    ('131', 'Кипер 131', 'ул. Димче Ковачевски',   41.1754493::double precision, 20.6745462::double precision),
    ('084', 'Кипер 084', 'ул. Маркс - Енгелс 34',  41.1769906,                   20.6828735),
    ('118', 'Кипер 118', 'Дебарски пат',           41.1908408,                   20.6767341)
) as v(external_id, name, address, latitude, longitude)
where c.code = 'kipper'
on conflict (chain_id, external_id) do nothing;
