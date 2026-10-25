using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class Policy
{
    public int Id { get; set; }
    public string Marketplace { get; set; } = string.Empty; // e.g. "ebay", "amazon"
    public string PolicyType { get; set; } = string.Empty; // e.g. "payment", "fulfillment", "return"
    public string PolicyKey { get; set; } = string.Empty; // the provider policy id
    public string Name { get; set; } = string.Empty; // human friendly name
    public bool IsDefault { get; set; } = false;
}
