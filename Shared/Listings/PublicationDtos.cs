namespace Shared;

public static class MarketplaceNames
{
    public const string Ebay = "eBay";
}

public static class PublicationStatuses
{
    public const string Published = "Published";
    public const string Failed = "Failed";
}

/// <summary>Where a listing is published: one row per marketplace account.</summary>
public class ListingPublicationDto
{
    public string Marketplace { get; set; } = MarketplaceNames.Ebay;
    public int AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Status { get; set; } = PublicationStatuses.Published;
    public string? ExternalListingId { get; set; }
    public string? LastError { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
}

/// <summary>Body of POST api/ebay/publish/{id}: which of the owner's accounts to publish to (empty = where it's already published, else the listing's account).</summary>
public class PublishRequest
{
    public List<int> AccountIds { get; set; } = new();
}

/// <summary>Accounts overview: one user with their marketplace accounts and what's posted on each.</summary>
public class UserAccountsOverviewDto
{
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsMe { get; set; }
    public int? ManagerUserId { get; set; }
    public string? ManagerEmail { get; set; }
    public List<AccountOverviewDto> Accounts { get; set; } = new();
    /// <summary>The user's listings not published anywhere yet (drafts).</summary>
    public int DraftCount { get; set; }
}

public class AccountOverviewDto
{
    public string Marketplace { get; set; } = MarketplaceNames.Ebay;
    public int AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsConnected { get; set; }
    /// <summary>This user's listings published on the account.</summary>
    public int PublishedCount { get; set; }
    /// <summary>This user's listings whose last publish to the account failed.</summary>
    public int FailedCount { get; set; }
    /// <summary>Connected by someone else and shared with this user by an Admin.</summary>
    public bool IsShared { get; set; }
    public string? OwnerEmail { get; set; }
    /// <summary>Users the account is shared with (filled for Admins, on the owner's row).</summary>
    public List<int> SharedWithUserIds { get; set; } = new();
}
