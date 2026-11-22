# Tally — Requirements Specification

**Version** 0.1 (draft) · **Date** 2026-11-20 · **Status** pre-implementation

A mobile app for Struga, North Macedonia. A user builds a grocery list; the app tells them
which shop (or combination of shops) to buy it at, weighing money saved against time and
fuel spent.

This document is written to be portable. It carries the verified technical findings for
each data source so that development can start on a different machine without repeating
the discovery work.

---

## 1. Scope

### In scope for v1

| | |
|---|---|
| Geography | Struga city only |
| Chains | Kipper, KAM, Misa (10 stores total in Struga) |
| Platform | React Native (iOS + Android) |
| Backend | C# / ASP.NET Core, PostgreSQL |
| Interface language | English |
| Product names | Original Macedonian / Albanian, with generated English aliases for search |
| Lists | Local to the device, no accounts, no sync |
| Transport modes | Car and walking |

### Explicitly out of scope for v1

- User accounts, list sync, sharing
- Public transport routing
- Any geography beyond Struga
- Chains beyond the three named
- In-app purchasing, delivery, loyalty cards
- Offline price browsing (the app requires connectivity)

### Why Struga

All three chains overlap there, which is what makes comparison meaningful. KAM is national
and Kipper is spread across several cities, but **Misa operates only in Struga, Ohrid and
Resen** (9 stores total). Skopje would give you only two of the three chains. Struga also
keeps the store count small enough to hand-verify product matching quality, which is the
single biggest risk in the product.

---

## 2. Decisions already settled

| Topic | Decision | Rationale |
|---|---|---|
| List input | Free text, app resolves to a canonical product, user confirms once | Catalogue-picking is too tedious for a 20-item list; silent matching risks comparing 1 L to 200 ml |
| List item semantics | An **intent** (`milk, 1 L, any brand`) with optional brand pin | Brand-switching is where most savings live |
| Partial baskets | Show the best store plus an explicit list of missing items | Most real baskets will not be fully stocked anywhere |
| Transport | Car and walking | Several Struga stores are within a few hundred metres of each other; fuel is meaningless on foot |
| Interface language | English, v1 | User decision. Source data is MK/SQ, so English aliases must be generated |
| Accounts | None. Lists stored on device | Removes auth, user storage and GDPR surface entirely |
| Distance & routing | **No maps provider.** Curated store-to-store matrix + Haversine for the origin leg | Users are local residents who know the town; a checkpoint line beats a map, and it removes a native dependency, an API key and a billing account |
| Crawl cadence | Nightly, ~04:00 local, per-chain configurable | See §5.3 |
| Markets | Predefined and curated. Users may opt out, never add | Keeps data quality under our control |
| Backend persistence | Raw SQL via Dapper. **No Entity Framework** | See §9.2 |
| Admin auth | Single admin account, `PasswordHasher<T>` standalone, no Identity stack | Avoids pulling EF in for one user |

---

## 3. Legal basis for price collection

North Macedonia amended its trade law (rulebook from the Ministry of Economy and Labour,
in force since **18 April**) requiring every retailer above microtrader size to publish:

- current **selling price and unit price**
- **per sales location**
- **updated daily**
- in a dedicated price-list section of their website
- **accessible without registration or login**

Penalties for non-compliance reach €10,000.

Three consequences for this project:

1. The data exists for every chain by law, not by goodwill.
2. No ToS click-through gate stands between us and the data.
3. Prices track actual shelf prices rather than a stale e-commerce catalogue.

**This is not a licence for unlimited reuse.** Mandated publication is not the same as
granted database rights. Read each `robots.txt`, identify the crawler honestly with a
contact URL in the User-Agent, pull once nightly, and cache aggressively.

---

## 4. Data sources (verified 2026-11-20; Kipper and Misa re-verified against the live sites 2026-11-22)

### 4.1 Kipper — JSON, easiest

Store pages carry the address, GPS coordinates (as a Google Maps link) and a "last price
update" timestamp. **Paths are not a fixed pattern** (verified 2026-11-22): the Struga
stores are `/mk/kipper-131struge/`, `/mk/kipper-84-struge/` and `/mk/kipper-118-struge/`
(Albanian "Strugë"). Find them in `https://kipper.mk/wp-sitemap-posts-post-1.xml`.

