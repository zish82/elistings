namespace Server.Data;

/// <summary>
/// Reference data the app needs (suppliers, DropShipMall delivery rates, supplier feeds, default policies),
/// written with EF so it works on PostgreSQL and SQLite alike. Idempotent: only adds what's missing and never
/// overwrites values edited on /suppliers.
/// </summary>
public static class SeedData
{
    public static void Ensure(AppDbContext db, IConfiguration configuration, bool hangfireIsPersistent)
    {
        EnsureSuppliers(db);
        EnsureDropShipMallDeliveryOptions(db);
        EnsureSupplierFeeds(db);
        EnsureDefaultPolicies(db, configuration);
        BackfillListingSupplierCategories(db);
        EnsureFeeSettings(db);
        RecoverInterruptedJobs(db, hangfireIsPersistent);
        db.SaveChanges();
    }

    private static void EnsureSuppliers(AppDbContext db)
    {
        var known = new[]
        {
            new Supplier { Code = "DropShipMall", Name = "DropShipMall", WebsiteHost = "dropshipmall.co.uk", SkuPrefix = "DSM", PricesIncludeVat = false },
            new Supplier { Code = "EdenHorticulture", Name = "Eden Horticulture", WebsiteHost = "edenhorticulture.co.uk", SkuPrefix = "EDN", PricesIncludeVat = false },
            new Supplier { Code = "Safelincs", Name = "Safelincs", WebsiteHost = "safelincs.co.uk", SkuPrefix = "SFL", PricesIncludeVat = true },
        };

        var existing = db.Suppliers.ToList();
        foreach (var supplier in known)
        {
            var match = existing.FirstOrDefault(s => s.Code.Equals(supplier.Code, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                supplier.VatRatePercent = 20m;
                supplier.DefaultMarginPercent = 20m;
                supplier.IsActive = true;
                db.Suppliers.Add(supplier);
            }
            else if (string.IsNullOrWhiteSpace(match.SkuPrefix))
            {
                match.SkuPrefix = supplier.SkuPrefix;
            }
        }
        db.SaveChanges();
    }

    /// <summary>DropShipMall UK delivery rates (ex VAT), from https://www.dropshipmall.co.uk/ordering/delivery-info/h1437.html</summary>
    private static void EnsureDropShipMallDeliveryOptions(AppDbContext db)
    {
        var dsm = db.Suppliers.FirstOrDefault(s => s.Code == "DropShipMall");
        if (dsm == null || db.SupplierDeliveryOptions.Any(o => o.SupplierId == dsm.Id)) return;

        SupplierDeliveryOption Option(string name, string description, string? categories, decimal cost, bool isDefault, int order) => new()
        {
            SupplierId = dsm.Id, Name = name, Description = description, PostageCategories = categories,
            Cost = cost, CostIncludesVat = false, IsDefault = isDefault, SortOrder = order, IsActive = true
        };

        db.SupplierDeliveryOptions.AddRange(
            Option("Standard Delivery (UK Mainland)", "Non-fragile items", "Standard Parcel,Small Parcel,Large Letter,DropShip", 4.99m, true, 10),
            Option("Fragile Delivery (UK Mainland)", "Glassware, ceramics, mirrors etc.", "Fragile", 7.50m, false, 20),
            Option("Oversize Delivery (UK Mainland)", "Large furniture, arches, trees", "Oversize", 9.95m, false, 30),
            Option("Pallet Delivery (UK Mainland)", "Items that ship on a pallet", "Pallet", 39.99m, false, 40),
            Option("Express Delivery (UK Mainland)", "Standard items only, 3pm cut-off", null, 9.95m, false, 50),
            Option("Standard Delivery (UK Zone 2)", "Scottish Highlands, islands, Northern Ireland", null, 6.99m, false, 60));
    }

    private static void EnsureSupplierFeeds(AppDbContext db)
    {
        var feeds = new[]
        {
            new SupplierFeed { Supplier = "DropShipMall", FeedType = "Product Catalogue", Url = "https://www.dropshipmall.co.uk/trade/catalogue/products",
                Description = "Full product details and prices. Best for initial import and periodic catalog refresh." },
            new SupplierFeed { Supplier = "DropShipMall", FeedType = "Shopify Catalogue", Url = "https://www.dropshipmall.co.uk/trade/catalogue/shopify",
                Description = "Shopify-optimized feed. Use if your workflow is Shopify-centric." },
            new SupplierFeed { Supplier = "DropShipMall", FeedType = "Stock Feed", Url = "https://www.dropshipmall.co.uk/trade/catalogue/stock",
                Description = "Stock-only feed with direct URL access. Best for frequent stock sync automation.", IsRecommended = true },
            new SupplierFeed { Supplier = "EdenHorticulture", FeedType = "Shopify Catalogue", Url = "https://edenhorticulture.co.uk/products.json",
                Description = "Public catalogue + logged-in trade prices and stock. Runs in the background.", IsRecommended = true },
            new SupplierFeed { Supplier = "Safelincs", FeedType = "Website Catalogue", Url = "https://www.safelincs.co.uk/sitemap.xml",
                Description = "Product pages from the sitemap (details, stock) + your uploaded trade price list. Runs in the background (a few hours).", IsRecommended = true },
        };

        var existing = db.SupplierFeeds.Select(f => new { f.Supplier, f.FeedType, f.Url }).ToList();
        foreach (var feed in feeds)
        {
            if (existing.Any(e => e.Supplier == feed.Supplier && e.FeedType == feed.FeedType && e.Url == feed.Url)) continue;
            feed.CreatedAtUtc = DateTime.UtcNow;
            db.SupplierFeeds.Add(feed);
        }
    }

    /// <summary>eBay UK business-seller rate card (Feb 2026), not VAT-registered. Edited on /suppliers.</summary>
    private static void EnsureFeeSettings(AppDbContext db)
    {
        if (db.MarketplaceFeeSettings.Any(s => s.Marketplace == "eBay")) return;
        db.MarketplaceFeeSettings.Add(new MarketplaceFeeSettings
        {
            Marketplace = "eBay",
            FinalValueFeePercent = 12.9m,
            PerOrderFeeSmall = 0.30m,
            PerOrderFeeLarge = 0.40m,
            PerOrderFeeThreshold = 10m,
            RegulatoryFeePercent = 0.35m,
            VatRegistered = false,
            VatRatePercent = 20m,
            MinProfit = 1m
        });
    }

    /// <summary>Listings created before supplier categories were stored get the category of their supplier product.</summary>
    private static void BackfillListingSupplierCategories(AppDbContext db)
    {
        var listings = db.Listings.Where(l => l.SupplierCategory == null && l.SupplierProductId != null).ToList();
        if (listings.Count == 0) return;

        var productIds = listings.Select(l => l.SupplierProductId!.Value).Distinct().ToList();
        var categories = db.SupplierProducts
            .Where(p => productIds.Contains(p.Id) && p.CategoryName != null && p.CategoryName != "")
            .Select(p => new { p.Id, p.CategoryName })
            .ToDictionary(p => p.Id, p => p.CategoryName);

        foreach (var listing in listings)
        {
            if (categories.TryGetValue(listing.SupplierProductId!.Value, out var category)) listing.SupplierCategory = category;
        }
    }

    /// <summary>Default eBay policy ids from configuration (EbaySettings:Default*PolicyId), if set.</summary>
    private static void EnsureDefaultPolicies(AppDbContext db, IConfiguration configuration)
    {
        foreach (var (type, key) in new[] { ("payment", "DefaultPaymentPolicyId"), ("fulfillment", "DefaultFulfillmentPolicyId"), ("return", "DefaultReturnPolicyId") })
        {
            var policyId = configuration[$"EbaySettings:{key}"];
            if (string.IsNullOrWhiteSpace(policyId) || db.Policies.Any(p => p.PolicyKey == policyId)) continue;
            db.Policies.Add(new Policy { Marketplace = "ebay", PolicyType = type, PolicyKey = policyId, Name = "Default (from config)", IsDefault = true });
        }
    }

    /// <summary>
    /// A job that was running when the server stopped can't still be running now. With persistent Hangfire storage it
    /// will be picked up again (and marked Running again); with in-memory storage queued jobs are lost too.
    /// </summary>
    private static void RecoverInterruptedJobs(AppDbContext db, bool hangfireIsPersistent)
    {
        var stale = db.BulkJobs
            .Where(j => j.Status == "Running" || (!hangfireIsPersistent && j.Status == "Queued"))
            .ToList();
        foreach (var job in stale)
        {
            job.Status = "Failed";
            job.FinishedAtUtc = DateTime.UtcNow;
            job.Message = "Interrupted by a server restart. Start it again if it doesn't resume.";
        }
    }
}
