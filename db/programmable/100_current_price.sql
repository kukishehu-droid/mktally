-- Latest observation per (product, store). DATA-MODEL.md §4.
-- Plain view until measurement says it needs to be materialised.
create or replace view current_price as
select distinct on (po.source_product_id, po.store_id)
       po.*
from   price_observation po
order  by po.source_product_id, po.store_id, po.observed_on desc;
