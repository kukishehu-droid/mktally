-- KAM's Struga stores. Three, from POST https://kam.com.mk/ShopsWeb/LoadShopList on
-- 2026-11-22, filtered on City (not Name: "Генц" is in Struga without saying so).
-- The adapter's discovery returns the same details nightly; this seed only makes the
-- stores exist before the first crawl. Which bank of the Crni Drim each is on: still to record.
--
-- Idempotent: inserts missing stores, never overwrites admin-curated fields.
insert into store (chain_id, external_id, name, address, city, municipality, latitude, longitude,
                   working_hours, sunday_working_hours, phone)
select c.id, v.external_id, v.name, v.address, 'Струга', 'Струга', v.latitude, v.longitude,
       v.working_hours, v.sunday_working_hours, v.phone
from chain c
cross join (values
    ('38', 'Генц',       'ул. Попово Блато бр.1/приземје - 1', 41.18688301123239::double precision, 20.675163482205495::double precision, '08:30-21:00',   '09:00-21:00',   '071/291-388'),
    ('64', 'Струга - 3', 'ул. ЈНА бр.1 згр.1/1-1 пр.',         41.17813888937535,                   20.684157180582737,                   '07:30-21:00',   '08:00-19:30',   '070/311-670'),
    ('69', 'Струга - 5', 'Ул. Маршал Тито бр. 108, Струга',    41.17720096464695,                   20.673502974602627,                   '07:30 - 22:00', '08:00 - 22:00', '072/314-228')
) as v(external_id, name, address, latitude, longitude, working_hours, sunday_working_hours, phone)
where c.code = 'kam'
on conflict (chain_id, external_id) do nothing;
