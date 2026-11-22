// Applies db/ to a PostgreSQL database. REQUIREMENTS.md §9.3.
//
//   dotnet run --project backend/tools/Tally.Migrator -- [--create] [--dev] [--db-root <path>]
//
// Connection string comes from the TALLY_DB environment variable.
//
// Stages, in order:
//   1. migrations/       journaled in public.schemaversions, each runs exactly once
//   2. programmable/     views and functions, CREATE OR REPLACE, re-run every time
//   3. seed/reference/   idempotent reference data, re-run every time
//   4. seed/dev/         only with --dev
//
// Script names are file names only (not folder paths), so the month folders are purely
// for browsing and ordering comes from the timestamp prefix.

using DbUp;
using DbUp.Builder;
using DbUp.Helpers;
using DbUp.ScriptProviders;

var connectionString = Environment.GetEnvironmentVariable("TALLY_DB");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set TALLY_DB to a PostgreSQL connection string.");
    return 2;
}

var create = args.Contains("--create");
var dev = args.Contains("--dev");
var dbRoot = OptionValue(args, "--db-root") ?? FindDbRoot();
if (dbRoot is null)
{
    Console.Error.WriteLine("Could not find the db/ folder. Pass --db-root <path>.");
    return 2;
}

if (create)
{
    EnsureDatabase.For.PostgresqlDatabase(connectionString);
}

var stages = new List<(string Name, string Folder, bool Journaled)>
{
    ("migrations", Path.Combine(dbRoot, "migrations"), true),
    ("programmable", Path.Combine(dbRoot, "programmable"), false),
    ("seed/reference", Path.Combine(dbRoot, "seed", "reference"), false),
};
if (dev)
{
    stages.Add(("seed/dev", Path.Combine(dbRoot, "seed", "dev"), false));
}

foreach (var (name, folder, journaled) in stages)
{
    if (!Directory.Exists(folder))
    {
        Console.WriteLine($"[{name}] folder not found, skipping: {folder}");
        continue;
    }

    Console.WriteLine($"[{name}] applying from {folder}");

    UpgradeEngineBuilder builder = DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsFromFileSystem(folder, new FileSystemScriptOptions
        {
            IncludeSubDirectories = true,
            UseOnlyFilenameForScriptName = true,
        })
        .WithTransactionPerScript()
        // Scripts use $$ dollar quoting; never let DbUp treat $...$ as a variable.
        .WithVariablesDisabled()
        .LogToConsole();

    builder = journaled
        ? builder.JournalToPostgresqlTable("public", "schemaversions")
        : builder.JournalTo(new NullJournal());

    var result = builder.Build().PerformUpgrade();
    if (!result.Successful)
    {
        Console.Error.WriteLine($"[{name}] FAILED in {result.ErrorScript?.Name}: {result.Error}");
        return 1;
    }
}

Console.WriteLine("Database is up to date.");
return 0;

static string? OptionValue(string[] args, string option)
{
    var i = Array.IndexOf(args, option);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Walk up from the working directory until a folder containing db/migrations is found,
// so the tool works from the repo root or from inside backend/.
static string? FindDbRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "db");
        if (Directory.Exists(Path.Combine(candidate, "migrations")))
        {
            return candidate;
        }
    }
    return null;
}
