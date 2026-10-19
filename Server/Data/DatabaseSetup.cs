using Microsoft.EntityFrameworkCore;

namespace Server.Data;

/// <summary>
/// Creates/updates the schema and seeds reference data at startup, for either database:
///  - PostgreSQL: EF Core migrations when the project has them (dotnet ef migrations add ...),
///    otherwise EnsureCreated() builds the schema from the model on an empty database.
///  - SQLite (legacy): the original hand-written repair SQL (<see cref="LegacySqliteSchema"/>).
/// Seed data is written with EF so it's identical on both.
/// </summary>
public static class DatabaseSetup
{
    public static bool IsPostgres(string connectionString)
        => connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
           || connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase) && connectionString.Contains("Username=", StringComparison.OrdinalIgnoreCase)
           || connectionString.StartsWith("postgres", StringComparison.OrdinalIgnoreCase);

    /// <summary>File path from a SQLite connection string ("Data Source=app.db").</summary>
    public static string SqliteDataSource(string connectionString)
        => connectionString.Split(';')
               .Select(part => part.Split('=', 2))
               .Where(kv => kv.Length == 2 && (kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                                              || kv[0].Trim().Equals("DataSource", StringComparison.OrdinalIgnoreCase)))
               .Select(kv => kv[1].Trim())
               .FirstOrDefault() ?? "app.db";

    public static void Initialize(IServiceProvider services, IConfiguration configuration, bool hangfireIsPersistent)
    {
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.Database.IsNpgsql())
            {
                if (db.Database.GetMigrations().Any())
                {
                    db.Database.Migrate();
                }
                else
                {
                    db.Database.EnsureCreated();          // empty database: full schema from the model
                    PostgresSchemaUpgrades.Apply(db);     // existing database: columns added since it was created
                }
            }
            else if (db.Database.IsSqlite())
            {
                LegacySqliteSchema.Repair(services, configuration, hangfireIsPersistent);
            }
            else
            {
                db.Database.EnsureCreated(); // e.g. tests
            }
        }

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SeedData.Ensure(db, configuration, hangfireIsPersistent);
        }
    }
}
