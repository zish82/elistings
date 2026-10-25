using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users { get; set; }
    public DbSet<Listing> Listings { get; set; }
    public DbSet<EbayTokenInfo> EbayTokens { get; set; }
    public DbSet<Policy> Policies { get; set; }
    public DbSet<SupplierSyncRun> SupplierSyncRuns { get; set; }
    public DbSet<SupplierListingSnapshot> SupplierListingSnapshots { get; set; }
    public DbSet<SupplierFeed> SupplierFeeds { get; set; }
    public DbSet<SupplierProduct> SupplierProducts { get; set; }
    public DbSet<Supplier> Suppliers { get; set; }
    public DbSet<SupplierDeliveryOption> SupplierDeliveryOptions { get; set; }
    public DbSet<BulkJobRecord> BulkJobs { get; set; }
    public DbSet<SupplierPriceListItem> SupplierPriceListItems { get; set; }
    public DbSet<MarketplaceFeeSettings> MarketplaceFeeSettings { get; set; }
    public DbSet<MarketplaceFeeCategoryRate> MarketplaceFeeCategoryRates { get; set; }
    public DbSet<EbayCategory> EbayCategories { get; set; }
    public DbSet<EbayOrder> EbayOrders { get; set; }
    public DbSet<UserSupplierAccess> UserSupplierAccess { get; set; }
    public DbSet<EbayOrderLine> EbayOrderLines { get; set; }
    public DbSet<ProductTag> ProductTags { get; set; }
    public DbSet<SupplierProductTag> SupplierProductTags { get; set; }

    /// <summary>
    /// Indexes (used when EF creates the schema, i.e. PostgreSQL and new databases; existing SQLite databases
    /// get the same indexes from LegacySqliteSchema).
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<EbayTokenInfo>().HasIndex(t => t.UserId);
        modelBuilder.Entity<Listing>().HasIndex(l => l.OwnerUserId);
        modelBuilder.Entity<Listing>().HasIndex(l => l.SupplierProductId);
        modelBuilder.Entity<Listing>().HasIndex(l => l.Sku);
        modelBuilder.Entity<SupplierFeed>().HasIndex(f => new { f.Supplier, f.FeedType, f.Url }).IsUnique();
        modelBuilder.Entity<SupplierProduct>().HasIndex(p => new { p.Supplier, p.SupplierSku });
        modelBuilder.Entity<SupplierProduct>().HasIndex(p => new { p.Supplier, p.ExternalProductId });
        modelBuilder.Entity<SupplierProduct>().HasIndex(p => p.GroupKey);
        modelBuilder.Entity<SupplierListingSnapshot>().HasIndex(s => new { s.ListingId, s.CheckedAtUtc });
        modelBuilder.Entity<Supplier>().HasIndex(s => s.Code).IsUnique();
        modelBuilder.Entity<SupplierDeliveryOption>().HasIndex(o => o.SupplierId);
        modelBuilder.Entity<BulkJobRecord>().HasIndex(j => new { j.Type, j.Tag });
        modelBuilder.Entity<SupplierPriceListItem>().HasIndex(i => new { i.Supplier, i.ProductCode });
        modelBuilder.Entity<MarketplaceFeeSettings>().HasIndex(s => s.Marketplace).IsUnique();
        modelBuilder.Entity<MarketplaceFeeCategoryRate>().HasIndex(r => new { r.Marketplace, r.CategoryId }).IsUnique();
        modelBuilder.Entity<EbayCategory>().HasIndex(c => c.CategoryId).IsUnique();
        modelBuilder.Entity<EbayOrder>().HasIndex(o => o.OrderId).IsUnique();
        modelBuilder.Entity<UserSupplierAccess>().HasIndex(a => new { a.UserId, a.SupplierCode }).IsUnique();
        modelBuilder.Entity<EbayOrder>().HasIndex(o => new { o.OwnerUserId, o.CreatedAtUtc });
        modelBuilder.Entity<EbayOrderLine>().HasIndex(l => l.EbayOrderId);
        modelBuilder.Entity<ProductTag>().HasIndex(t => new { t.Supplier, t.NormalizedName }).IsUnique();
        modelBuilder.Entity<SupplierProductTag>().HasIndex(t => new { t.SupplierProductId, t.ProductTagId }).IsUnique();
        modelBuilder.Entity<SupplierProductTag>().HasIndex(t => t.ProductTagId);
    }
}
