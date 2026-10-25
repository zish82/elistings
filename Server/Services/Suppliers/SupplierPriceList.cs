using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

public sealed record PriceListRow(string ProductCode, string? Description, string? Category, decimal Price, decimal? DeliveryCost);

/// <summary>
/// Reads a supplier trade price list (.xlsx or .csv). Built for the Safelincs "Trade Price List" workbook
/// (columns: Product Code, Description, Category, Price (ex VAT), Difference, Delivery, With Delivery and tax;
/// one sheet per category plus "All Products"), but only needs a product code and a price column.
/// </summary>
public static class SupplierPriceListParser
{
    private const int HeaderSearchRows = 30;

    public static (List<PriceListRow> Rows, List<string> Warnings) Parse(Stream stream, string fileName)
    {
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(stream);
            var lines = new List<IReadOnlyList<string?>>();
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(SplitCsvLine(line));
            var warnings = new List<string>();
            return (ParseTable(lines, sheetName: null, warnings), warnings);
        }

        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Upload the price list as an Excel (.xlsx) or CSV file.");
        }

        using var workbook = new XLWorkbook(stream);
        var sheets = workbook.Worksheets.ToList();
        // The "All Products" sheet already has everything; otherwise combine the category sheets.
        var all = sheets.FirstOrDefault(s => s.Name.Trim().Equals("All Products", StringComparison.OrdinalIgnoreCase));
        if (all != null) sheets = new List<IXLWorksheet> { all };

        var result = new Dictionary<string, PriceListRow>(StringComparer.OrdinalIgnoreCase);
        var sheetWarnings = new List<string>();
        foreach (var sheet in sheets)
        {
            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
            if (lastColumn == 0 || lastRow == 0) continue;

            var rows = new List<IReadOnlyList<string?>>(lastRow);
            for (var r = 1; r <= lastRow; r++)
            {
                var cells = new string?[lastColumn];
                for (var c = 1; c <= lastColumn; c++) cells[c - 1] = CellText(sheet.Cell(r, c));
                rows.Add(cells);
            }

            var category = all != null ? null : sheet.Name.Trim();
            foreach (var row in ParseTable(rows, category, sheetWarnings))
            {
                result.TryAdd(row.ProductCode, row); // first sheet wins for duplicates
            }
        }

        if (result.Count == 0 && sheetWarnings.Count == 0)
        {
            sheetWarnings.Add("No rows with a product code and a price were found.");
        }
        return (result.Values.ToList(), sheetWarnings);
    }

    /// <summary>Finds the header row, maps the columns, and reads every row with a code and a price.</summary>
    internal static List<PriceListRow> ParseTable(IReadOnlyList<IReadOnlyList<string?>> rows, string? sheetName, List<string> warnings)
    {
        var result = new List<PriceListRow>();
        int headerIndex = -1, code = -1, price = -1, delivery = -1, description = -1, category = -1;

        for (var r = 0; r < Math.Min(rows.Count, HeaderSearchRows) && headerIndex < 0; r++)
        {
            var headers = rows[r].Select(h => Normalize(h)).ToList();
            var codeIdx = headers.FindIndex(h => h is "product code" or "code" or "sku" or "product code/sku");
            if (codeIdx < 0) codeIdx = headers.FindIndex(h => h.Contains("product code"));
            if (codeIdx < 0) continue;

            headerIndex = r;
            code = codeIdx;
            // Trade price ex VAT: prefer a header that says "ex", then any plain "price" column.
            price = headers.FindIndex(h => h.Contains("price") && h.Contains("ex"));
            if (price < 0) price = headers.FindIndex(h => h.Contains("price") && !h.Contains("with") && !h.Contains("inc") && !h.Contains("rrp") && !h.Contains("retail"));
            delivery = headers.FindIndex(h => h == "delivery" || (h.StartsWith("delivery") && !h.Contains("with")));
            description = headers.FindIndex(h => h is "description" or "product name" or "name" or "product");
            category = headers.FindIndex(h => h == "category");
        }

        var label = sheetName == null ? "the file" : $"sheet '{sheetName}'";
        if (headerIndex < 0)
        {
            warnings.Add($"No 'Product Code' header found in {label}; skipped.");
            return result;
        }
        if (price < 0)
        {
            warnings.Add($"No price column found in {label}; skipped.");
            return result;
        }

        for (var r = headerIndex + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var productCode = At(row, code)?.Trim();
            if (string.IsNullOrWhiteSpace(productCode)) continue;
            var value = ParseMoney(At(row, price));
            if (value is not > 0m) continue;

            result.Add(new PriceListRow(
                productCode,
                NullIfEmpty(At(row, description)),
                NullIfEmpty(At(row, category)) ?? sheetName,
                value.Value,
                ParseMoney(At(row, delivery)) is { } d && d >= 0 ? d : null));
        }
        return result;
    }

    internal static decimal? ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Replace(",", ""), @"-?\d+(?:\.\d+)?");
        return m.Success && decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string? CellText(IXLCell cell)
    {
        var value = cell.Value; // cached result for formula cells
        if (value.IsBlank) return null;
        if (value.IsNumber) return value.GetNumber().ToString(CultureInfo.InvariantCulture);
        if (value.IsText) return value.GetText();
        return value.ToString();
    }

    private static string? At(IReadOnlyList<string?> row, int index) => index >= 0 && index < row.Count ? row[index] : null;

    private static string Normalize(string? header) => Regex.Replace((header ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Minimal CSV splitter (quoted fields, doubled quotes).</summary>
    internal static List<string?> SplitCsvLine(string line)
    {
        var fields = new List<string?>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else current.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields;
    }
}

/// <summary>Stores a supplier's uploaded trade price list and applies it to that supplier's products.</summary>
public class SupplierPriceListService
{
    private readonly AppDbContext _context;
    private readonly SupplierCatalogService _catalog;

    public SupplierPriceListService(AppDbContext context, SupplierCatalogService catalog)
    {
        _context = context;
        _catalog = catalog;
    }

    /// <summary>Replaces the supplier's price list with <paramref name="rows"/> and re-prices matching products.</summary>
    public async Task<PriceListUploadResultDto> ReplaceAsync(string supplierCode, IReadOnlyCollection<PriceListRow> rows, CancellationToken ct = default)
    {
        var result = new PriceListUploadResultDto { Rows = rows.Count };
        var now = DateTime.UtcNow;

        await using (var tx = await _context.Database.BeginTransactionAsync(ct))
        {
            await _context.SupplierPriceListItems.Where(i => i.Supplier == supplierCode).ExecuteDeleteAsync(ct);
            _context.SupplierPriceListItems.AddRange(rows.Select(r => new SupplierPriceListItem
            {
                Supplier = supplierCode,
                ProductCode = r.ProductCode,
                Description = r.Description,
                Category = r.Category,
                Price = r.Price,
                DeliveryCost = r.DeliveryCost,
                UploadedAtUtc = now
            }));
            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (await EnsurePricesExVatAsync(supplierCode, ct))
        {
            result.Notes.Add("The supplier was set to 'prices exclude VAT' on /suppliers, because trade price lists are ex VAT.");
        }

        result.ProductsUpdated = await ApplyAsync(supplierCode, ct);
        return result;
    }

    /// <summary>Trade prices (code → row) for the importer. Case-insensitive codes.</summary>
    public async Task<Dictionary<string, SupplierPriceListItem>> LoadAsync(string supplierCode, CancellationToken ct = default)
        => (await _context.SupplierPriceListItems.AsNoTracking().Where(i => i.Supplier == supplierCode).ToListAsync(ct))
            .GroupBy(i => i.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public async Task<PriceListStatusDto> GetStatusAsync(string supplierCode, CancellationToken ct = default)
    {
        var items = _context.SupplierPriceListItems.Where(i => i.Supplier == supplierCode);
        var count = await items.CountAsync(ct);
        var uploaded = count == 0 ? null : await items.MaxAsync(i => (DateTime?)i.UploadedAtUtc, ct);
        var codes = await items.Select(i => i.ProductCode.ToLower()).ToListAsync(ct);
        var matched = count == 0 ? 0 : await _context.SupplierProducts
            .Where(p => p.Supplier == supplierCode && p.SupplierSku != null && codes.Contains(p.SupplierSku.ToLower()))
            .CountAsync(ct);
        return new PriceListStatusDto { Supplier = supplierCode, Rows = count, UploadedAtUtc = uploaded, MatchedProducts = matched };
    }

    /// <summary>
    /// Trade price lists are ex VAT. A supplier marked "prices include VAT" would get VAT left out of its costs,
    /// so switch it once (returns true when changed).
    /// </summary>
    public async Task<bool> EnsurePricesExVatAsync(string supplierCode, CancellationToken ct = default)
    {
        var supplier = await _context.Suppliers.FirstOrDefaultAsync(s => s.Code == supplierCode, ct);
        if (supplier == null || !supplier.PricesIncludeVat) return false;
        supplier.PricesIncludeVat = false;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Applies the stored price list to the supplier's imported products (matched by SKU = product code).</summary>
    public async Task<int> ApplyAsync(string supplierCode, CancellationToken ct = default)
    {
        var prices = await LoadAsync(supplierCode, ct);
        if (prices.Count == 0) return 0;

        // Also when the supplier is disabled (FindAsync only returns enabled suppliers).
        var supplier = (await _catalog.GetAllAsync()).FirstOrDefault(s => s.Code.Equals(supplierCode, StringComparison.OrdinalIgnoreCase));
        var products = await _context.SupplierProducts
            .Where(p => p.Supplier == supplierCode && p.SupplierSku != null)
            .ToListAsync(ct);

        var updated = new List<SupplierProduct>();
        foreach (var product in products)
        {
            if (!prices.TryGetValue(product.SupplierSku!.Trim(), out var item)) continue;
            ApplyPrice(product, item, supplier);
            updated.Add(product);
        }

        // Keep linked listings' cost in step (the listing price itself is the user's).
        var ids = updated.Select(p => p.Id).ToList();
        if (ids.Count > 0)
        {
            var byId = updated.ToDictionary(p => p.Id);
            var listings = await _context.Listings.Where(l => l.SupplierProductId != null && ids.Contains(l.SupplierProductId.Value)).ToListAsync(ct);
            foreach (var listing in listings) listing.SourcePrice = byId[listing.SupplierProductId!.Value].SupplierPrice;
        }

        await _context.SaveChangesAsync(ct);
        return updated.Count;
    }

    public static void ApplyPrice(SupplierProduct product, SupplierPriceListItem item, SupplierDto? supplier)
    {
        product.SupplierPrice = item.Price;
        product.PriceIsEstimate = false;
        product.TradePriceFetchedAtUtc = item.UploadedAtUtc;
        if (item.DeliveryCost.HasValue) product.DeliveryCost = item.DeliveryCost;
        if (string.IsNullOrWhiteSpace(product.CategoryName) && !string.IsNullOrWhiteSpace(item.Category)) product.CategoryName = item.Category;
        product.SuggestedPrice = PricingMath.SuggestItemPrice(PricingMath.CostIncVat(product.SupplierPrice, supplier), supplier?.DefaultMarginPercent ?? 20m);
    }
}