- Timestamp, server-rendered: `Датум и време на последно ажурирање на цените: 20 November 2026 - 15:43`
  (English month name). On 2026-11-22 all three Struga stores showed this same value, so
  it looks like a chain-wide import time, and **Kipper's prices lag about two days, like KAM's**.
- Map link: `/maps/@lat,lon,...` (131) or `/maps/dir//lat,lon/@viewport` (84, 118). In the
  second form the store is the part after `dir//`, not after `@`.
- The body class carries `postid-NNNNN`, the page's WordPress post id. That id, not the
  store number, identifies the store to the price endpoint.

The price table is DataTables with server-side processing. Verified request (2026-11-22):

```
POST https://kipper.mk/wp-admin/admin-ajax.php
Content-Type: application/x-www-form-urlencoded

action=get_products_data&post_id=22911&draw=1&start=0&length=500
```

No nonce, no column specs needed. `length=500` is honoured. Without an explicit order,
rows come back sorted by name.

Returns `{ draw, recordsTotal, recordsFiltered, data: [...] }`, counts as **strings**. For
store 131 (Struga), `recordsTotal` was **2,190**. Record shape:

```json
{
  "product_image": "000307.webp",
  "product_name": "7ДЕЈС МИНИ КРОАСАН КАКАО 60г",
  "product_name_alt": "7DAYS MINI KROASAN KAKAO 60gr",
  "product_price": "36",
  "product_price_normal": "36",
  "product_price_discount_percentage": null,
  "product_price_weight": "600",
  "product_type_alt": "1КГ=",
  "product_status": "D",
  "product_subgroup": "КИФЛИ И ТОРТИЧКИ",
  "promotion_datetime_from": null,
  "promotion_datetime_to": null
}
```

- `product_name` is Macedonian, `product_name_alt` is Albanian (not English)
- `product_price_weight` + `product_type_alt` together give the unit price (600 den per 1 kg).
  The weight price is **rounded to whole denars** (149 den for 12 pieces is `12`).
  `product_type_alt` variants seen: `1КГ=`, `1кг=`, `100гр=`, `1Л=`, `1ПАР=`, reversed `=1Л`
  and `=1КГ`, and empty.
- `product_image` looks like an article number (`000307.webp`); a few are null
- `product_status` was `D` on every row sampled; meaning unknown
- Promotions can be **announced in advance**: rows with `product_price = product_price_normal`
  and a future `promotion_datetime_from/to` (date only, `2026-11-24`)
- `product_subgroup` is a usable starting taxonomy
- **No EAN / barcode** in the payload
- Names contain Latin look-alike letters inside Cyrillic words (`KРУГСЕН` with a Latin K)
- Site sits behind Cloudflare; do not hammer it
- **robots.txt** allows general crawlers (`User-agent: * Allow: /`, `admin-ajax.php`
  explicitly allowed) but blocks named AI crawlers (ClaudeBot, GPTBot, anthropic-ai and
  others) and declares a Cloudflare content signal `search=yes, ai-train=no, use=reference`.
  Tally's crawler is not an AI crawler and does not train models, but the matching stage
  sends product names to an LLM, which is "ai-input", a use Kipper's signal leaves
  unspecified. See §3.

### 4.2 KAM — JSON store list + daily PDF price lists

**Store list**, one call, no auth:

```
POST https://kam.com.mk/ShopsWeb/LoadShopList
```

```json
{
  "Id": 73, "Name": "Радовиш",
  "Address": "бул. Александар Македонски бр.34 А",
  "Coordinates": "41.628251341026996, 22.455374587226718",
  "ContactPhone": "071/341-256",
  "City": "Радовиш", "Municipality": "Радовиш",
  "WorkingHours": "08:00 – 21:00",
  "SundayWorkingHours": "неработен ден",
  "Tags": ["bakery", "parking", "fishfood"],
  "ShopFiles": [{ "RelativePath": "2026/11/20/73.pdf", "DateUploaded": "/Date(1795129200000)/" }]
}
```

This single call gives GPS, opening hours **including Sunday**, phone, store facilities, and
the pointer to the price list. No manual store curation needed for KAM.

**Price list** is a plain GET at `https://kam.com.mk/{RelativePath}`:

```
https://kam.com.mk/2026/11/20/73.pdf     → HTTP 200, application/pdf, ~520 KB, 171 pages
```

Verified properties of that PDF:

- **Text-based, not scanned.** Zero image XObjects, no DCTDecode or CCITTFaxDecode.
- Produced by Crystal Reports, so layout is machine-generated and stable day to day.
- `ToUnicode` CMaps present; `pypdf` extracted clean Cyrillic on the first attempt.
- Columns: `Назив на стока` · `Продажна цена` · `Единична цена` · `Опис на стока` ·
  `Достапност во продажен објект` · `Редовна цена` · `Цена со попуст` · `Попуст(%)` ·
  `Вид на продажно поттикнување` · `Времетраење на промоција`
- **KAM publishes per-item availability** (`Да` / `Не`), which the other two do not.

Sample extracted rows:

```
ТОРТ. ДОРА КАКАО ФИЛ280   89ден.   100 гр = 31.78 ден.   MIDI ТОРТИЧКИ СО КАКАО ФИЛ   Да   89ден.
ЛЕБ ТОНУС 450 Г СТАНДАРД  75ден.   100 гр = 16.66 ден.   TONUS ЛЕБ СТАНДАРД           Да   75ден.
ЖЕЛЕ 200 ГР               46ден.   100 гр = 23.00 ден.   MILORD ЖЕЛЕ БОНБОНИ          Да   46ден.
```

**Caveat: KAM's data lags.** The file dated `2026/11/20` carried an internal snapshot
timestamp of `18.11.2026 5:52:12AM`. Roughly two days behind. Surface the publication date
in the UI rather than implying live prices.

**Caveat: naive text extraction interleaves the columns.** Use coordinate-based extraction
(word positions), not string splitting. In C#, **PdfPig** (Apache 2.0) is the natural choice.

**Opportunity:** the dated URL path is predictable, so `/2026/11/19/73.pdf` and earlier
likely resolve. If KAM retains old files, price history can be backfilled from day one.

### 4.3 Misa — simple GET, easiest to parse

Misa's price list is hosted on `proverkanaceni.mk`, which appears to be a **shared
compliance platform used by multiple smaller chains**. One adapter here may unlock further
retailers later at no extra cost.

```
GET https://mkbmisa.proverkanaceni.mk/?org={id}&search=&perPage={10|20|50|100}&page={n}
```

Plain HTML, no auth. A single `<table>` carrying the legally mandated set, identical in
meaning to KAM's PDF columns. **The header is two rows** (verified 2026-11-22):
`Промотивна или попустна цена` is a group heading (`colspan=3`) over the last three
columns, so there are **nine data columns**, not ten:

```
Назив на стока-производ | Продажна цена | Единечна цена | Опис на стока |
Достапност во продажен објект | Редовна цена |
  [Промотивна или попустна цена:] Цена со попуст | Вид на продажно поттикнување | Времетраење на промоција
```

A live promo row (`org=3`):

```
НИВЕА ГЕЛ ЗА БРИЧЕЊЕ 200МЛСЕНЗ.550302 | 299 ден. | 100ml = 149.50 ден. | ПЕНА,КРЕМ ЗА БРИЧЕЊЕ |
Да | 309 ден. | 299 ден. Попуст:3% | промотивна цена | 01.10.2026 до 31.12.2026
```

`Цена со попуст` holds **both** the promo price and the percentage in one cell.

Sample rows (`org=3`, Пазар Струга):

```
['МАЛБОРО ГОЛД МЕКО', '210 ден.', '', 'ЦИГАРИ', 'Да', '210 ден.']
['МАЛБОРО ТВРДО КС',  '220 ден.', '', 'ЦИГАРИ', 'Да', '220 ден.']
```

Notes for the adapter:

- Prices are strings with a `ден.` suffix — strip and parse.
- `Единечна цена` (unit price) is often **empty**. Do not assume it is present.
- Availability (`Да` / `Не`) **is** published, same as KAM.
- Trailing columns are omitted entirely on rows with no promotion, so rows are
  ragged. Index by position defensively, or map against the header row.
- **Pagination is `?page={n}`**, and it is large: `org=3` reported **5,667 items**
  ("Прикажи 1 - 100 од 5667 артикли"), 57 pages at `perPage=100`. This is a bigger
  catalogue than Kipper's 2,190. The paginator is windowed (`1 2 3 … 57 »`); the last
  page links no higher page. Past the end the table shows one "Нема податоци за
  прикажување" row.
- Unit price units are written either way: `100ml`, `100gr`, `100GR`.
- Names often carry a supplier code (`…СЕНЗ.550302`, `75 МЛ.550002`) and sometimes a size
  glued to an abbreviation (`200МЛСЕНЗ`).
