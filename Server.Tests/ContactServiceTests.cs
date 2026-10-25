using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Moq;
using Server.Configuration;
using Server.Controllers;
using Server.Services;
using Xunit;

namespace Server.Tests;

/// <summary>Digital Club website contact form: honeypot, "not configured" and the email that is sent.</summary>
public class ContactServiceTests
{
    private static ContactSettings Configured() => new()
    {
        ToEmail = "owner@example.com",
        FromEmail = "website@example.com",
        Smtp = new SmtpSettings { Host = "smtp.example.com", Username = "user" }
    };

    private static ContactRequest Valid() => new()
    {
        Name = "Jane Doe",
        Email = "jane@example.org",
        Company = "Acme",
        Topic = "agents",
        Message = "We'd like agents for our support team.",
        Language = "en-gb",
        Consent = true
    };

    private static (ContactService Service, Mock<IContactMailer> Mailer) Create(ContactSettings settings)
    {
        var mailer = new Mock<IContactMailer>();
        return (new ContactService(Options.Create(settings), mailer.Object, NullLogger<ContactService>.Instance), mailer);
    }

    [Fact]
    public async Task ValidEnquiry_IsEmailed_WithReplyToTheVisitor()
    {
        var (service, mailer) = Create(Configured());
        MimeMessage? sent = null;
        mailer.Setup(m => m.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
              .Callback<MimeMessage, CancellationToken>((m, _) => sent = m)
              .Returns(Task.CompletedTask);

        var result = await service.SendAsync(Valid(), "203.0.113.5", CancellationToken.None);

        Assert.Equal(ContactResult.Sent, result);
        Assert.NotNull(sent);
        Assert.Equal("owner@example.com", sent!.To.Mailboxes.Single().Address);
        Assert.Equal("website@example.com", sent.From.Mailboxes.Single().Address);
        Assert.Equal("jane@example.org", sent.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("Website enquiry: Agent Implementation – Jane Doe", sent.Subject);
        Assert.Contains("We'd like agents for our support team.", sent.TextBody);
        Assert.Contains("Company:  Acme", sent.TextBody);
    }

    [Fact]
    public async Task Honeypot_IsIgnored_WithoutSending()
    {
        var (service, mailer) = Create(Configured());
        var request = Valid();
        request.Website = "http://spam.example";

        Assert.Equal(ContactResult.IgnoredSpam, await service.SendAsync(request, null, CancellationToken.None));
        mailer.Verify(m => m.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotConfigured_DoesNotSend()
    {
        var (service, mailer) = Create(new ContactSettings());

        Assert.Equal(ContactResult.NotConfigured, await service.SendAsync(Valid(), null, CancellationToken.None));
        mailer.Verify(m => m.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void LineBreaksInTheName_DontReachTheSubject()
    {
        var (service, _) = Create(Configured());
        var request = Valid();
        request.Name = "Jane\r\nBcc: someone@example.net";

        var message = service.BuildMessage(request, null);

        Assert.DoesNotContain("\n", message.Subject);
        Assert.Empty(message.Bcc);
    }

    [Fact]
    public async Task Controller_RejectsMissingConsent_AndReports503WhenNotConfigured()
    {
        var (service, _) = Create(new ContactSettings());
        var controller = new PublicContactController(service, NullLogger<PublicContactController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };

        var noConsent = Valid();
        noConsent.Consent = false;
        Assert.IsAssignableFrom<ObjectResult>(await controller.Post(noConsent, CancellationToken.None));
        Assert.False(controller.ModelState.IsValid);

        controller.ModelState.Clear();
        var result = Assert.IsType<ObjectResult>(await controller.Post(Valid(), CancellationToken.None));
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task Controller_Returns502WhenTheMailServerFails()
    {
        var (service, mailer) = Create(Configured());
        mailer.Setup(m => m.SendAsync(It.IsAny<MimeMessage>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new IOException("connection refused"));
        var controller = new PublicContactController(service, NullLogger<PublicContactController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };

        var result = Assert.IsType<ObjectResult>(await controller.Post(Valid(), CancellationToken.None));
        Assert.Equal(502, result.StatusCode);
    }
}
