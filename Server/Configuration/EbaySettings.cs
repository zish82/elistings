namespace Server.Configuration;

public class EbaySettings
{
    public string AppId { get; set; } = string.Empty;
    public string CertId { get; set; } = string.Empty;
    public string DevId { get; set; } = string.Empty;
    public string RuName { get; set; } = string.Empty;
    public string UserToken { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public bool IsSandbox { get; set; } = true;
    // Default business policy IDs to apply when creating listings programmatically
    public string DefaultPaymentPolicyId { get; set; } = string.Empty;
    public string DefaultFulfillmentPolicyId { get; set; } = string.Empty;
    public string DefaultReturnPolicyId { get; set; } = string.Empty;
}