- **The page's "Датум и време на последно ажурирање на цените" is not a publication time.**
  It showed the current minute on every load (21:42, then 21:43). Do not use it as
  `published_on` or as a change fingerprint.
- **No `ETag` or `Last-Modified`** (server: IIS). Change detection falls back to hashing
  the rows. `robots.txt` returns 404, so nothing is disallowed.

Nine stores:

| org | Store |
|---|---|
| 2 | Продавница 3 Охрид |
| 3 | Продавница 1 Пазар Струга |
| 4 | Продавница 2 Мислешево |
| 7 | Продавница Струга 2 ЈНА |
| 8 | Продавница Драслајца |
| 9 | Мега Маркет 9 |
| 11 | Охрид Видобиста |
| 12 | Маркет Миса 12 |
| 13 | Маркет ресен |

Store GPS coordinates are **not** in this feed and must be curated manually (nine stores,
one-off task).

### 4.4 Struga store roster

**Ten stores.** Note that KAM has **three** in Struga, not two: `Генц` does not carry
"Струга" in its name and is only findable via its `City` field. Filter KAM's store list on
`City`, never on `Name`. Kipper also has **three**, not two: store 118 was found in the
site's sitemap on 2026-11-22.

| Chain | Store | id | Lat, Lon | Weekday | Sunday |
|---|---|---|---|---|---|
| KAM | Генц · ул. Попово Блато бр.1 | 38 | 41.18688301123239, 20.675163482205495 | 08:30–21:00 | 09:00–21:00 |
| KAM | Струга - 3 · ул. ЈНА бр.1 | 64 | 41.17813888937535, 20.684157180582737 | 07:30–21:00 | 08:00–19:30 |
| KAM | Струга - 5 · ул. Маршал Тито бр. 108 | 69 | 41.17720096464695, 20.673502974602627 | 07:30–22:00 | 08:00–22:00 |
| Kipper | Кипер 131 · ул. Димче Ковачевски | 131 | 41.1754493, 20.6745462 | — | — |
| Kipper | Кипер 084 · ул. Маркс - Енгелс 34 | 084 | 41.1769906, 20.6828735 | — | — |
| Kipper | Кипер 118 · Дебарски пат | 118 | 41.1908408, 20.6767341 | — | — |
| Misa | Продавница 1 Пазар Струга | org=3 | **needs capture** | — | — |
| Misa | Продавница Струга 2 ЈНА | org=7 | **needs capture** | — | — |
| Misa | Продавница 2 Мислешево | org=4 | **needs capture** · outskirts | — | — |
| Misa | Продавница Драслајца | org=8 | **needs capture** · outskirts | — | — |

Two observations from the coordinates:

- **Kipper 131 and KAM Струга-5 are roughly 210 m apart.** A two-chain comparison is
  available on foot with almost no travel cost, which is a strong case for walking mode.
- **Sunday hours vary widely** (one closes 19:30, another runs to 22:00). The optimizer
  must respect per-store Sunday hours, not a single store-open assumption.

Kipper does not expose opening hours in a structured form; capture manually.

### 4.5 Prior art

