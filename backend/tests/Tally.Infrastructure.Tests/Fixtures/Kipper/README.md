# Kipper fixtures

Live captures (2026-11-22):

- `store_131_trimmed.html`: store 131's page, trimmed. The shortlink, body class, map
  link and the timestamp/table shortcode text are verbatim; Elementor wrappers simplified.
- `ajax_131_sample.json`: four real rows from `POST admin-ajax.php`
  (`action=get_products_data&post_id=22911&draw=1&start=0&length=500`), including one
  promotion announced in advance (price = normal price, future window).

Hand-built:

- `synthetic_store_page.html`: variant store page covering the directions-style map link
  (used by stores 84 and 118), a Macedonian month name, and the shortlink fallback for the
  post id.
- `synthetic_ajax_page.json`: edge cases not seen live yet: a live promo with price below
  normal, numeric price, missing image, missing price.

When Kipper changes its site, re-capture the two live files the same way and diff.
