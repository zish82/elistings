using System.Text.Json;
using Server.Configuration;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>
/// Shared pre-publish logic for single and bulk publishing: fills default policies,
/// auto-picks an eBay category when missing, and validates required fields so we fail
/// with a clear message before calling eBay.
/// </summary>
public static class ListingPublishHelper
{
    public const int MaxTitleLength = 80;

    public static async Task<ListingDto> PrepareAsync(Listing listing, AppDbContext context, IEbayService ebayService, EbaySettings settings, SkuService skuService)
    {
        // Drafts created before SKU generation existed get one now; published listings keep theirs.
        await skuService.EnsureSkuAsync(listing);

        // Policies: listing -> DB default -> appsettings default
        listing.PaymentPolicyId = FirstNonEmpty(listing.PaymentPolicyId, DefaultPolicy(context, "payment"), settings.DefaultPaymentPolicyId);
        listing.FulfillmentPolicyId = FirstNonEmpty(listing.FulfillmentPolicyId, DefaultPolicy(context, "fulfillment"), settings.DefaultFulfillmentPolicyId);
        listing.ReturnPolicyId = FirstNonEmpty(listing.ReturnPolicyId, DefaultPolicy(context, "return"), settings.DefaultReturnPolicyId);

        var images = string.IsNullOrEmpty(listing.ImageUrlsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(listing.ImageUrlsJson) ?? new List<string>();

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(listing.Title)) problems.Add("title is empty");
        else if (listing.Title.Trim().Length > MaxTitleLength) problems.Add($"title is {listing.Title.Trim().Length} characters (eBay max is {MaxTitleLength})");
        if (listing.Price <= 0m) problems.Add("item price is not set");
        else if (PricingMath.ListingTotal(listing.Price, listing.DeliveryCost, listing.IncludeDeliveryInPrice) < 0.99m) problems.Add("total price must be at least £0.99");
        if (!string.Equals(listing.Marketplace, "eBay", StringComparison.OrdinalIgnoreCase)) problems.Add($"marketplace '{listing.Marketplace}' is not supported yet");
        if (!images.Any()) problems.Add("no images");
        if (string.IsNullOrWhiteSpace(listing.PaymentPolicyId)) problems.Add("no payment policy");
        if (string.IsNullOrWhiteSpace(listing.FulfillmentPolicyId)) problems.Add("no shipping policy");
        if (string.IsNullOrWhiteSpace(listing.ReturnPolicyId)) problems.Add("no return policy");

        if (problems.Any())
        {
            throw new InvalidOperationException("Cannot publish: " + string.Join(", ", problems) + ".");
        }

        // Category: auto-pick eBay's top suggestion from the title when none is set.
        if (string.IsNullOrWhiteSpace(listing.CategoryId))
        {
            var suggestions = await ebayService.GetCategorySuggestionsAsync(listing.Title, listing.EbayAccountId);
            var best = suggestions.FirstOrDefault();
            if (best == null || string.IsNullOrWhiteSpace(best.CategoryId))
            {
                throw new InvalidOperationException("Cannot publish: no eBay category set and none could be suggested from the title.");
            }
            listing.CategoryId = best.CategoryId;
            listing.CategoryName = best.CategoryName;
        }

        // Separate postage isn't implemented: the marketplace price is item price + delivery.
        // The DTO handed to the marketplace service is therefore already priced at the total.
        var marketplacePrice = PricingMath.ListingTotal(listing.Price, listing.DeliveryCost, listing.IncludeDeliveryInPrice);

        return new ListingDto
        {
            Id = listing.Id,
            EbayAccountId = listing.EbayAccountId,
            Title = listing.Title.Trim(),
            Description = listing.Description,
            Price = marketplacePrice,
            DeliveryCost = null,
            IncludeDeliveryInPrice = true,
            ItemSpecifics = ListingMapping.ReadSpecifics(listing.ItemSpecificsJson),
            Barcode = listing.Barcode,
            Marketplace = listing.Marketplace,
            Quantity = listing.Quantity < 1 ? 1 : listing.Quantity,
            CategoryId = listing.CategoryId,
            CategoryName = listing.CategoryName,
            FulfillmentPolicyId = listing.FulfillmentPolicyId,
            PaymentPolicyId = listing.PaymentPolicyId,
            ReturnPolicyId = listing.ReturnPolicyId,
            Type = listing.Type,
            Brand = listing.Brand,
            Colour = listing.Colour,
            Sku = listing.Sku,
            EbayOfferId = listing.EbayOfferId,
            ImageUrls = images,
            SourceUrl = listing.SourceUrl
        };
    }

    public static void ApplyPublishResult(Listing listing, EbayListingResponse result)
    {
        listing.Status = "Published";
        listing.EbayItemId = result.ListingId;
        listing.Sku = result.Sku;
        listing.EbayOfferId = result.OfferId;
    }

    private static string? DefaultPolicy(AppDbContext context, string type)
        => context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == type && p.IsDefault)?.PolicyKey;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
