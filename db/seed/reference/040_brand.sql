-- Brand lexicon seed. Idempotent.
-- Only brands seen in the verified samples (REQUIREMENTS.md §4) so far. Grow it from data:
-- after the adapters have run, the most frequent leading tokens of unmatched source
-- products across chains are the candidates (see the query at the end of this file).

insert into brand (name) values
    ('7DAYS'), ('AGRINA'), ('DORA'), ('MARLBORO'), ('MILORD'), ('TONUS'), ('ZDRAVJE')
on conflict (name) do nothing;

insert into brand_alias (alias, brand_id)
select v.alias, b.id
from (values
    ('7DAYS',    '7 DAYS'),
    ('7DAYS',    '7ДЕЈС'),
    ('7DAYS',    '7 ДЕЈС'),
    ('AGRINA',   'АГРИНА'),
    ('DORA',     'ДОРА'),
    ('MARLBORO', 'МАЛБОРО'),
    ('MARLBORO', 'МАРЛБОРО'),
    ('MARLBORO', 'MALBORO'),
    ('MILORD',   'МИЛОРД'),
    ('TONUS',    'ТОНУС'),
    ('ZDRAVJE',  'ЗДРАВЈЕ')
) as v(brand, alias)
join brand b on b.name = v.brand
on conflict (alias) do nothing;

-- Candidate brands, for the admin page or a manual session (not executed here):
--
-- select split_part(norm_descriptor, ' ', 1) as leading_token,
--        count(*)                            as products,
--        count(distinct chain_id)            as chains
-- from   source_product
-- where  norm_brand is null or norm_brand = split_part(norm_descriptor, ' ', 1)
-- group  by 1
-- having count(distinct chain_id) > 1
-- order  by products desc
-- limit  200;
