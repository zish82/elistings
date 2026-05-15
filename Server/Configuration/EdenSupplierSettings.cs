namespace Server.Configuration;

public class EdenSupplierSettings
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string LoginPath { get; set; } = "/account/login";
}
