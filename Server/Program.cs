using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.PostgreSql;
using Hangfire.Storage.SQLite;
using Server.Data;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddMemoryCache(); // e.g. eBay category item specifics
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

// Polite access to supplier websites: every HttpClient from the factory goes through the per-host throttle.
builder.Services.AddSingleton(sp => new Server.Services.HostRequestThrottle(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddTransient<Server.Services.HostThrottlingHandler>();
builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<Server.Services.HostThrottlingHandler>());

// ---- Database: PostgreSQL when the connection string is a Postgres one ("Host=..."), otherwise SQLite (legacy).
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=app.db";
var usePostgres = DatabaseSetup.IsPostgres(connectionString);
if (usePostgres)
{
    // Keep DateTime as "timestamp without time zone" (same behaviour as SQLite: values are UTC by convention).
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
}
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (usePostgres) options.UseNpgsql(connectionString);
    else options.UseSqlite(connectionString);
});

// ---- Hangfire runs all background work (scheduled + started from pages).
// PostgreSQL: Hangfire's tables live in the main database, in their own "hangfire" schema.
// SQLite: a separate hangfire.db next to app.db. "Hangfire:Storage": "Memory" forces in-memory (not persistent).
var useMemoryForHangfire = (builder.Configuration["Hangfire:Storage"] ?? string.Empty).Equals("Memory", StringComparison.OrdinalIgnoreCase);
// A job is only handed to another worker if it hasn't finished within this time. Must be longer than our
// longest job (bulk publish of many listings), otherwise a slow job could run twice at once.
// The Safelincs import (one page every few seconds) is the longest, typically 2–3 hours.
var hangfireInvisibilityTimeout = TimeSpan.FromHours(10);
// How quickly an idle worker notices a newly queued job (default 15s feels slow after a click).
var hangfireQueuePollInterval = TimeSpan.FromSeconds(2);
builder.Services.AddHangfire(configuration =>
{
    configuration
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings();

    if (useMemoryForHangfire)
    {
        configuration.UseMemoryStorage();
    }
    else if (usePostgres)
    {
        configuration.UsePostgreSqlStorage(
            options => options.UseNpgsqlConnection(connectionString),
            new Hangfire.PostgreSql.PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                InvisibilityTimeout = hangfireInvisibilityTimeout,
                QueuePollInterval = hangfireQueuePollInterval
            });
    }
    else
    {
        var appDbSource = DatabaseSetup.SqliteDataSource(connectionString);
        var hangfireDbPath = builder.Configuration["Hangfire:DatabasePath"]
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(appDbSource, builder.Environment.ContentRootPath)) ?? builder.Environment.ContentRootPath, "hangfire.db");
        configuration.UseSQLiteStorage(hangfireDbPath, new SQLiteStorageOptions
        {
            InvisibilityTimeout = hangfireInvisibilityTimeout,
            QueuePollInterval = hangfireQueuePollInterval
        });
    }
});
// Main worker pool for everything except Eden...
builder.Services.AddHangfireServer(options =>
{
    options.ServerName = $"{Environment.MachineName}:main";
    options.Queues = new[] { Server.Services.Jobs.BackgroundJobs.DefaultQueue };
    options.WorkerCount = 4; // plenty for this app, and keeps database write contention low
});
// ...and a single worker for the "eden" queue, so only one Eden job (import, sync, scrape) runs at a time.
builder.Services.AddHangfireServer(options =>
{
    options.ServerName = $"{Environment.MachineName}:eden";
    options.Queues = new[] { Server.Services.Jobs.BackgroundJobs.EdenQueue };
    options.WorkerCount = 1;
});
// ...and one for Safelincs (its imports take hours, so they don't hold up Eden or page jobs).
builder.Services.AddHangfireServer(options =>
{
    options.ServerName = $"{Environment.MachineName}:safelincs";
    options.Queues = new[] { Server.Services.Jobs.BackgroundJobs.SafelincsQueue };
    options.WorkerCount = 1;
});
// ...and one for Godropship (also a website pass of a few hours).
builder.Services.AddHangfireServer(options =>
{
    options.ServerName = $"{Environment.MachineName}:godropship";
    options.Queues = new[] { Server.Services.Jobs.BackgroundJobs.GodropshipQueue };
    options.WorkerCount = 1;
});

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
builder.Services.AddScoped<Server.Services.Jobs.BulkJobService>();
builder.Services.AddScoped<Server.Services.Jobs.BackgroundJobs>();
builder.Services.AddScoped<Server.Services.SupplierCatalogService>();
builder.Services.AddScoped<Server.Services.SupplierAccessService>();
// Per-supplier integrations. Onboarding a supplier = add its enricher / stock checker here.
builder.Services.AddHttpClient<Server.Services.DropShipMallWebsiteClient>();
builder.Services.AddScoped<Server.Services.ISupplierProductEnricher, Server.Services.DropShipMallProductEnricher>();
builder.Services.AddScoped<Server.Services.ProductEnrichmentService>();
builder.Services.AddScoped<Server.Services.ISupplierStockChecker, Server.Services.DropShipMallStockChecker>();
builder.Services.AddScoped<Server.Services.ISupplierStockChecker, Server.Services.GodropshipStockChecker>();
builder.Services.AddScoped<Server.Services.ISupplierStockChecker, Server.Services.EdenStockChecker>(); // before the legacy adapter, so it wins for imported Eden products
builder.Services.AddScoped<Server.Services.ISupplierStockChecker, Server.Services.WebsiteStockCheckerAdapter>(); // Eden, Safelincs
builder.Services.AddScoped<Server.Services.SupplierStockService>();
builder.Services.AddScoped<Server.Services.SkuService>();
builder.Services.AddScoped<Server.Services.EdenCatalogImportService>();
builder.Services.AddScoped<Server.Services.ISupplierCatalogImporter>(sp => sp.GetRequiredService<Server.Services.EdenCatalogImportService>());
builder.Services.AddScoped<Server.Services.SupplierPriceListService>();
builder.Services.AddScoped<Server.Services.ProductTagService>();
builder.Services.AddScoped<Server.Services.MarketplaceFeeService>();
builder.Services.AddScoped<Server.Services.EbayCategoryService>();
builder.Services.AddScoped<Server.Services.EbayOrderSyncService>();
builder.Services.AddScoped<Server.Services.SafelincsCatalogImportService>();
builder.Services.AddScoped<Server.Services.ISupplierCatalogImporter>(sp => sp.GetRequiredService<Server.Services.SafelincsCatalogImportService>());
builder.Services.AddHttpClient<Server.Services.GodropshipWebsiteClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });
builder.Services.AddScoped<Server.Services.GodropshipCatalogImportService>();
builder.Services.AddScoped<Server.Services.ISupplierCatalogImporter>(sp => sp.GetRequiredService<Server.Services.GodropshipCatalogImportService>());
builder.Services.AddHttpClient("safelincs")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });

