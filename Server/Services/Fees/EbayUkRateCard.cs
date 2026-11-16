using Server.Data;

namespace Server.Services;

/// <summary>
/// eBay UK final value fees for business sellers, by category, from
/// https://www.ebay.co.uk/help/selling/fees-credits-invoices/fees-business-sellers?id=4809 (page last updated 30 Sep 2026;
/// fees exclude VAT). Loaded on first start and with "Load eBay UK rates" on /suppliers; edit or update them there.
/// Not modelled: the Trainers rate (7% when the item price is £100 or more), below-standard surcharges and the
/// 10p small-order fee in selected Collectables categories.
/// </summary>
public static class EbayUkRateCard
{
    public const string SourceUrl = "https://www.ebay.co.uk/help/selling/fees-credits-invoices/fees-business-sellers?id=4809";
    public const string AsOf = "30 Sep 2026";

    private static MarketplaceFeeCategoryRate R(string id, string name, decimal percent,
        decimal? threshold1 = null, decimal? percent2 = null, decimal? threshold2 = null, decimal? percent3 = null, decimal? smallOrderFee = null)
        => new()
        {
            Marketplace = MarketplaceFeeService.Ebay, CategoryId = id, CategoryName = name, FinalValueFeePercent = percent,
            Threshold1 = threshold1, Percent2 = percent2, Threshold2 = threshold2, Percent3 = percent3, PerOrderFeeSmall = smallOrderFee
        };

    public static IReadOnlyList<MarketplaceFeeCategoryRate> Rates() => new[]
    {
        R("20081", "Antiques", 10.9m),
        R("550", "Art", 10.9m),
        R("262051", "Art NFTs", 5m),
        R("2984", "Baby", 10.9m),
        R("267", "Books, Comics & Magazines", 9.9m),
        R("12576", "Business, Office & Industrial", 12.5m),
        R("625", "Cameras & Photography", 9.9m),
        R("11724", "Camcorders", 6.9m, 1000m, 3m),
        R("31388", "Digital Cameras", 6.9m, 1000m, 3m),
        R("69323", "Film Photography", 6.9m, 1000m, 3m),
        R("78997", "Lenses & Filters", 6.9m, 1000m, 3m),
        R("11450", "Clothes, Shoes & Accessories", 11.9m),
        R("169291", "Women's Bags & Handbags", 12.9m, 800m, 7m),
        R("15709", "Men's Trainers (7% if item £100+)", 11.9m),
        R("95672", "Women's Trainers (7% if item £100+)", 11.9m),
        R("11116", "Coins", 10.9m, 450m, 3m),
        R("1", "Collectables", 10.9m),
        R("262050", "Emerging NFTs", 5m),
        R("262052", "Non-Sport Trading Card NFTs", 5m),
        R("58058", "Computers, Tablets & Networking", 9.9m),
        R("171957", "Desktops & All-In-Ones", 6.9m, 1000m, 3m),
        R("165", "Drives, Storage & Blank Media", 6.9m, 1000m, 3m),
        R("175698", "Enterprise Networking, Servers", 6.9m, 1000m, 3m),
        R("171485", "Tablets & eBook Readers", 6.9m, 1000m, 3m),
        R("175672", "Laptops & Netbooks", 6.9m, 1000m, 3m),
        R("171961", "Printers, Scanners & Supplies", 6.9m, 1000m, 3m),
        R("14339", "Crafts", 12.9m),
        R("237", "Dolls & Bears", 10.9m),
        R("1305", "Event Tickets", 12.9m),
        R("11232", "Films & TV", 9.9m),
        R("262053", "Film NFTs", 5m),
        R("159912", "Garden & Patio", 10.9m),
        R("26395", "Health & Beauty", 10.9m),
        R("182101", "Hair Extensions & Wigs", 11.9m),
        R("183497", "Electronic Smoking, Parts & Accessories", 12.9m),
        R("3252", "Holidays & Travel", 7.9m, 650m, 3m),
        R("11700", "Home, Furniture & DIY", 11.9m, 500m, 7.9m, smallOrderFee: 0.10m),
        R("20710", "Appliances", 6.9m, 400m, 3m),
        R("631", "DIY Tools & Workshop Equipment", 6.9m, 400m, 3m),
        R("67779", "Power Strips & Surge Protectors", 9.9m, 250m, 7.9m),
        R("3197", "Furniture", 10.9m, 500m, 7.9m, 1000m, 3m),
        R("26677", "Bath", 10.9m, 500m, 7.9m, 1000m, 3m),
        R("3191", "Other Home Plumbing & Fixtures", 10.9m, 500m, 7.9m, 1000m, 3m),
        R("281", "Jewellery & Watches", 14.9m, 1000m, 4m),
        R("260324", "Watches, Parts & Accessories", 12.9m, 750m, 3m),
        R("15032", "Mobile Phones & Communication", 9.9m),
        R("9355", "Mobile & Smart Phones", 6.9m, 1000m, 3m),
        R("11233", "Music", 9.9m),
        R("262054", "Music NFTs", 5m),
        R("619", "Musical Instruments & DJ Equipment", 10.9m),
        R("1281", "Pet Supplies", 12.9m),
        R("870", "Pottery, Ceramics & Glass", 10.9m),
        R("293", "Sound & Vision", 9.9m),
        R("32852", "DVD, Blu-ray & Home Cinema", 6.9m, 1000m, 3m),
        R("112529", "Headphones", 6.9m, 1000m, 3m),
        R("14969", "Home Audio & HiFi Separates", 6.9m, 1000m, 3m),
        R("11071", "Televisions", 6.9m, 1000m, 3m),
        R("888", "Sporting Goods", 10.9m),
        R("64482", "Sports Memorabilia", 10.9m),
        R("262055", "Sport Trading Card NFTs", 5m),
        R("260", "Stamps", 10.9m),
        R("220", "Toys & Games", 10.9m),
        R("262056", "CCG NFTs", 5m),
        R("117213", "Tents", 10.9m, 250m, 7.9m),
        R("131090", "Vehicle Parts & Accessories", 9.5m, 750m, 3m),
        R("179680", "Tyres", 6.9m, 750m, 3m),
        R("124313", "Tyres", 6.9m, 750m, 3m),
        R("139835", "GPS & Sat Nav Devices", 6.9m, 750m, 3m),
        R("35000", "Power Tools & Equipment (Vehicle Parts)", 6.9m, 750m, 3m),
        R("1249", "Video Games & Consoles", 9.9m),
        R("139971", "Video Game Consoles", 6.9m, 400m, 2m),
        R("40005", "Wholesale & Job Lots", 12.9m),
        R("99", "Everything Else", 12.9m),
        R("88739", "Memorials & Funerals", 11.9m),
    };

    /// <summary>Typical eBay fee category per known supplier (used for product estimates; changeable on /suppliers).</summary>
    public static readonly IReadOnlyDictionary<string, string> SupplierFeeCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DropShipMall"] = "11700",      // Home, Furniture & DIY (gifts, floristry, décor)
        ["EdenHorticulture"] = "159912", // Garden & Patio
        ["Safelincs"] = "12576",         // Business, Office & Industrial (fire safety)
        ["Godropship"] = "11700"         // Home, Furniture & DIY (mixed general merchandise)
    };
}
