using Microsoft.EntityFrameworkCore;
using Server.Configuration;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>Result of publishing one listing to one marketplace account.</summary>
public class AccountPublishResult
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ExternalListingId { get; set; }
    public List<string> ImageWarnings { get; set; } = new();
}

public class ListingPublishOutcome
{
    public List<AccountPublishResult> Results { get; } = new();
    public bool AnySucceeded => Results.Any(r => r.Success);

    /// <summary>Things to tell the user although the listing was published: accounts that failed, pictures left out.</summary>
    public List<string> Warnings()
    {
        var multi = Results.Count > 1;
        var warnings = new List<string>();
        foreach (var r in Results)
        {
            var prefix = multi ? $"{r.AccountName}: " : string.Empty;
            if (!r.Success) warnings.Add($"{prefix}not published – {r.Error}");
            warnings.AddRange(r.ImageWarnings.Select(w => prefix + w));
        }
        return warnings;
    }
}

/// <summary>
/// Publishes a listing to one or more of its owner's marketplace accounts (same price and quantity everywhere) and
/// records each in <see cref="ListingPublication"/>. The listing's own EbayAccountId / EbayItemId / EbayOfferId keep
/// its primary publication, so older code and pages keep working.
/// </summary>
public class ListingPublisher
{
    private readonly AppDbContext _context;
    private readonly IEbayService _ebay;
    private readonly EbaySettings _settings;
    private readonly SkuService _skus;
    private readonly ICurrentUserService? _publisher;

    public ListingPublisher(AppDbContext context, IEbayService ebay, EbaySettings settings, SkuService skus, ICurrentUserService? publisher)
    {
        _context = context;
        _ebay = ebay;
        _settings = settings;
        _skus = skus;
        _publisher = publisher;
    }

    /// <summary>
    /// The accounts a publish goes to: the ones asked for; otherwise every account it's already on (re-sync);
    /// otherwise the listing's account (or the owner's default).
    /// </summary>
    public async Task<List<int>> TargetAccountsAsync(Listing listing, IReadOnlyCollection<int>? requestedAccountIds)
    {
        if (requestedAccountIds is { Count: > 0 }) return requestedAccountIds.Distinct().ToList();

        var existing = await _context.ListingPublications
            .Where(p => p.ListingId == listing.Id && p.Marketplace == MarketplaceNames.Ebay && p.Status == PublicationStatuses.Published)
            .OrderBy(p => p.Id)
            .Select(p => p.AccountId)
            .ToListAsync();
        if (existing.Count > 0)
        {
            // Primary first.
            if (listing.EbayAccountId.HasValue && existing.Remove(listing.EbayAccountId.Value)) existing.Insert(0, listing.EbayAccountId.Value);
            return existing;
        }
        return listing.EbayAccountId.HasValue ? new List<int> { listing.EbayAccountId.Value } : new List<int>();
    }

    /// <summary>
    /// Bulk publish to chosen accounts: a published listing keeps the accounts it's on (main account first) plus the chosen
    /// ones; a draft goes to the chosen accounts only (its main account becomes the first chosen one it can use).
    /// </summary>
    public static async Task<List<int>> BulkTargetsAsync(AppDbContext context, Listing listing, IReadOnlyCollection<int> chosen)
    {
        var current = await context.ListingPublications
            .Where(p => p.ListingId == listing.Id && p.Marketplace == MarketplaceNames.Ebay && p.Status == PublicationStatuses.Published)
            .OrderBy(p => p.Id)
            .Select(p => p.AccountId)
            .ToListAsync();
        if (current.Count == 0) return chosen.Distinct().ToList();

        var targets = new List<int>();
        if (listing.EbayAccountId.HasValue && current.Contains(listing.EbayAccountId.Value)) targets.Add(listing.EbayAccountId.Value);
        targets.AddRange(current.Concat(chosen).Where(id => !targets.Contains(id)).Distinct());
        return targets;
    }

