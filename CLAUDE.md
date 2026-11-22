# Tally

Mobile grocery app for Struga, North Macedonia. A user builds a shopping list; the app
recommends which shop or combination of shops to buy it at, weighing money saved against
time and fuel spent.

**Read `REQUIREMENTS.md` before starting non-trivial work.** It carries the verified data
source details (endpoints, payload shapes, PDF structure) that are expensive to rediscover.

**`DATA-MODEL.md`** has the PostgreSQL DDL, the API surface including the
`POST /api/baskets/compare` request and response shapes, and the version constraints.

Screen designs, with annotations explaining each decision:
https://claude.ai/code/artifact/70cc02ad-e571-4534-b707-167f8a3a964c

## Layout

```
backend/      C# / ASP.NET Core. Onion, two hosts over one core. See backend/CLAUDE.md
frontend/     React Native. See frontend/CLAUDE.md
db/           SQL migrations, seed, programmable objects
contracts/    openapi.json — the API contract, source of the generated TS client
```

Monorepo on purpose: a single change often spans a C# endpoint and its RN call site, and
the API contract should version atomically with both.

## Rules that span both sides

- **The API contract is generated, not hand-written.** Backend produces `contracts/openapi.json`;
  frontend generates its client from it. Never hand-maintain TypeScript types mirroring C# DTOs.
- **Every displayed price carries its publication date.** These are the chains' own published
  price lists, not live shelf checks, and KAM's run about two days behind. Never imply live prices.
- **Item coverage is always shown** ("7 of 8 items"). Never hide an incomplete basket.
- **Promo prices are visually flagged.** They expire, and an expired promo shown as current
  destroys trust faster than anything else in this app.

## Three chains

Kipper (JSON API), KAM (JSON store list + daily PDFs), Misa (plain GET, HTML). All three
publish under a North Macedonian legal mandate requiring daily per-store price publication
accessible without login. Details and exact endpoints in `REQUIREMENTS.md` §4.

Crawl politely: honest User-Agent with a contact URL, once nightly, respect `robots.txt`,
cache aggressively. Mandated publication is not a licence for unlimited reuse.

## Status (2026-11-22)

Build order is `REQUIREMENTS.md` §12. Steps 1–4 (schema, adapters, matching) carry the real
risk and should be validated before UI work starts.

Repo: https://github.com/kukishehu-droid/mktally (default branch `master`). The README is
also the crawler's contact page, so keep its TallyBot section accurate.

**Done, building, all tests passing:**

- **Schema + migrator.** `db/` migrations (incl. `brand`/`brand_alias` and `norm_*` columns on
  `source_product`), partition function, `current_price` view, idempotent reference seeds
  (chains, settings, brands, Misa, Kipper and KAM Struga stores). Runner:
  `backend/tools/Tally.Migrator` (DbUp; `TALLY_DB` env var, `--create`, `--dev`).
- **Shared crawler HTTP.** `Infrastructure/Ingestion/Http/PoliteHttp`: robots.txt (RFC 9309),
  retries, Cloudflare challenge reported and never bypassed. `Crawlers:ContactUrl` is
  required; set it to the repo URL.
- **Misa adapter** and **Kipper adapter**, verified against the live sites on 2026-11-22.
  Fixtures in `backend/tests/.../Fixtures` are trimmed live captures plus labelled synthetic
  edge cases. Live findings are recorded in `REQUIREMENTS.md` §4.1 and §4.3.
- **Normalisation** in `Tally.Domain/Catalog`: transliteration (incl. Latin look-alike
  letters), size/pack parsing, brand lexicon, exact key `BRAND|DESCRIPTOR|SIZE`, unit-price
  veto and confirmation. Only lexicon brands and trusted sizes produce a key.

**Open:**

- **KAM adapter written, not yet compiled or run** (no .NET SDK was at hand). First job:
  restore (new package PdfPig 0.1.16), build, run the tests. `Infrastructure/Ingestion/Kam`:
  store list (cached, one request per run), PDF via PdfPig, rows found by position
  (`KamTableReader`, pure, logic checked against the full live Генц PDF). `published_on` is
  the PDF's own stamp, not the URL date. Fixes KAM's `1 гр`/`1 мл` unit labels (really per
  kg / per L). Fixture is a 3-page cut of the real Генц PDF. Live findings in
  `REQUIREMENTS.md` §4.2. Once green, move this to Done.
- **KAM Струга-5 (69) publishes an empty table** (stamped 13.10.2026). The adapter raises
  `KamNoPriceListException`; the worker must keep the store visible with no current prices.
- Deterministic matching (equality join on `norm_key`) and the hand-labelled set of ~200
  pairs with measured precision/recall. Needs real data from the adapters first.
- No ingestion pipeline yet: no Worker host, no Dapper repositories, no COPY staging upsert.
- **Decision pending:** Kipper's robots.txt blocks named AI crawlers and declares
  `ai-train=no`; whether sending Kipper product names to an LLM for matching is acceptable.
- Data to capture: coordinates for the 4 Misa Struga stores, opening hours for Misa and
  Kipper, which bank of the Crni Drim each store is on, the store-to-store distance matrix.
- Development machine is Windows + Visual Studio; `git` is not on the PowerShell PATH.
