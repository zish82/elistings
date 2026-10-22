using Microsoft.EntityFrameworkCore;

namespace Server.Data;

/// <summary>
/// One-off copy of all app data from the old SQLite file into an empty PostgreSQL database, keeping ids
/// (so links between listings, products, eBay accounts etc. stay intact).
///
///   1. Set ConnectionStrings:DefaultConnection to the Postgres database (e.g. Host=localhost;Database=elistings;Username=...;Password=...).
///   2. dotnet run --project Server -- --copy-sqlite-to-postgres Server/app.db
///   3. Start the app normally.
///
/// Run the app at least once on SQLite with the current version first, so the SQLite file has every column.
/// Hangfire's own job history is not copied (schedules are recreated on startup).
/// </summary>
public static class SqliteToPostgresCopier
{
    private const int BatchSize = 1000;

    public static async Task<int> RunAsync(string sqliteFile, string postgresConnectionString, bool force)
    {
        if (!DatabaseSetup.IsPostgres(postgresConnectionString))
        {
            Console.WriteLine("ConnectionStrings:DefaultConnection must point at PostgreSQL (Host=...) to copy into.");
            return 1;
        }
        if (!File.Exists(sqliteFile))
        {
            Console.WriteLine($"SQLite file not found: {Path.GetFullPath(sqliteFile)}");
            return 1;
        }

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        await using var source = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqliteFile}").Options);
        await using var target = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(postgresConnectionString).Options);

        Console.WriteLine("Creating the PostgreSQL schema (if needed)...");
        if (target.Database.GetMigrations().Any()) await target.Database.MigrateAsync();
        else
        {
            await target.Database.EnsureCreatedAsync();
            PostgresSchemaUpgrades.Apply(target);
        }

        if (!force && (await target.Users.AnyAsync() || await target.Listings.AnyAsync() || await target.SupplierProducts.AnyAsync()))
        {
            Console.WriteLine("The PostgreSQL database already has data. Re-run with --force to copy anyway (rows with the same ids will fail).");
            return 1;
        }

        // All or nothing: a failed copy leaves the PostgreSQL database as it was, so it can simply be re-run.
        await using var transaction = await target.Database.BeginTransactionAsync();

        // Starting the app on PostgreSQL seeds reference data (suppliers, delivery options, feeds, policies, fee settings).
        // Clear it so the copied rows (with their original ids and any edits made on /suppliers) take its place.
        await target.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"SupplierDeliveryOptions\", \"SupplierFeeds\", \"Policies\", \"Suppliers\", \"MarketplaceFeeSettings\", \"MarketplaceFeeCategoryRates\" RESTART IDENTITY;");

        target.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await CopyAllAsync(source, target);
            await transaction.CommitAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine($"The SQLite file is missing newer tables/columns ({ex.Message}). Start the app once on SQLite " +
                              "with this version (it upgrades the file), stop it, then run the copy again. Nothing was copied.");
            return 1;
        }

        Console.WriteLine("Done. Start the app normally; it now uses PostgreSQL.");
        return 0;
    }

    private static async Task CopyAllAsync(AppDbContext source, AppDbContext target)
    {
        await CopyAsync(source.Users, u => u.Id, target, "Users");
        await CopyAsync(source.EbayTokens, t => t.Id, target, "EbayTokens");
        await CopyAsync(source.Policies, p => p.Id, target, "Policies");
        await CopyAsync(source.Suppliers, s => s.Id, target, "Suppliers");
        await CopyAsync(source.SupplierDeliveryOptions, o => o.Id, target, "SupplierDeliveryOptions");
        await CopyAsync(source.SupplierFeeds, f => f.Id, target, "SupplierFeeds");
        await CopyAsync(source.SupplierProducts, p => p.Id, target, "SupplierProducts");
        await CopyAsync(source.Listings, l => l.Id, target, "Listings");
        await CopyAsync(source.SupplierSyncRuns, r => r.Id, target, "SupplierSyncRuns");
        await CopyAsync(source.SupplierListingSnapshots, s => s.Id, target, "SupplierListingSnapshots");
        await CopyAsync(source.BulkJobs, j => j.Id, target, "BulkJobs", hasIdentity: false);
        await CopyAsync(source.SupplierPriceListItems, i => i.Id, target, "SupplierPriceListItems");
        await CopyAsync(source.MarketplaceFeeSettings, s => s.Id, target, "MarketplaceFeeSettings");
        await CopyAsync(source.MarketplaceFeeCategoryRates, r => r.Id, target, "MarketplaceFeeCategoryRates");
        await CopyAsync(source.EbayCategories, c => c.Id, target, "EbayCategories");
        await CopyAsync(source.EbayOrders, o => o.Id, target, "EbayOrders");
        await CopyAsync(source.UserSupplierAccess, a => a.Id, target, "UserSupplierAccess");
        await CopyAsync(source.EbayOrderLines, l => l.Id, target, "EbayOrderLines");
        await CopyAsync(source.ProductTags, t => t.Id, target, "ProductTags");
        await CopyAsync(source.SupplierProductTags, t => t.Id, target, "SupplierProductTags");
    }

    private static async Task CopyAsync<T, TKey>(DbSet<T> sourceSet, System.Linq.Expressions.Expression<Func<T, TKey>> key,
        AppDbContext target, string table, bool hasIdentity = true) where T : class
    {
        var total = await sourceSet.CountAsync();
        Console.Write($"Copying {table} ({total})... ");

        // Read in id order in pages so large tables (SupplierProducts) don't need to fit in memory at once.
        var copied = 0;
        while (copied < total)
        {
            var batch = await sourceSet.AsNoTracking()
                .OrderBy(key)
                .Skip(copied).Take(BatchSize)
                .ToListAsync();
            if (batch.Count == 0) break;

            target.Set<T>().AddRange(batch);
            await target.SaveChangesAsync();
            target.ChangeTracker.Clear();
            copied += batch.Count;
        }

        if (hasIdentity)
        {
            // Explicit ids were inserted, so move the identity sequence past the highest one.
            await target.Database.ExecuteSqlRawAsync(
                $"SELECT setval(pg_get_serial_sequence('\"{table}\"', 'Id'), COALESCE((SELECT MAX(\"Id\") FROM \"{table}\"), 1), (SELECT COUNT(*) FROM \"{table}\") > 0);");
        }

        Console.WriteLine("ok");
    }
}
