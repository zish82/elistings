using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hangfire.MemoryStorage;
using Server.Data;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddHangfire(configuration => configuration
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseMemoryStorage());
builder.Services.AddHangfireServer();

var sqliteConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=app.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(sqliteConnectionString));

builder.Services.Configure<Server.Configuration.EbaySettings>(
    builder.Configuration.GetSection("EbaySettings"));
builder.Services.Configure<Server.Configuration.EdenSupplierSettings>(
    builder.Configuration.GetSection("EdenSupplier"));
builder.Services.Configure<Server.Configuration.DropShipMallSettings>(
    builder.Configuration.GetSection("DropShipMall"));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "elistings.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<Server.Services.IPasswordHasher, Server.Services.PasswordHasher>();
builder.Services.AddScoped<Server.Services.BackgroundUserContext>();
builder.Services.AddScoped<Server.Services.ICurrentUserService, Server.Services.CurrentUserService>();
builder.Services.AddSingleton<Server.Services.BulkJobRunner>();
builder.Services.AddScoped<Server.Services.SupplierCatalogService>();
// Per-supplier integrations. Onboarding a supplier = add its enricher / stock checker here.
builder.Services.AddHttpClient<Server.Services.DropShipMallWebsiteClient>();
builder.Services.AddScoped<Server.Services.ISupplierProductEnricher, Server.Services.DropShipMallProductEnricher>();
builder.Services.AddScoped<Server.Services.ProductEnrichmentService>();
builder.Services.AddScoped<Server.Services.ISupplierStockChecker, Server.Services.DropShipMallStockChecker>();
builder.Services.AddScoped<Server.Services.ISupplierStockChecker, Server.Services.WebsiteStockCheckerAdapter>(); // Eden, Safelincs
builder.Services.AddScoped<Server.Services.SupplierStockService>();
builder.Services.AddScoped<Server.Services.SkuService>();

builder.Services.AddHttpClient<Server.Services.IEbayService, Server.Services.EbayService>();
builder.Services.AddHttpClient<Server.Services.IMarketplaceImageService, Server.Services.EbayImageService>();
builder.Services.AddScoped<Server.Services.IEdenSupplierSyncService, Server.Services.EdenSupplierSyncService>();
builder.Services.AddScoped<Server.Services.IFeedProductSyncService, Server.Services.DropShipMallFeedSyncService>();
builder.Services.AddHttpClient("eden-auth")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        UseCookies = true,
        CookieContainer = new CookieContainer()
    });
builder.Services.AddSingleton<Server.Services.IProductScraper, Server.Services.SafelincsProductScraper>();
builder.Services.AddSingleton<Server.Services.IProductScraper, Server.Services.EdenHorticultureProductScraper>();
builder.Services.AddSingleton<Server.Services.IProductScraper, Server.Services.GenericProductScraper>();
builder.Services.AddHttpClient<Server.Services.IScraperService, Server.Services.ScraperService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });

builder.Services.AddCors(options =>
{
    options.AddPolicy("ClientCors", policy =>
        policy.WithOrigins(
                "http://localhost:5046",
                "https://localhost:7099",
                "http://localhost:5197",
                "https://localhost:7138")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseForwardedHeaders(forwardedHeadersOptions);

// Some browsers request /favicon.ico even when index.html specifies a PNG favicon.
app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.png"));

app.UseCors("ClientCors");

app.UseHttpsRedirection();

// Enable routing middleware so endpoint routing can execute mapped endpoints
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseHangfireDashboard("/hangfire");
}

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapFallbackToFile("index.html");
});

