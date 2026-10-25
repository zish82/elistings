namespace Server.Configuration;

/// <summary>
/// "Contact" section: where Digital Club website enquiries (POST api/public/contact) are emailed.
/// Put real values in appsettings.Development.json or environment variables (Contact__Smtp__Password …), never in git.
/// </summary>
public class ContactSettings
{
    /// <summary>Address that receives enquiries. Empty = contact form switched off (returns 503).</summary>
    public string ToEmail { get; set; } = "";

    /// <summary>Sender address (must be allowed by the SMTP service). Defaults to <see cref="SmtpSettings.Username"/>.</summary>
    public string FromEmail { get; set; } = "";

    public string FromName { get; set; } = "Digital Club website";

    /// <summary>Websites allowed to post the form from another domain (CORS), e.g. "https://digitalclub.example".
    /// Not needed when the site and the form endpoint share a domain (the Caddy setup proxies it).</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Messages accepted per visitor IP per hour.</summary>
    public int MaxPerHourPerIp { get; set; } = 5;

    public SmtpSettings Smtp { get; set; } = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ToEmail) && !string.IsNullOrWhiteSpace(Smtp.Host);
}

public class SmtpSettings
{
    public string Host { get; set; } = "";

    /// <summary>587 = STARTTLS (usual), 465 = TLS from the start.</summary>
    public int Port { get; set; } = 587;

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
