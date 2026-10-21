namespace Shared;

public class EbayAccountDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsConnected { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public int LinkedListingCount { get; set; }
    /// <summary>False for accounts connected before the sales report: reconnect to allow reading orders.</summary>
    public bool CanReadOrders { get; set; }
}