builder.Services.AddHttpClient<Server.Services.IEbayService, Server.Services.EbayService>();
builder.Services.AddHttpClient<Server.Services.IMarketplaceImageService, Server.Services.EbayImageService>();
builder.Services.AddSingleton<Server.Services.PublishProgressStore>(); // live picture-by-picture publish status
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
builder.Services.AddSingleton<Server.Services.IProductScraper, Server.Services.GodropshipProductScraper>();
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

    // Digital Club website contact form posted from another domain (only the origins in "Contact:AllowedOrigins").
    var contactOrigins = builder.Configuration.GetSection("Contact:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    options.AddPolicy(Server.Controllers.ContactPolicies.Cors, policy =>
        policy.WithOrigins(contactOrigins)
              .WithMethods("POST")
              .WithHeaders("Content-Type"));
});

// Digital Club website contact form: email settings + per-IP rate limit.
builder.Services.Configure<Server.Configuration.ContactSettings>(builder.Configuration.GetSection("Contact"));
builder.Services.AddScoped<Server.Services.IContactMailer, Server.Services.SmtpContactMailer>();
builder.Services.AddScoped<Server.Services.ContactService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var perHour = Math.Max(1, builder.Configuration.GetValue<int?>("Contact:MaxPerHourPerIp") ?? 5);
    options.AddPolicy(Server.Controllers.ContactPolicies.RateLimit, context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = perHour,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
});

// One-off data copy from the old SQLite file into PostgreSQL:
//   dotnet run --project Server -- --copy-sqlite-to-postgres app.db
if (args.Contains("--copy-sqlite-to-postgres"))
{
    var sourceIndex = Array.IndexOf(args, "--copy-sqlite-to-postgres") + 1;
    var sourceFile = sourceIndex < args.Length && !args[sourceIndex].StartsWith("--") ? args[sourceIndex] : "app.db";
    Environment.ExitCode = await SqliteToPostgresCopier.RunAsync(sourceFile, connectionString, force: args.Contains("--force"));
    return;
}

