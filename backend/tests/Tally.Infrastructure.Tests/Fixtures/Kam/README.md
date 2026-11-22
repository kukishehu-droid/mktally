# KAM fixtures

Live captures (2026-11-22):

- `shop_list_sample.json`: four stores verbatim from `POST https://kam.com.mk/ShopsWeb/LoadShopList`
  (85 stores live): Радовиш (73, the store in REQUIREMENTS.md §4.2) and the three Struga stores
  38, 64 and 69. Keeps the escaped `"\/Date(1795302000000)\/"` form, trailing space in
  `"Генц "`, an en dash in Радовиш's hours and the empty `Tags` of store 69.
- `price_38_trimmed.pdf`: pages 1, 8 and 18 of `https://kam.com.mk/2026/11/22/38.pdf`
  (Генц, 167 pages, 507 KB), cut out with their objects byte-for-byte; only the page tree,
  object numbers and xref were rebuilt. SHA-256
  `6b8f43230199dff19b1106086dbd834c3f69e80a35665594dae3eba72364975e`. 33 rows:
  - page 1: plain rows, wrapped names and descriptions;
  - page 8: `ДУО КЕКС ГРИСОН 500ГР`, a promotion with a regular price and "Попуст: 11%",
    and the `1 гр = 204.00 ден.` unit label that really means per kilogram;
  - page 18: `МИСИРКА ЦЕЛА ЗАМРЗНАТА РОНСАРД 2.8 КГ`, "промотивна цена" with no regular price.
  The stamp above the table reads `18.11.2026  5:46:25AM` although the URL says 22.11.

Not captured as files, covered by tests on hand-built glyphs instead:

- Store 69's file (`2026/11/22/69.pdf`, 23 KB): header only, no rows, stamped 13.10.2026.
- A missing dated path: HTTP 200, empty body, no Content-Type.

When KAM changes its layout, re-capture the same pages and check the header labels and
column edges in `KamTableReader` against them.
