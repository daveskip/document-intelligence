using DocumentIntelligence.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocumentIntelligence.Infrastructure.Tests;

/// <summary>
/// AppDbContext variant for SQLite-based tests.
/// Adds DateTimeOffset → long conversions because SQLite has no native DateTimeOffset support.
/// </summary>
internal sealed class SqliteAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}