// Auto-migrate database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    
    // Ensure EbayTokens table exists (fix for existing databases)
    try 
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Users"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Users"" PRIMARY KEY AUTOINCREMENT,
                ""Email"" TEXT NOT NULL,
                ""PasswordHash"" TEXT NOT NULL,
                ""PasswordSalt"" TEXT NOT NULL,
                ""Role"" TEXT NOT NULL,
                ""IsActive"" INTEGER NOT NULL DEFAULT 1,
                ""CreatedAt"" TEXT NOT NULL
            );
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Users_Email"" ON ""Users"" (""Email"");
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""EbayTokens"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_EbayTokens"" PRIMARY KEY AUTOINCREMENT,
                ""UserId"" INTEGER NOT NULL DEFAULT 0,
                ""Name"" TEXT NOT NULL DEFAULT 'eBay Account',
                ""IsDefault"" INTEGER NOT NULL DEFAULT 0,
                ""AccessToken"" TEXT NOT NULL,
                ""RefreshToken"" TEXT NOT NULL,
                ""ExpiryTime"" TEXT NOT NULL,
                ""RefreshTokenExpiryTime"" TEXT NOT NULL
            );
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_EbayTokens_UserId"" ON ""EbayTokens"" (""UserId"");
        ");

        // Ensure EbayTokens table has account metadata for multi-account per-user storage.
        var ebayTokenColumns = new List<string>();
        var conn2 = db.Database.GetDbConnection();
        if (conn2.State != System.Data.ConnectionState.Open) conn2.Open();
        using (var cmd = conn2.CreateCommand())
        {
            cmd.CommandText = @"PRAGMA table_info(""EbayTokens"");";
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    ebayTokenColumns.Add(reader["name"].ToString() ?? "");
                }
            }

            if (!ebayTokenColumns.Any(c => c.Equals("UserId", StringComparison.OrdinalIgnoreCase)))
            {
                cmd.CommandText = @"ALTER TABLE ""EbayTokens"" ADD COLUMN ""UserId"" INTEGER NOT NULL DEFAULT 0;";
                cmd.ExecuteNonQuery();
            }

            if (!ebayTokenColumns.Any(c => c.Equals("Name", StringComparison.OrdinalIgnoreCase)))
            {
                cmd.CommandText = @"ALTER TABLE ""EbayTokens"" ADD COLUMN ""Name"" TEXT NOT NULL DEFAULT 'eBay Account';";
                cmd.ExecuteNonQuery();
            }

            if (!ebayTokenColumns.Any(c => c.Equals("IsDefault", StringComparison.OrdinalIgnoreCase)))
            {
                cmd.CommandText = @"ALTER TABLE ""EbayTokens"" ADD COLUMN ""IsDefault"" INTEGER NOT NULL DEFAULT 0;";
                cmd.ExecuteNonQuery();
            }
        }

        // Backfill legacy single-token setups to the first user when exactly one user exists.
        db.Database.ExecuteSqlRaw(@"
            UPDATE ""EbayTokens""
            SET ""Name"" = 'Default eBay Account'
            WHERE COALESCE(TRIM(""Name""), '') = '';
        ");

        db.Database.ExecuteSqlRaw(@"
            UPDATE ""EbayTokens""
            SET ""UserId"" = (SELECT ""Id"" FROM ""Users"" ORDER BY ""Id"" LIMIT 1)
            WHERE ""UserId"" = 0
              AND 1 = (SELECT COUNT(*) FROM ""Users"");
        ");

        db.Database.ExecuteSqlRaw(@"
            UPDATE ""EbayTokens""
            SET ""IsDefault"" = 1
            WHERE ""Id"" IN (
                SELECT et.""Id""
                FROM ""EbayTokens"" et
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""EbayTokens"" other
                    WHERE other.""UserId"" = et.""UserId"" AND other.""IsDefault"" = 1
                )
            );
        ");

        // Ensure Listings exists before repairing columns on databases created by older schema versions.
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Listings"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Listings"" PRIMARY KEY AUTOINCREMENT,
                ""OwnerUserId"" INTEGER NOT NULL DEFAULT 0,
                ""EbayAccountId"" INTEGER NULL,
                ""SupplierProductId"" INTEGER NULL,
                ""Title"" TEXT NOT NULL DEFAULT '',
                ""Description"" TEXT NOT NULL DEFAULT '',
                ""Price"" REAL NOT NULL DEFAULT 0.99,
                ""Quantity"" INTEGER NOT NULL DEFAULT 1,
                ""Status"" TEXT NOT NULL DEFAULT 'Draft',
                ""EbayItemId"" TEXT NULL,
                ""CategoryId"" TEXT NULL,
                ""CategoryName"" TEXT NULL,
                ""FulfillmentPolicyId"" TEXT NULL,
                ""PaymentPolicyId"" TEXT NULL,
                ""ReturnPolicyId"" TEXT NULL,
                ""Type"" TEXT NULL,
                ""Brand"" TEXT NULL,
                ""Colour"" TEXT NULL,
                ""Sku"" TEXT NULL,
                ""EbayOfferId"" TEXT NULL,
                ""ImageUrlsJson"" TEXT NULL,
                ""SourceUrl"" TEXT NULL,
                ""SourcePrice"" REAL NULL,
                ""SourceStockLevel"" INTEGER NULL,
                ""SourceProductCode"" TEXT NULL,
                ""CreatedAt"" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
        ");

        // Ensure Listings table has required columns (completely silent raw SQL)
        var requiredColumns = new[] { "OwnerUserId", "EbayAccountId", "SupplierProductId", "Type", "Brand", "Colour", "ImageUrlsJson", "Sku", "EbayOfferId", "SourceUrl", "SourcePrice", "SourceStockLevel", "SourceProductCode", "CategoryName", "Quantity" };
        var existingColumns = new List<string>();
        
        try 
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            
            using (var cmd = conn.CreateCommand())
            {
                // Find table name
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Listings';";
                var actualTableName = cmd.ExecuteScalar()?.ToString() ?? "Listings";
                Console.WriteLine($"[Migration] Checking table: {actualTableName}");

                cmd.CommandText = $@"PRAGMA table_info(""{actualTableName}"");";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        existingColumns.Add(reader["name"].ToString() ?? "");
                    }
                }

                foreach (var col in requiredColumns)
                {
                    if (!existingColumns.Any(c => c.Equals(col, StringComparison.OrdinalIgnoreCase)))
                    {
                        try 
                        {
                            Console.WriteLine($"[Migration] Adding missing column: {col} to {actualTableName}");
                            if (string.Equals(col, "SourcePrice", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" REAL NULL;";
                            }
                            else if (string.Equals(col, "EbayAccountId", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" INTEGER NULL;";
                            }
                            else if (string.Equals(col, "SupplierProductId", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" INTEGER NULL;";
                            }
                            else if (string.Equals(col, "OwnerUserId", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" INTEGER NOT NULL DEFAULT 0;";
                            }
                            else if (string.Equals(col, "Quantity", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" INTEGER NOT NULL DEFAULT 1;";
                            }
                            else if (string.Equals(col, "SourceStockLevel", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" INTEGER NULL;";
                            }
                            else
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" TEXT NULL;";
                            }
                            cmd.ExecuteNonQuery();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Migration] Failed to add column {col}: {ex.Message}");
                        }
                    }
                    else 
                    {
                        Console.WriteLine($"[Migration] Column {col} already exists.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Migration] Fatal error during manual migration: {ex.Message}");
        }

        try
        {
            db.Database.ExecuteSqlRaw(@"
                UPDATE ""Listings""
                SET ""EbayAccountId"" = (
                    SELECT et.""Id""
                    FROM ""EbayTokens"" et
                    WHERE et.""UserId"" = ""Listings"".""OwnerUserId""
                    ORDER BY et.""IsDefault"" DESC, et.""Id""
                    LIMIT 1
                )
                WHERE ""EbayAccountId"" IS NULL
                  AND EXISTS (
                    SELECT 1
                    FROM ""EbayTokens"" et
                    WHERE et.""UserId"" = ""Listings"".""OwnerUserId""
                );
            ");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Migration] Listing eBay account backfill error: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Overall Migration Error: {ex.Message}");
    }

    // Ensure Policies table exists and seed defaults from configuration if provided
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Policies"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Policies"" PRIMARY KEY AUTOINCREMENT,
                ""Marketplace"" TEXT NOT NULL,
                ""PolicyType"" TEXT NOT NULL,
                ""PolicyKey"" TEXT NOT NULL,
                ""Name"" TEXT NULL,
                ""IsDefault"" INTEGER NOT NULL DEFAULT 0
            );
        ");

        // Seed any configured EbaySettings defaults into Policies table if not present
        var paymentDefault = app.Configuration["EbaySettings:DefaultPaymentPolicyId"];
        var fulfillmentDefault = app.Configuration["EbaySettings:DefaultFulfillmentPolicyId"];
        var returnDefault = app.Configuration["EbaySettings:DefaultReturnPolicyId"];

        // Use EF to insert if values are present and not already in table
        if (!string.IsNullOrEmpty(paymentDefault) && !db.Policies.Any(p => p.PolicyKey == paymentDefault))
        {
            db.Policies.Add(new Policy { Marketplace = "ebay", PolicyType = "payment", PolicyKey = paymentDefault, Name = "Default (from config)", IsDefault = true });
        }
        if (!string.IsNullOrEmpty(fulfillmentDefault) && !db.Policies.Any(p => p.PolicyKey == fulfillmentDefault))
        {
            db.Policies.Add(new Policy { Marketplace = "ebay", PolicyType = "fulfillment", PolicyKey = fulfillmentDefault, Name = "Default (from config)", IsDefault = true });
        }
        if (!string.IsNullOrEmpty(returnDefault) && !db.Policies.Any(p => p.PolicyKey == returnDefault))
        {
            db.Policies.Add(new Policy { Marketplace = "ebay", PolicyType = "return", PolicyKey = returnDefault, Name = "Default (from config)", IsDefault = true });
        }

        db.SaveChanges();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Policy migration/seeding error: {ex.Message}");
    }

    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""SupplierSyncRuns"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SupplierSyncRuns"" PRIMARY KEY AUTOINCREMENT,
                ""Supplier"" TEXT NOT NULL,
                ""StartedAtUtc"" TEXT NOT NULL,
                ""FinishedAtUtc"" TEXT NULL,
                ""ProcessedCount"" INTEGER NOT NULL DEFAULT 0,
                ""SuccessCount"" INTEGER NOT NULL DEFAULT 0,
                ""FailedCount"" INTEGER NOT NULL DEFAULT 0,
                ""TriggeredBy"" TEXT NOT NULL DEFAULT 'scheduler'
            );
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""SupplierListingSnapshots"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SupplierListingSnapshots"" PRIMARY KEY AUTOINCREMENT,
                ""ListingId"" INTEGER NOT NULL,
                ""SyncRunId"" INTEGER NULL,
                ""Supplier"" TEXT NOT NULL,
                ""SupplierSku"" TEXT NULL,
                ""SupplierPrice"" REAL NULL,
                ""StockStatus"" TEXT NOT NULL,
                ""StockText"" TEXT NULL,
                ""CheckedAtUtc"" TEXT NOT NULL,
                ""IsSuccess"" INTEGER NOT NULL DEFAULT 0,
                ""ErrorMessage"" TEXT NULL
            );
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_SupplierListingSnapshots_ListingId_CheckedAtUtc""
            ON ""SupplierListingSnapshots"" (""ListingId"", ""CheckedAtUtc"");
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""SupplierFeeds"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SupplierFeeds"" PRIMARY KEY AUTOINCREMENT,
                ""Supplier"" TEXT NOT NULL,
                ""FeedType"" TEXT NOT NULL,
                ""Url"" TEXT NOT NULL,
                ""Description"" TEXT NULL,
                ""CookieHeader"" TEXT NULL,
                ""IsRecommended"" INTEGER NOT NULL DEFAULT 0,
                ""CreatedAtUtc"" TEXT NOT NULL
            );
        ");

        var supplierFeedColumns = new List<string>();
        using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            if (cmd.Connection?.State != System.Data.ConnectionState.Open)
            {
                cmd.Connection?.Open();
            }

            cmd.CommandText = "PRAGMA table_info('SupplierFeeds');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                supplierFeedColumns.Add(reader.GetString(1));
            }
        }

        if (!supplierFeedColumns.Contains("CookieHeader", StringComparer.OrdinalIgnoreCase))
        {
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""SupplierFeeds"" ADD COLUMN ""CookieHeader"" TEXT NULL;");
        }

        db.Database.ExecuteSqlRaw(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SupplierFeeds_Supplier_FeedType_Url""
            ON ""SupplierFeeds"" (""Supplier"", ""FeedType"", ""Url"");
        ");

        db.Database.ExecuteSqlRaw(@"
            INSERT OR IGNORE INTO ""SupplierFeeds"" (""Supplier"", ""FeedType"", ""Url"", ""Description"", ""IsRecommended"", ""CreatedAtUtc"")
            VALUES
            ('DropShipMall', 'Product Catalogue', 'https://www.dropshipmall.co.uk/trade/catalogue/products', 'Full product details and prices. Best for initial import and periodic catalog refresh.', 0, CURRENT_TIMESTAMP),
            ('DropShipMall', 'Shopify Catalogue', 'https://www.dropshipmall.co.uk/trade/catalogue/shopify', 'Shopify-optimized feed. Use if your workflow is Shopify-centric.', 0, CURRENT_TIMESTAMP),
            ('DropShipMall', 'Stock Feed', 'https://www.dropshipmall.co.uk/trade/catalogue/stock', 'Stock-only feed with direct URL access. Best for frequent stock sync automation.', 1, CURRENT_TIMESTAMP);
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""SupplierProducts"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SupplierProducts"" PRIMARY KEY AUTOINCREMENT,
                ""Supplier"" TEXT NOT NULL,
                ""SupplierFeedId"" INTEGER NULL,
                ""ExternalProductId"" TEXT NULL,
                ""Handle"" TEXT NULL,
                ""SupplierSku"" TEXT NULL,
                ""Title"" TEXT NOT NULL,
                ""Description"" TEXT NULL,
                ""Brand"" TEXT NULL,
                ""ProductUrl"" TEXT NULL,
                ""ImageUrl"" TEXT NULL,
                ""CategoryName"" TEXT NULL,
                ""ImageUrlsJson"" TEXT NULL,
                ""SupplierPrice"" REAL NULL,
                ""StockLevel"" INTEGER NULL,
                ""SuggestedPrice"" REAL NULL,
                ""Currency"" TEXT NOT NULL DEFAULT 'GBP',
                ""IsActive"" INTEGER NOT NULL DEFAULT 1,
                ""RawPayloadJson"" TEXT NULL,
                ""CreatedAtUtc"" TEXT NOT NULL,
                ""UpdatedAtUtc"" TEXT NOT NULL
            );
        ");

        var supplierProductColumns = new List<string>();
        using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            if (cmd.Connection?.State != System.Data.ConnectionState.Open)
            {
                cmd.Connection?.Open();
            }

            cmd.CommandText = "PRAGMA table_info('SupplierProducts');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                supplierProductColumns.Add(reader.GetString(1));
            }
        }

        if (!supplierProductColumns.Contains("CategoryName", StringComparer.OrdinalIgnoreCase))
        {
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""SupplierProducts"" ADD COLUMN ""CategoryName"" TEXT NULL;");
        }

        if (!supplierProductColumns.Contains("ImageUrlsJson", StringComparer.OrdinalIgnoreCase))
        {
            db.Database.ExecuteSqlRaw(@"ALTER TABLE ""SupplierProducts"" ADD COLUMN ""ImageUrlsJson"" TEXT NULL;");
        }

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_SupplierProducts_Supplier_Sku""
            ON ""SupplierProducts"" (""Supplier"", ""SupplierSku"");
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_SupplierProducts_Supplier_ExternalId""
            ON ""SupplierProducts"" (""Supplier"", ""ExternalProductId"");
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_Listings_SupplierProductId""
            ON ""Listings"" (""SupplierProductId"");
        ");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Supplier sync migration error: {ex.Message}");
    }
}