var app = builder.Build();

// Create / update the database schema and seed reference data (SQLite or PostgreSQL).
// Must run before anything touches Hangfire storage: on an empty PostgreSQL database Hangfire would create its
// tables first and EnsureCreated would then skip the app tables.
DatabaseSetup.Initialize(app.Services, app.Configuration, hangfireIsPersistent: !useMemoryForHangfire);


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

app.UseHttpsRedirection();

// Enable routing middleware so endpoint routing can execute mapped endpoints
app.UseRouting();

// After UseRouting so an endpoint's own [EnableCors] policy (the public contact form) is honoured;
// every other endpoint keeps the "ClientCors" default.
app.UseCors("ClientCors");
app.UseRateLimiter();

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

// Queued job rows whose Hangfire job no longer exists (e.g. hangfire.db was reset) would wait forever.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var monitoring = scope.ServiceProvider.GetRequiredService<JobStorage>().GetMonitoringApi();
        foreach (var job in db.BulkJobs.Where(j => j.Status == "Queued").ToList())
        {
            if (string.IsNullOrEmpty(job.HangfireJobId) || monitoring.JobDetails(job.HangfireJobId) == null)
            {
                job.Status = "Failed";
                job.FinishedAtUtc = DateTime.UtcNow;
                job.Message = "This job was lost before it started. Start it again.";
            }
        }
        db.SaveChanges();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Job recovery check skipped: {ex.Message}");
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
            TimeZone = TimeZoneInfo.Utc,
            MisfireHandling = MisfireHandlingMode.Ignorable // app was off at the scheduled time: wait for the next one
        });

    recurringJobs.AddOrUpdate<Server.Services.IFeedProductSyncService>(
        "dropshipmall-catalogue-import",
        svc => svc.RunCatalogueImportAsync(),
        "0 1 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc,
            MisfireHandling = MisfireHandlingMode.Ignorable // app was off at the scheduled time: wait for the next one
        });

    recurringJobs.AddOrUpdate<Server.Services.EdenCatalogImportService>(
        "eden-catalogue-import",
        svc => svc.RunScheduledAsync(),
        "30 2 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc,
            MisfireHandling = MisfireHandlingMode.Ignorable // app was off at the scheduled time: wait for the next one
        });

    // Safelincs: a full website pass (a few hours at one page every few seconds), overnight.
    recurringJobs.AddOrUpdate<Server.Services.SafelincsCatalogImportService>(
        "safelincs-catalogue-import",
        svc => svc.RunScheduledAsync(),
        "30 3 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc,
            MisfireHandling = MisfireHandlingMode.Ignorable // app was off at the scheduled time: wait for the next one
        });

    // Godropship: a full website pass (~2,400 pages, about 2 hours), overnight. Only runs when background sync is
    // switched on for Godropship on /supplier-feeds (off by default until Godropship gives written permission).
    recurringJobs.AddOrUpdate<Server.Services.GodropshipCatalogImportService>(
        "godropship-catalogue-import",
        svc => svc.RunScheduledAsync(),
        "30 4 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc,
            MisfireHandling = MisfireHandlingMode.Ignorable // app was off at the scheduled time: wait for the next one
        });

    // Sales report: automatic import of new/changed eBay orders. Off by default (use "Sync now" on /sales);
    // turn on with "EbayOrders": { "AutoSyncEnabled": true } (schedule: "AutoSyncCron", default every 2 hours).
    if (app.Configuration.GetValue<bool>("EbayOrders:AutoSyncEnabled"))
    {
        recurringJobs.AddOrUpdate<Server.Services.EbayOrderSyncService>(
            "ebay-orders-sync",
            svc => svc.RunScheduledAsync(),
            app.Configuration["EbayOrders:AutoSyncCron"] ?? "15 */2 * * *",
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
                MisfireHandling = MisfireHandlingMode.Ignorable
            });
    }
    else
    {
        recurringJobs.RemoveIfExists("ebay-orders-sync");
    }

    recurringJobs.AddOrUpdate<Server.Services.IFeedProductSyncService>(
        "dropshipmall-stock-refresh",
        svc => svc.RunStockRefreshAsync(),
        "0 */4 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc,
            MisfireHandling = MisfireHandlingMode.Ignorable // app was off at the scheduled time: wait for the next one
        });
}

app.Run();