[cenoteka.mk](https://cenoteka.mk/) and [pazarluk.mk](https://pazarluk.mk/) already do price
comparison and catalogue aggregation for North Macedonia. Neither appears to do
list-driven multi-store routing, which is the differentiator. Worth reviewing before
committing further.

---

## 5. Ingestion

### 5.1 Adapter contract

```csharp
public interface IPriceSourceAdapter
{
    string ChainCode { get; }
    Task<IReadOnlyList<StoreDescriptor>> DiscoverStoresAsync(CancellationToken ct);
    Task<SourceFingerprint> ProbeAsync(StoreDescriptor store, CancellationToken ct);
    Task<IReadOnlyList<RawPriceRecord>> FetchAsync(StoreDescriptor store, CancellationToken ct);
}
```

`RawPriceRecord` is the common shape every chain normalises into: source name (MK), source
name (alt), selling price, regular price, promo price, discount %, unit price, unit basis,
availability, category string, promo window, publication date.

### 5.2 Change detection

`ProbeAsync` must be cheap. Parse only when the fingerprint changes.

| Chain | Fingerprint |
|---|---|
| Kipper | "last price update" timestamp on the store page |
| KAM | `ShopFiles[0].RelativePath` + `DateUploaded` from the store-list JSON |
| Misa | `ETag` / `Last-Modified`, falling back to a hash of the response |

### 5.3 Schedule

- **Nightly at 04:00 local.** Per-chain cadence configurable from the admin page.
- Stagger the three chains; do not fire simultaneously. Add jitter.
- **Weekly forced full re-parse** regardless of fingerprint, as a safety net against a
  missed change signal or a parser regression.

Weekly-only crawling was considered and rejected: promotions carry explicit start and end
dates, so a seven-day-old snapshot will display promo prices that expired days earlier.
Because change detection makes a no-change night cost three HTTP requests, daily is
effectively free. More frequent than daily is pointless, since KAM's own data is already
two days stale at source.

### 5.4 Bulk write

Use Npgsql `BeginBinaryImport` (COPY) into a staging table, then set-based upsert into the
live tables. Row-by-row inserts will not keep up with 171-page PDFs across multiple stores.

### 5.5 Manual import (fallback, not routine)

The admin page must accept a CSV/JSON upload conforming to `RawPriceRecord` for a given
store and date. Purposes:

1. Bootstrap the matching pipeline before adapters are finished.
2. Degrade gracefully when an adapter breaks, instead of a chain going dark.
3. Onboard a future chain that publishes something unparseable.

**It must not become the routine path.** A recurring manual export gets done for two weeks
and then quietly stops, and stale prices are worse than no app.

---

## 6. Canonical catalogue and matching

This is the highest-risk component. If matching is 80% accurate, every savings figure the
app displays is wrong, and the product is worse than useless because it is confidently
wrong. Build and measure this before building much UI.

### 6.1 Two distinct concepts

| Concept | Meaning |
|---|---|
| `canonical_product` | Genuinely the same SKU. Zdravje 2.8% 1 L at Kipper and at KAM. |
| `equivalence_group` | Acceptable substitutes. "1 L whole milk, any brand." |

**List items resolve against `equivalence_group`.** KAM's private-label milk and Kipper's
private-label milk are never the same product but are substitutes, and brand-switching is
where most savings live.

### 6.2 Pipeline

**Stage 0 — Normalise (pure code, no AI).** Transliterate Cyrillic to Latin, extract brand
(usually the leading token), descriptor, size and unit basis. Normalise sizes to a base
unit. Deterministic, auditable, free.

**Stage 1 — Deterministic match.** Barcode join first if any chain exposes EAN (check
explicitly while building each adapter). Otherwise normalised key equality:
`AGRINA|POMFRIT 10X10MM|1000G`.

**Stage 2 — Blocked candidate generation.** Generate candidate pairs only within the same
size bucket and compatible category, using `pg_trgm` similarity or embeddings. Blocking is
what avoids the full O(n²) comparison.

**Stage 3 — LLM adjudication.** Only pairs above the similarity floor and below auto-accept.
Structured output:

```json
{ "same_product": true, "confidence": 0.94, "reason": "same brand, same size, spelling variant" }
```

Three non-negotiable rules:

1. **Code has veto over the model.** Size mismatch means not a match, regardless of what
   the model says. Size confusion is the single biggest source of wrong savings figures.
2. **Cache every verdict permanently**, keyed on the normalised string pair. Never re-ask.
3. **Use the Batch API.** Inherently async, runs at 50% of standard cost.

Default model `claude-opus-5`. Volume is too low for a cheaper tier to save meaningful
money, and accuracy determines whether the savings numbers are honest. If volume grows,
benchmark `claude-haiku-4-5` against a labelled sample rather than switching blind.

**Stage 4 — Human review queue.** Low-confidence verdicts surface in the admin page,
prioritised by how often the product appears in real user lists. Reviewing the 200 products
that appear in most baskets lifts perceived accuracy disproportionately.

**Stage 5 — Enrichment.** One batch job per new canonical product generating the English
alias and taxonomy mapping. This is what makes an English v1 work against MK/SQ source data.

### 6.3 Hard constraint

**No LLM call ever happens in the request path.** User search and price comparison hit
Postgres only. All AI runs inside the nightly ingestion job.

### 6.4 Validation

- Hand-label a sample of 200 pairs and measure precision/recall before building UI.
- Automated sanity check: if two matched products' unit prices differ by more than ~5×,
  flag it. That is almost always a size-parsing bug.

### 6.5 Cost estimate

≈7,000 unique raw product strings for Struga collapsing to 4,000–5,000 canonical products.
Stages 1–2 should resolve most. Single-digit dollars as a one-time cost, halved by the
Batch API. Nightly incremental cost is cents.

---

## 7. Recommendation engine

### 7.1 Inputs

User's resolved list, current location, radius, max stops, transport mode, fuel price,
vehicle consumption, value of time.

### 7.2 Algorithm

Candidate stores within radius is small (5–15 in Struga), and nobody accepts more than
about three stops. So:

1. Enumerate store subsets of size 1..`maxStops`.
2. For each subset, assign every list item to the cheapest store in the subset that stocks it.
3. Solve the route over ≤3 points (trivial TSP).
4. Score: `basket_total + fuel_cost + (extra_minutes × value_of_time)`.

Milliseconds. No solver library required. This lives in `Tally.Domain` as pure
computation with no I/O, making it fully unit-testable.

### 7.3 Distance model — no maps provider

The app does not use Google Maps, or any routing API. Users are residents of a small town
who already know how to get between two shops; what they need is the decision, not turn-by-turn
navigation.

**Store-to-store distances are hand-curated.** Ten stores is 45 pairs (90 ordered, since A→B and B→A can differ). Measure each once,
seed into `store_distance` (`from_store_id`, `to_store_id`, `metres`, `walk_seconds`,
`drive_seconds`), and expose it for editing in the admin page. Distances are static, so this
is a one-off task that is more accurate than any API would be.

This matters more in Struga than it would elsewhere: the Crni Drim splits the town where it
leaves Lake Ohrid and crossing points are limited, so two stores a few hundred metres apart
in a straight line can be a substantially longer walk if they sit on opposite banks.
Straight-line maths would silently recommend a bad route. Record which bank each store is on
when capturing coordinates.

**The origin leg uses Haversine** against the device's GPS position, multiplied by a
configurable detour factor (start at 1.3). Approximation is acceptable here because this leg
is common to every option being compared, so it rarely changes the ranking.

Travel time comes from configurable constants:

| Setting | Default |
|---|---|
| Walking speed | 4.8 km/h |
| Driving speed (town) | 25 km/h |
| Per-stop overhead | 12 min |
| Detour factor | 1.3 |

Store coordinates are still required, for the radius filter and the origin leg. Kipper and
KAM publish them; Misa's nine need manual entry (§4.3).

### 7.4 Output

Present the Pareto-relevant options rather than one number, each showing basket total, item
coverage, stop count, total time, fuel cost and net benefit. Include:

- the cheapest **complete** single-store basket
- the cheapest single-store basket even if **incomplete**, labelled with what is missing
- the best 2-stop option
- the best 3-stop option, with an explicit note when its marginal saving is trivial

The recommendation must state its reasoning in prose, with every input figure visible and
editable. An opaque score gets distrusted.

---

## 8. Mobile application

Screens are mocked, with annotations explaining each design decision, at:

**https://claude.ai/code/artifact/70cc02ad-e571-4534-b707-167f8a3a964c**

Summary:

| Screen | Purpose |
|---|---|
| Add to list | Free-text search, group row first, specific SKUs below |
| My list | Resolved items, cheapest price per row, promo flags |
| Options | The four comparison cards |
| Why this option | The full arithmetic, every figure editable |
| Route | Checkpoint line (you → stop 1 → stop 2) with per-stop checklist and opening hours |
| Item comparison | One product across all stores, unit price, publication date |
| Preferences | Transport, radius, max stops, time value, fuel, market opt-outs |

### Non-negotiable UI rules

- Every price displays its **publication date**. Never imply live shelf prices.
- Promo prices are **visually flagged** because they expire.
- Item coverage ("7 of 8") is always shown, never hidden.

---

## 9. Architecture

### 9.1 Layers

Onion, dependencies pointing inward, with **two hosts over one core**:

```
backend/src/
  Tally.Domain/          entities, value objects, optimizer, normalisation rules. Zero deps.
  Tally.Application/     use cases + port interfaces, organised by feature
  Tally.Infrastructure/  Postgres, crawler adapters, PDF parsing, Claude, Maps
  Tally.Api/             ASP.NET Core host → serves the mobile app
  Tally.Worker/          scheduled host → runs ingestion
```

Ingestion and the query API have genuinely different shapes: one is scheduled, long-running
and dependency-heavy; the other is latency-sensitive. A 171-page PDF parse must never be
able to affect API latency, and the API host should not carry PdfPig or an LLM client.

`Tally.Application` is organised **by feature, not by technical type**:
`Catalog/`, `Baskets/`, `Ingestion/`, `Stores/`. Not `Interfaces/`, `Services/`, `DTOs/`.

### 9.2 Persistence

**Dapper, raw SQL. No Entity Framework anywhere in the solution.**

- The basket query is set-based across canonical products × store prices. Hand-written SQL
  beats LINQ-to-Entities badly on this shape.
- Ingestion does bulk upserts of thousands of rows (COPY).
- Price history wants `DISTINCT ON`, window functions and partitioning, none of which LINQ
  expresses well.

**Admin authentication** uses `PasswordHasher<T>` from `Microsoft.AspNetCore.Identity` as a
standalone class (no EF dependency), an `admin_user` table, and cookie or JWT auth. Full
ASP.NET Core Identity would drag in eight tables and a migration system for one account.

### 9.3 Database migrations

Runner: **DbUp** or **Grate**. Plain `.sql` files applied in order and tracked in a version
table. No ORM involved.

```
db/
  migrations/
    2026-11/
      20261120_1430_create_schema.sql
      20261120_1505_create_canonical_product.sql
    2026-12/
  seed/
    reference/     chains, stores, taxonomy. Idempotent, safe to re-run.
    dev/           local-only fixtures
  programmable/    views and functions. CREATE OR REPLACE, re-run every deploy.
```

Month-year folders are for human browsing. Timestamped filenames sort correctly regardless
of folder. The distinction that matters: **migrations are versioned and run once;
views and functions are idempotent and re-run on every deploy.** Mixing them is what makes
schema folders rot.

### 9.4 Mobile structure

```
frontend/src/
  ui/                    design system: Button, Card, Text, Sheet. App-agnostic.
  features/
    list/                components / hooks / screens, colocated
    recommendations/
    stores/
  api/                   generated from contracts/openapi.json
  navigation/
  theme/
```

The test for `ui/`: could this component be lifted into a separate library unmodified? If it
knows what a basket is, it belongs in `features/`.

### 9.5 Repository

```
tally/
  backend/      src/ + tests/
  frontend/
  db/           migrations/ seed/ programmable/
  contracts/    openapi.json → generated TS client
  CLAUDE.md     + backend/CLAUDE.md + frontend/CLAUDE.md
```

Monorepo so that a single session, human or agent, can change a C# endpoint and the RN call
site together, and so the API contract is versioned atomically with both sides.

---

## 10. Open questions

**Blocking nothing, but each changes the UI or schema:**

1. **Substitutions.** If a store lacks avocado, does the app silently offer another brand or
   always ask? Mockups assume ask.
2. **Promo expiry warnings.** A promo ending tonight changes tomorrow's recommendation.
   Warning badge on the route screen?
3. **Default time value.** 300 den/hour is a guess. Possibly ship with time cost off.
4. **Walking radius.** Should switching to walking auto-shrink the radius?

**Technical:**

5. Worker as a hosted service with Quartz in-process, or a separate scheduled container?
6. OpenAPI spec generated at build time, or committed to `contracts/`?
7. Does any chain expose EAN/barcode? Check while building each adapter. Even one would
   anchor the whole matching pipeline.
8. Does KAM retain historical PDFs at dated URLs? If so, backfill price history at launch.

---

## 11. Risk register

| Risk | Severity | Mitigation |
|---|---|---|
| Cross-chain product matching inaccurate | **Critical** | Hand-labelled validation set before UI work; human review queue; size as hard constraint |
| Published price ≠ shelf price | High | Show publication date; KAM availability flag; modest promises |
| Adapter breakage | Medium | Change-detection alerts; manual import fallback; weekly forced re-parse |
| Curated distances drift as streets change | Low | 45 pairs, admin-editable; re-check annually |
| Prior art competition | Low | Differentiator is list-driven routing, which neither incumbent does |

---

## 12. Suggested build order

1. Database schema + migration runner
2. Misa adapter (simplest) → validates `RawPriceRecord` shape end to end
3. Kipper adapter (JSON)
4. Normalisation + deterministic matching, measured against a hand-labelled set
5. KAM adapter (PDF extraction)
6. LLM adjudication + enrichment
7. Query API + OpenAPI contract
8. Optimizer (pure domain, fully testable in isolation)
9. Mobile app
10. Admin page

Steps 1–4 are the real risk. If matching quality cannot be made good enough, that is the
point to find out, before any UI exists.
