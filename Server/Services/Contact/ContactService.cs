using System.ComponentModel.DataAnnotations;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Server.Configuration;

namespace Server.Services;

/// <summary>Enquiry posted by the Digital Club website contact form.</summary>
public class ContactRequest
{
    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = "";

    [StringLength(150)]
    public string? Company { get; set; }

    [StringLength(30)]
    public string? Topic { get; set; }

    [Required, StringLength(4000, MinimumLength = 2)]
    public string Message { get; set; } = "";

    [StringLength(10)]
    public string? Language { get; set; }

    /// <summary>Honeypot field, hidden from people. Anything in it means a bot.</summary>
    public string? Website { get; set; }

    public bool Consent { get; set; }
}

/// <summary>Sends an email. Separate from <see cref="ContactService"/> so tests don't need an SMTP server.</summary>
public interface IContactMailer
{
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);
}

public class SmtpContactMailer : IContactMailer
{
    private readonly ContactSettings _settings;

    public SmtpContactMailer(IOptions<ContactSettings> settings) => _settings = settings.Value;

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var smtp = _settings.Smtp;
        using var client = new SmtpClient();
        var security = smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(smtp.Host, smtp.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.Username))
        {
            await client.AuthenticateAsync(smtp.Username, smtp.Password, cancellationToken);
        }
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

public enum ContactResult { Sent, IgnoredSpam, NotConfigured }

/// <summary>Turns a website enquiry into an email to <see cref="ContactSettings.ToEmail"/>.</summary>
public class ContactService
{
    private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = "AI Implementation",
        ["agents"] = "Agent Implementation",
        ["digital"] = "Digital solution (not AI)",
        ["elistings"] = "eListings",
        ["other"] = "Something else"
    };

    private readonly ContactSettings _settings;
    private readonly IContactMailer _mailer;
    private readonly ILogger<ContactService> _logger;

    public ContactService(IOptions<ContactSettings> settings, IContactMailer mailer, ILogger<ContactService> logger)
    {
        _settings = settings.Value;
        _mailer = mailer;
        _logger = logger;
    }

    public async Task<ContactResult> SendAsync(ContactRequest request, string? visitorIp, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            _logger.LogInformation("Contact form: honeypot filled, message ignored");
            return ContactResult.IgnoredSpam;
        }
        if (!_settings.IsConfigured)
        {
            _logger.LogWarning("Contact form: message received but \"Contact:ToEmail\" / \"Contact:Smtp:Host\" aren't set, so it wasn't sent");
            return ContactResult.NotConfigured;
        }

        await _mailer.SendAsync(BuildMessage(request, visitorIp), cancellationToken);
        _logger.LogInformation("Contact form: enquiry emailed");
        return ContactResult.Sent;
    }

    internal MimeMessage BuildMessage(ContactRequest request, string? visitorIp)
    {
        var name = OneLine(request.Name);
        var topic = Topics.TryGetValue(request.Topic ?? "", out var t) ? t : "General";
        var from = string.IsNullOrWhiteSpace(_settings.FromEmail) ? _settings.Smtp.Username : _settings.FromEmail;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, from));
        message.To.Add(MailboxAddress.Parse(_settings.ToEmail));
        message.ReplyTo.Add(new MailboxAddress(name, request.Email.Trim()));
        message.Subject = $"Website enquiry: {topic} – {name}";

        var body = new StringBuilder()
            .AppendLine($"Name:     {name}")
            .AppendLine($"Email:    {request.Email.Trim()}")
            .AppendLine($"Company:  {OneLine(request.Company ?? "")}")
            .AppendLine($"Topic:    {topic}")
            .AppendLine($"Language: {OneLine(request.Language ?? "")}")
            .AppendLine($"Consent:  {(request.Consent ? "Yes" : "No")}")
            .AppendLine($"Sent:     {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC from {visitorIp ?? "unknown"}")
            .AppendLine()
            .AppendLine(request.Message.Trim())
            .AppendLine()
            .AppendLine("— Reply to this email to answer the enquiry.");
        message.Body = new TextPart("plain") { Text = body.ToString() };
        return message;
    }

    /// <summary>No line breaks in header-like values.</summary>
    private static string OneLine(string value) => string.Join(' ', value.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
}
