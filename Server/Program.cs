using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

var sqliteConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=app.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(sqliteConnectionString));

builder.Services.Configure<Server.Configuration.EbaySettings>(
    builder.Configuration.GetSection("EbaySettings"));

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
builder.Services.AddScoped<Server.Services.ICurrentUserService, Server.Services.CurrentUserService>();

builder.Services.AddHttpClient<Server.Services.IEbayService, Server.Services.EbayService>();
builder.Services.AddHttpClient<Server.Services.IMarketplaceImageService, Server.Services.EbayImageService>();
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

        // Ensure Listings table has required columns (completely silent raw SQL)
        var requiredColumns = new[] { "OwnerUserId", "EbayAccountId", "Type", "Brand", "Colour", "ImageUrlsJson", "Sku", "EbayOfferId", "SourceUrl", "SourcePrice", "SourceProductCode" };
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
                            else if (string.Equals(col, "OwnerUserId", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd.CommandText = $@"ALTER TABLE ""{actualTableName}"" ADD COLUMN ""{col}"" INTEGER NOT NULL DEFAULT 0;";
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
}

app.Run();