// Suppliers, delivery options, pricing/enrichment columns (added Oct 2026).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Suppliers"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Suppliers"" PRIMARY KEY AUTOINCREMENT,
                ""Code"" TEXT NOT NULL,
                ""Name"" TEXT NOT NULL,
                ""WebsiteHost"" TEXT NULL,
                ""PricesIncludeVat"" INTEGER NOT NULL DEFAULT 0,
                ""VatRatePercent"" TEXT NOT NULL DEFAULT '20',
                ""DefaultMarginPercent"" TEXT NOT NULL DEFAULT '20',
                ""IsActive"" INTEGER NOT NULL DEFAULT 1
            );");
        db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Suppliers_Code"" ON ""Suppliers"" (""Code"");");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""SupplierDeliveryOptions"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SupplierDeliveryOptions"" PRIMARY KEY AUTOINCREMENT,
                ""SupplierId"" INTEGER NOT NULL,
                ""Name"" TEXT NOT NULL,
                ""Description"" TEXT NULL,
                ""PostageCategories"" TEXT NULL,
                ""Cost"" TEXT NOT NULL DEFAULT '0',
                ""CostIncludesVat"" INTEGER NOT NULL DEFAULT 0,
                ""IsDefault"" INTEGER NOT NULL DEFAULT 0,
                ""SortOrder"" INTEGER NOT NULL DEFAULT 0,
                ""IsActive"" INTEGER NOT NULL DEFAULT 1
            );");
        db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_SupplierDeliveryOptions_SupplierId"" ON ""SupplierDeliveryOptions"" (""SupplierId"");");

        void EnsureColumn(string table, string column, string definition)
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"PRAGMA table_info(""{table}"");";
            var exists = false;
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase)) exists = true;
                }
            }
            if (exists) return;
            cmd.CommandText = $@"ALTER TABLE ""{table}"" ADD COLUMN ""{column}"" {definition};";
            cmd.ExecuteNonQuery();
            Console.WriteLine($"[Migration] Added {table}.{column}");
        }

        EnsureColumn("SupplierProducts", "PostageCategory", "TEXT NULL");
        EnsureColumn("SupplierProducts", "Barcode", "TEXT NULL");
        EnsureColumn("SupplierProducts", "SpecificsJson", "TEXT NULL");
        EnsureColumn("SupplierProducts", "DetailsFetchedAtUtc", "TEXT NULL");

        EnsureColumn("Listings", "Marketplace", "TEXT NOT NULL DEFAULT 'eBay'");
        EnsureColumn("Listings", "DeliveryOptionName", "TEXT NULL");
        EnsureColumn("Listings", "DeliveryCost", "TEXT NULL");
        EnsureColumn("Listings", "IncludeDeliveryInPrice", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn("Listings", "ItemSpecificsJson", "TEXT NULL");
        EnsureColumn("Listings", "Barcode", "TEXT NULL");
        EnsureColumn("Suppliers", "SkuPrefix", "TEXT NULL");

        // Seed known suppliers. Existing rows (and any edits made on /suppliers) are left alone.
        db.Database.ExecuteSqlRaw(@"
            INSERT OR IGNORE INTO ""Suppliers"" (""Code"", ""Name"", ""WebsiteHost"", ""PricesIncludeVat"", ""VatRatePercent"", ""DefaultMarginPercent"", ""IsActive"") VALUES
            ('DropShipMall', 'DropShipMall', 'dropshipmall.co.uk', 0, '20', '20', 1),
            ('EdenHorticulture', 'Eden Horticulture', 'edenhorticulture.co.uk', 1, '20', '20', 1),
            ('Safelincs', 'Safelincs', 'safelincs.co.uk', 1, '20', '20', 1);");

        db.Database.ExecuteSqlRaw(@"
            UPDATE ""Suppliers"" SET ""SkuPrefix"" = CASE ""Code""
                WHEN 'DropShipMall' THEN 'DSM' WHEN 'EdenHorticulture' THEN 'EDN' WHEN 'Safelincs' THEN 'SFL' END
            WHERE ""SkuPrefix"" IS NULL AND ""Code"" IN ('DropShipMall', 'EdenHorticulture', 'Safelincs');");

        // DropShipMall UK delivery rates (ex VAT) from https://www.dropshipmall.co.uk/ordering/delivery-info/h1437.html
        db.Database.ExecuteSqlRaw(@"
            INSERT INTO ""SupplierDeliveryOptions"" (""SupplierId"", ""Name"", ""Description"", ""PostageCategories"", ""Cost"", ""CostIncludesVat"", ""IsDefault"", ""SortOrder"", ""IsActive"")
            SELECT s.""Id"", v.""Name"", v.""Description"", v.""PostageCategories"", v.""Cost"", 0, v.""IsDefault"", v.""SortOrder"", 1
            FROM ""Suppliers"" s
            JOIN (
                SELECT 'Standard Delivery (UK Mainland)' AS ""Name"", 'Non-fragile items' AS ""Description"", 'Standard Parcel,Small Parcel,Large Letter,DropShip' AS ""PostageCategories"", '4.99' AS ""Cost"", 1 AS ""IsDefault"", 10 AS ""SortOrder""
                UNION ALL SELECT 'Fragile Delivery (UK Mainland)', 'Glassware, ceramics, mirrors etc.', 'Fragile', '7.50', 0, 20
                UNION ALL SELECT 'Oversize Delivery (UK Mainland)', 'Large furniture, arches, trees', 'Oversize', '9.95', 0, 30
                UNION ALL SELECT 'Pallet Delivery (UK Mainland)', 'Items that ship on a pallet', 'Pallet', '39.99', 0, 40
                UNION ALL SELECT 'Express Delivery (UK Mainland)', 'Standard items only, 3pm cut-off', NULL, '9.95', 0, 50
                UNION ALL SELECT 'Standard Delivery (UK Zone 2)', 'Scottish Highlands, islands, Northern Ireland', NULL, '6.99', 0, 60
            ) v
            WHERE s.""Code"" = 'DropShipMall'
              AND NOT EXISTS (SELECT 1 FROM ""SupplierDeliveryOptions"" o WHERE o.""SupplierId"" = s.""Id"");");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Supplier/delivery migration error: {ex.Message}");
    }
}

using (var hangfireScope = app.Services.CreateScope())
{
    var recurringJobs = hangfireScope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<Server.Services.IEdenSupplierSyncService>(
        "eden-published-listing-sync",
        svc => svc.RunPublishedSyncAsync(),
        "0 */4 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    recurringJobs.AddOrUpdate<Server.Services.IFeedProductSyncService>(
        "dropshipmall-catalogue-import",
        svc => svc.RunCatalogueImportAsync(),
        "0 1 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    recurringJobs.AddOrUpdate<Server.Services.IFeedProductSyncService>(
        "dropshipmall-stock-refresh",
        svc => svc.RunStockRefreshAsync(),
        "0 */4 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });
}

app.Run();
