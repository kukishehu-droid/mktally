namespace Tally.Application.Ingestion;

/// <summary>
/// Opaque change marker for one store's price list. Compared by <see cref="Value"/> only.
/// REQUIREMENTS.md §5.2.
/// </summary>
/// <param name="Value">Prefixed by kind, e.g. <c>etag:</c>, <c>last-modified:</c>, <c>sha256:</c>.</param>
/// <param name="PublishedOn">The chain's own publication date when the probe can see it.</param>
public sealed record SourceFingerprint(string Value, DateOnly? PublishedOn = null);
