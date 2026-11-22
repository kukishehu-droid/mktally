-- pg_trgm backs product_alias search (DATA-MODEL.md §3, §9).
-- Must exist before any gin_trgm_ops index is created.
create extension if not exists pg_trgm;
