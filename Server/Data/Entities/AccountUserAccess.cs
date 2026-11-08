using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>
/// A marketplace account (for eBay: EbayTokenInfo) shared with another eListings user by an Admin.
/// The user who connected the account always has access (EbayTokenInfo.UserId) and needs no row here.
/// Each user still only sees the listings they created themselves.
/// </summary>
public class AccountUserAccess
{
    public int Id { get; set; }
    public string Marketplace { get; set; } = MarketplaceNames.Ebay;
    public int AccountId { get; set; }
    public int UserId { get; set; }
}
