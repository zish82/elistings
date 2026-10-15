namespace Server.Configuration;

/// <summary>
/// Trade-account login used to download the DropShipMall Product / Shopify catalogue feeds,
/// which are only available to a logged-in session. Put real values in appsettings.Development.json
/// (or user-secrets), not in appsettings.json.
/// </summary>
public class DropShipMallSettings
{
    public string BaseUrl { get; set; } = "https://www.dropshipmall.co.uk";
    public string LoginPath { get; set; } = "/account.php?c=l";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}
