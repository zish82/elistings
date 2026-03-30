using Microsoft.EntityFrameworkCore;
using Server.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var sqliteConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=app.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(sqliteConnectionString));

builder.Services.Configure<Server.Configuration.EbaySettings>(
    builder.Configuration.GetSection("EbaySettings"));

builder.Services.AddHttpClient<Server.Services.IEbayService, Server.Services.EbayService>();
builder.Services.AddHttpClient<Server.Services.IMarketplaceImageService, Server.Services.EbayImageService>();
builder.Services.AddHttpClient<Server.Services.ScraperService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder => builder.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// Some browsers request /favicon.ico even when index.html specifies a PNG favicon.
app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.png"));

app.UseCors("AllowAll");

app.UseHttpsRedirection();

// Enable routing middleware so endpoint routing can execute mapped endpoints
app.UseRouting();

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
            CREATE TABLE IF NOT EXISTS ""EbayTokens"" (
                ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_EbayTokens"" PRIMARY KEY AUTOINCREMENT,
                ""AccessToken"" TEXT NOT NULL,
                ""RefreshToken"" TEXT NOT NULL,
                ""ExpiryTime"" TEXT NOT NULL,
                ""RefreshTokenExpiryTime"" TEXT NOT NULL
            );
        ");

        // Ensure Listings table has required columns (completely silent raw SQL)
        var requiredColumns = new[] { "Type", "Brand", "Colour", "ImageUrlsJson", "Sku", "EbayOfferId", "SourceUrl", "SourcePrice", "SourceProductCode" };
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
