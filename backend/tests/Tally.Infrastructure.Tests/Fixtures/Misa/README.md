# Misa fixtures

Live capture (2026-11-22):

- `org3_page1_trimmed.html`: `?org=3&search=&perPage=100&page=1`, trimmed to the first five
  rows plus every promo row on the page. Table and pagination markup are as served
  (whitespace collapsed). It shows the two-row header and the combined
  `299 ден. Попуст:3%` promo cell.

Hand-built:

- `synthetic_page1.html`: single-row header, ragged rows, invented pagination. Kept to cover
  a layout without the grouped promo heading, and the "no unit price" / "bad price" cases.

When the page changes, re-capture the live file the same way and diff.
