# Backend

C# / ASP.NET Core, PostgreSQL. Onion architecture, two hosts over one shared core.

```
src/
  Tally.Domain/          entities, value objects, optimizer, normalisation. Zero dependencies.
  Tally.Application/     use cases + port interfaces
  Tally.Infrastructure/  Postgres, crawler adapters, PDF parsing, Claude, Maps
  Tally.Api/             ASP.NET Core host → serves the mobile app
  Tally.Worker/          scheduled host → runs ingestion
```

Dependencies point inward. `Domain` references nothing. `Api` and `Worker` are separate
deployables on purpose: a 171-page PDF parse must never affect API latency, and the API
host should not carry PdfPig or an LLM client.

## Do not use Entity Framework

**No EF anywhere in the solution.** Use **Dapper** with hand-written SQL.

- The basket query is set-based across canonical products × store prices. Raw SQL beats
  LINQ-to-Entities badly on this shape.
- Ingestion does bulk upserts of thousands of rows: Npgsql `BeginBinaryImport` (COPY) into
  a staging table, then a set-based upsert. Never row-by-row.
- Price history uses `DISTINCT ON`, window functions and partitioning. LINQ expresses none
  of these well.

Admin auth uses `PasswordHasher<T>` from `Microsoft.AspNetCore.Identity` **as a standalone
class** (it has no EF dependency), an `admin_user` table, and cookie or JWT auth. Do not
pull in the full Identity stack for one account.

## Organise Application by feature

`Catalog/`, `Baskets/`, `Ingestion/`, `Stores/`.

Not `Interfaces/`, `Services/`, `DTOs/`. Technical-type folders are the standard way Onion
projects rot.

## SQL

Queries live next to their repository as `.sql` embedded resources, not as inline strings
scattered through C#. Keep them readable and formatted.

Migrations are plain `.sql` run by DbUp or Grate. See `db/` for the folder convention.
The distinction that matters: **migrations are versioned and run once; views and functions
are idempotent and re-run every deploy.**

## Crawler adapters

Implement `IPriceSourceAdapter` (contract in `REQUIREMENTS.md` §5.1). Every chain normalises
into `RawPriceRecord`.

`ProbeAsync` must be cheap, and parsing happens only when the fingerprint changes. A
no-change night should cost three HTTP requests total.

KAM's PDFs need **coordinate-based** extraction — naive text extraction interleaves the
columns. Use **PdfPig**.

## Product matching: three hard rules

1. **Code has veto over the model.** Size mismatch means not a match, regardless of what the
   LLM says. Size confusion (1 L vs 1.5 L) is the biggest source of wrong savings figures.
   Size is a hard blocking constraint, never a soft similarity signal.
2. **Cache every adjudication verdict permanently**, keyed on the normalised string pair.
   Never re-ask the model about a pair it has already seen.
3. **No LLM call in the request path, ever.** User search and price comparison hit Postgres
   only. All AI runs inside the nightly ingestion job.

## Calling Claude

Model `claude-opus-5` (exact string, never append a date suffix). Use the official
`Anthropic` SDK, never raw HTTP. Adjudication and enrichment are inherently async, so use
the **Batch API** — it runs at 50% of standard cost.

Use `thinking: { type: "adaptive" }`. `budget_tokens` is rejected with a 400 on this model.

## Testing

The optimizer in `Tally.Domain` is pure computation with no I/O. It should have thorough
unit tests covering partial baskets, single-store, multi-store, and the walking-mode case
where fuel cost drops out.

Matching quality needs a hand-labelled set of ~200 pairs with measured precision and recall,
not spot checks.