    /// <summary>Publishes (or re-syncs). Throws when nothing could be published; otherwise returns per-account results.</summary>
    public async Task<ListingPublishOutcome> PublishAsync(Listing listing, IReadOnlyCollection<int>? requestedAccountIds = null)
    {
        var targets = await TargetAccountsAsync(listing, requestedAccountIds);

        // The primary account is validated and used for the listing's own fields.
        if (targets.Count > 0 && (!listing.EbayAccountId.HasValue || !targets.Contains(listing.EbayAccountId.Value)))
        {
            listing.EbayAccountId = targets[0];
        }

        // Validation, default policies, SKU, auto category (throws InvalidOperationException with a clear message).
        var dto = await ListingPublishHelper.PrepareAsync(listing, _context, _ebay, _settings, _skus, _publisher);
        await _context.SaveChangesAsync(); // keep an auto-selected category / defaulted policies even if eBay rejects

        // Suppliers that block this server's picture downloads: only pictures uploaded by hand (or already on eBay) are used.
        var pictureSupplier = (await new SupplierCatalogService(_context).GetContextForListingAsync(listing)).Supplier;
        if (pictureSupplier is { RequireUploadedPictures: true })
        {
            dto.ImageUrls = dto.ImageUrls
                .Where(u => u.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) || u.Contains("ebayimg.com", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (dto.ImageUrls.Count == 0)
                throw new InvalidOperationException($"Upload at least one picture for this listing first: {pictureSupplier.Name} pictures can't be downloaded by this server (Suppliers page → Upload pictures manually).");
        }
        if (!listing.EbayAccountId.HasValue)
        {
            throw new InvalidOperationException("Cannot publish: no eBay account connected. Connect one on the eBay Accounts page.");
        }
        if (targets.Count == 0) targets.Add(listing.EbayAccountId.Value);
        if (dto.Variants.Count > 0 && targets.Count > 1)
        {
            throw new InvalidOperationException("A listing with variations can only be published to one account for now.");
        }

        var primary = listing.EbayAccountId.Value;
        if (targets.Remove(primary)) targets.Insert(0, primary);

        var accounts = await _context.EbayTokens.AsNoTracking()
            .Where(t => targets.Contains(t.Id))
            .Select(t => new { t.Id, t.UserId, t.Name })
            .ToDictionaryAsync(t => t.Id);
        var publications = await _context.ListingPublications
            .Where(p => p.ListingId == listing.Id && p.Marketplace == MarketplaceNames.Ebay)
            .ToListAsync();

        var outcome = new ListingPublishOutcome();
        PolicyNames? primaryPolicyNames = null;
        var listingFieldsSet = false;

        foreach (var accountId in targets)
        {
            accounts.TryGetValue(accountId, out var a);
            var result = new AccountPublishResult { AccountId = accountId, AccountName = a?.Name ?? $"Account {accountId}" };
            outcome.Results.Add(result);

            if (a == null) { result.Error = "this account is no longer connected"; continue; }
            if (!await AccountAccess.CanUseAsync(_context, listing.OwnerUserId, accountId)) { result.Error = "the listing's owner can't use this account"; continue; }

            var publication = publications.FirstOrDefault(p => p.AccountId == accountId);
            try
            {
                var accountDto = ForAccount(dto, accountId, publication, listing, primary);
                if (accountId != primary)
                {
                    primaryPolicyNames ??= await PolicyNamesAsync(primary, dto);
                    await ApplyAccountPoliciesAsync(accountDto, accountId, primaryPolicyNames);
                }

                var response = await _ebay.CreateListingAsync(accountDto);
                result.Success = true;
                result.ExternalListingId = response.ListingId;
                result.ImageWarnings = response.ImageWarnings ?? new List<string>();

                if (response.VariantOfferIds.Count > 0)
                {
                    foreach (var variant in await _context.ListingVariants.Where(v => v.ListingId == listing.Id).ToListAsync())
                        if (response.VariantOfferIds.TryGetValue(variant.Sku, out var variantOffer)) variant.OfferId = variantOffer;
                }

                publication ??= AddPublication(listing.Id, accountId, publications);
                publication.Status = PublicationStatuses.Published;
                publication.ExternalListingId = response.ListingId;
                publication.OfferId = response.OfferId;
                publication.Sku = response.Sku;
                publication.LastError = null;
                publication.PublishedAtUtc ??= DateTime.UtcNow;
                publication.UpdatedAtUtc = DateTime.UtcNow;

                // The listing's own fields follow the primary account, or the first account that worked (primary is tried first).
                if (!listingFieldsSet)
                {
                    listing.EbayAccountId = accountId;
                    ListingPublishHelper.ApplyPublishResult(listing, response);
                    listingFieldsSet = true;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                // A listing that is live on this account stays "Published" (the update failed, not the listing).
                // A publication created just now has never been live, so it is Failed.
                if (publication == null)
                {
                    publication = AddPublication(listing.Id, accountId, publications);
                    publication.Status = PublicationStatuses.Failed;
                }
                else if (publication.Status != PublicationStatuses.Published) publication.Status = PublicationStatuses.Failed;
                publication.LastError = ex.Message;
                publication.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await _context.SaveChangesAsync();

        if (!outcome.AnySucceeded)
        {
            var errors = outcome.Results.Select(r => outcome.Results.Count > 1 ? $"{r.AccountName}: {r.Error}" : r.Error);
            throw new Exception(string.Join(" | ", errors));
        }
        return outcome;
    }

    private ListingPublication AddPublication(int listingId, int accountId, List<ListingPublication> publications)
    {
        var publication = new ListingPublication { ListingId = listingId, Marketplace = MarketplaceNames.Ebay, AccountId = accountId };
        _context.ListingPublications.Add(publication);
        publications.Add(publication);
        return publication;
    }

    /// <summary>A copy of the prepared listing for one account (its own offer id; the primary keeps the listing's).</summary>
    private static ListingDto ForAccount(ListingDto dto, int accountId, ListingPublication? publication, Listing listing, int primary)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<ListingDto>(System.Text.Json.JsonSerializer.Serialize(dto))!;
        copy.EbayAccountId = accountId;
        // Offer ids belong to one account. Unknown = null: eBay is asked for this account's offer for the SKU.
        copy.EbayOfferId = publication?.OfferId ?? (accountId == primary && listing.EbayAccountId == primary ? dto.EbayOfferId : null);
        return copy;
    }

    private sealed record PolicyNames(string? Payment, string? Fulfillment, string? Return);

    /// <summary>Names of the primary account's chosen policies, to find same-named policies on the other accounts.</summary>
    private async Task<PolicyNames> PolicyNamesAsync(int primaryAccountId, ListingDto dto)
    {
        async Task<string?> NameOf(Func<int?, Task<List<EbayPolicyDto>>> load, string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            try { return (await load(primaryAccountId)).FirstOrDefault(p => p.PolicyId == id)?.Name; }
            catch { return null; }
        }
        return new PolicyNames(
            await NameOf(_ebay.GetPaymentPoliciesAsync, dto.PaymentPolicyId),
            await NameOf(_ebay.GetFulfillmentPoliciesAsync, dto.FulfillmentPolicyId),
            await NameOf(_ebay.GetReturnPoliciesAsync, dto.ReturnPolicyId));
    }

    /// <summary>
    /// Business policies belong to one eBay account. For another account use: the same policy id if that account has it,
    /// else a policy with the same name, else the account's first policy of that type.
    /// </summary>
    private async Task ApplyAccountPoliciesAsync(ListingDto dto, int accountId, PolicyNames names)
    {
        dto.PaymentPolicyId = PickPolicy(await _ebay.GetPaymentPoliciesAsync(accountId), dto.PaymentPolicyId, names.Payment, "payment");
        dto.FulfillmentPolicyId = PickPolicy(await _ebay.GetFulfillmentPoliciesAsync(accountId), dto.FulfillmentPolicyId, names.Fulfillment, "postage");
        dto.ReturnPolicyId = PickPolicy(await _ebay.GetReturnPoliciesAsync(accountId), dto.ReturnPolicyId, names.Return, "returns");
    }

    internal static string PickPolicy(IReadOnlyList<EbayPolicyDto> policies, string? currentId, string? currentName, string type)
    {
        if (policies.Count == 0) throw new InvalidOperationException($"this account has no {type} policy (create one in eBay Seller Hub)");
        return policies.FirstOrDefault(p => p.PolicyId == currentId)?.PolicyId
            ?? policies.FirstOrDefault(p => !string.IsNullOrWhiteSpace(currentName) && string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase))?.PolicyId
            ?? policies[0].PolicyId;
    }
}
