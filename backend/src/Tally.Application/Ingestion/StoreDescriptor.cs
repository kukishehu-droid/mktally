namespace Tally.Application.Ingestion;

/// <summary>
/// A store as the chain describes it. Maps onto the <c>store</c> table, keyed by
/// (<see cref="ChainCode"/>, <see cref="ExternalId"/>). Fields the chain does not
/// publish are null and are curated by hand in the admin page.
/// </summary>
public sealed record StoreDescriptor
{
    public required string ChainCode { get; init; }

    /// <summary>KAM <c>Id</c>, Misa <c>org</c>, Kipper slug.</summary>
    public required string ExternalId { get; init; }

    public required string Name { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }
    public string? Municipality { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? WorkingHours { get; init; }
    public string? SundayWorkingHours { get; init; }
    public string? Phone { get; init; }
}
