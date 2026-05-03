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
}

public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string Role { get; set; } = Shared.AuthRoles.Lister;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Listing
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }
    public int? EbayAccountId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Status { get; set; } = "Draft";
    public string? EbayItemId { get; set; }
    public string? CategoryId { get; set; }
    public string? FulfillmentPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }
    public string? Type { get; set; }
    public string? Brand { get; set; }
    public string? Colour { get; set; }
    public string? Sku { get; set; }
    public string? EbayOfferId { get; set; }
    public string? ImageUrlsJson { get; set; }
    public string? SourceUrl { get; set; }
    public decimal? SourcePrice { get; set; }
    public string? SourceProductCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Policy
{
    public int Id { get; set; }
    public string Marketplace { get; set; } = string.Empty; // e.g. "ebay", "amazon"
    public string PolicyType { get; set; } = string.Empty; // e.g. "payment", "fulfillment", "return"
    public string PolicyKey { get; set; } = string.Empty; // the provider policy id
    public string Name { get; set; } = string.Empty; // human friendly name
    public bool IsDefault { get; set; } = false;
}

public class EbayTokenInfo
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "eBay Account";
    public bool IsDefault { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiryTime { get; set; }
    public DateTime RefreshTokenExpiryTime { get; set; }
}
