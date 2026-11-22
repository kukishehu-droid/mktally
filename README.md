# Tally

Grocery price comparison for Struga, North Macedonia. Users build a shopping list;
Tally shows which shop, or pair of shops, is cheapest once travel time and fuel are counted.

Covers the three chains with stores in Struga: Kipper, KAM and Misa (10 stores).

## TallyBot

User-Agent: `TallyBot/0.1 (+https://github.com/kukishehu-droid/mktally)`

TallyBot reads the per-store price lists that North Macedonian retailers publish under the
Ministry of Economy and Labour rulebook (in force since 18 April), which requires daily
per-store prices accessible without login.

- Runs once a night, around 04:00 local time, with pauses between requests
- Checks first whether a price list has changed, and downloads it only if it has
- Respects robots.txt (`User-agent: TallyBot`)
- Reads prices only; never logs in, submits forms or collects personal data
- Every price Tally shows carries the date the retailer published it

To ask questions or have your site excluded, open an issue in this repository.

## Repository layout

```
backend/      C# / ASP.NET Core (.NET 9), onion architecture. See backend/CLAUDE.md
frontend/     React Native app. See frontend/CLAUDE.md
db/           PostgreSQL migrations, reference seed, views and functions
contracts/    openapi.json, the generated API contract
```

Product and data-source details: [REQUIREMENTS.md](REQUIREMENTS.md).
Schema and API surface: [DATA-MODEL.md](DATA-MODEL.md).

## Getting started

Requirements: .NET 9 SDK, PostgreSQL 16+.

```
cd backend
dotnet build
dotnet test
```

Apply the database (creates it if missing):

```
set TALLY_DB=Host=localhost;Database=tally;Username=postgres;Password=...
dotnet run --project tools/Tally.Migrator -- --create
```

The migrator runs `db/migrations` once each (tracked in `schemaversions`), then re-runs
`db/programmable` and `db/seed/reference` every time; both are idempotent.

## Status

Pre-release. Done: schema, Misa and Kipper adapters (verified against the live sites),
product-name normalisation. Next: KAM adapter (PDF), product matching, API, optimizer, app.
