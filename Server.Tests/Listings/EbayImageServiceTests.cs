using System.Text;
using Server.Services;
using Xunit;

namespace Server.Tests;

/// <summary>Picture upload: spotting non-image downloads (supplier block pages) and non-XML eBay answers.</summary>
public class EbayImageServiceTests
{
    [Fact]
    public void RealImageSignatures_AreRecognised()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 };
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D };
        var webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");

        Assert.True(EbayImageService.LooksLikeImage(jpeg));
        Assert.True(EbayImageService.LooksLikeImage(png));
        Assert.True(EbayImageService.LooksLikeImage(webp));
    }

    [Fact]
    public void HtmlBlockPage_IsNotAnImage()
    {
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><head><title>Access denied</title></head></html>");
        Assert.False(EbayImageService.LooksLikeImage(html));
        Assert.False(EbayImageService.LooksLikeImage(Array.Empty<byte>()));
    }

    [Fact]
    public void NonXmlEbayAnswer_GivesAReadableError()
    {
        var ex = Assert.Throws<Exception>(() => EbayImageService.ParseUploadResponse(503, "Service Unavailable"));
        Assert.Contains("HTTP 503", ex.Message);
        Assert.Contains("Service Unavailable", ex.Message);
    }

    [Fact]
    public void SuccessResponse_ReturnsThePictureUrl()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<UploadSiteHostedPicturesResponse xmlns=""urn:ebay:apis:eBLBaseComponents"">
  <Ack>Success</Ack>
  <SiteHostedPictureDetails><FullURL>https://i.ebayimg.com/00/s/abc/$_1.JPG</FullURL></SiteHostedPictureDetails>
</UploadSiteHostedPicturesResponse>";
        Assert.Equal("https://i.ebayimg.com/00/s/abc/$_1.JPG", EbayImageService.ParseUploadResponse(200, xml));
    }

    [Fact]
    public void FailureResponse_ReturnsEbaysMessage()
    {
        const string xml = @"<UploadSiteHostedPicturesResponse xmlns=""urn:ebay:apis:eBLBaseComponents"">
  <Ack>Failure</Ack><Errors><LongMessage>Picture is too small.</LongMessage></Errors>
</UploadSiteHostedPicturesResponse>";
        var ex = Assert.Throws<Exception>(() => EbayImageService.ParseUploadResponse(200, xml));
        Assert.Contains("Picture is too small.", ex.Message);
    }
}
