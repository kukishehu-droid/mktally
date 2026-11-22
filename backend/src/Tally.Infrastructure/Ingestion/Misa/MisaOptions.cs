namespace Tally.Infrastructure.Ingestion.Misa;

public sealed class MisaOptions
{
    public const string SectionName = "Crawlers:Misa";

    public Uri BaseUrl { get; set; } = new("https://mkbmisa.proverkanaceni.mk/");

    /// <summary>REQUIREMENTS.md §4.3: largest supported page size, ~57 pages per store.</summary>
    public int PerPage { get; set; } = 100;

    /// <summary>Safety cap. Hitting it fails the run rather than ingesting a truncated catalogue.</summary>
    public int MaxPages { get; set; } = 150;

    /// <summary>Pause between page requests. Politeness, not rate-limit avoidance.</summary>
    public TimeSpan PageDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan PageDelayJitter { get; set; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Store roster override. Left empty, <see cref="MisaStoreRoster.All"/> is used.
    /// (Defaults are not put here because configuration binding appends to a pre-filled list.)
    /// </summary>
    public List<MisaStoreOptions> Stores { get; set; } = [];
}

public sealed class MisaStoreOptions
{
    public string Org { get; set; } = "";
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string? Municipality { get; set; }
}

/// <summary>
/// Misa's feed has no store list, so the roster is curated. REQUIREMENTS.md §4.3.
/// Coordinates and hours are curated in the database, not here.
/// </summary>
public static class MisaStoreRoster
{
    public static IReadOnlyList<MisaStoreOptions> All { get; } =
    [
        new() { Org = "2",  Name = "Продавница 3 Охрид",        City = "Охрид",     Municipality = "Охрид" },
        new() { Org = "3",  Name = "Продавница 1 Пазар Струга", City = "Струга",    Municipality = "Струга" },
        new() { Org = "4",  Name = "Продавница 2 Мислешево",    City = "Мислешево", Municipality = "Струга" },
        new() { Org = "7",  Name = "Продавница Струга 2 ЈНА",   City = "Струга",    Municipality = "Струга" },
        new() { Org = "8",  Name = "Продавница Драслајца",      City = "Драслајца", Municipality = "Струга" },
        new() { Org = "9",  Name = "Мега Маркет 9" },            // location unconfirmed
        new() { Org = "11", Name = "Охрид Видобиста",           City = "Охрид",     Municipality = "Охрид" },
        new() { Org = "12", Name = "Маркет Миса 12" },           // location unconfirmed
        new() { Org = "13", Name = "Маркет ресен",              City = "Ресен",     Municipality = "Ресен" },
    ];
}
