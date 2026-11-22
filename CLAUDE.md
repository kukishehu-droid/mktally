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

## Status

Pre-implementation. Suggested build order is `REQUIREMENTS.md` §12. Steps 1–4 (schema,
two adapters, matching) carry the real risk and should be validated before UI work starts.
