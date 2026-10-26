using System.Text;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>Picture upload (eBay Media API): spotting non-image downloads, reading eBay's answers, live progress.</summary>
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
    public void CreatedResponse_ReturnsThePictureUrl()
    {
        const string json = @"{ ""imageUrl"": ""https://i.ebayimg.com/images/g/abc/s-l1600.jpg"", ""expirationDate"": ""2026-12-01T00:00:00.000Z"" }";
        Assert.Equal("https://i.ebayimg.com/images/g/abc/s-l1600.jpg", EbayImageService.ParseImageResponse(201, json));
    }

    [Fact]
    public void CreatedWithoutBody_MeansUseTheLocationHeader()
    {
        Assert.Null(EbayImageService.ParseImageResponse(201, ""));
    }

    [Fact]
    public void ErrorResponse_ShowsEbaysMessage()
    {
        const string json = @"{ ""errors"": [ { ""errorId"": 190201, ""domain"": ""API_MEDIA"", ""message"": ""The image file size is larger than the limit."" } ] }";
        var ex = Assert.Throws<Exception>(() => EbayImageService.ParseImageResponse(400, json));
        Assert.Contains("HTTP 400", ex.Message);
        Assert.Contains("larger than the limit", ex.Message);
    }

    [Fact]
    public void NonJsonError_IsQuoted()
    {
        var ex = Assert.Throws<Exception>(() => EbayImageService.ParseImageResponse(404, "Resource not found"));
        Assert.Contains("HTTP 404", ex.Message);
        Assert.Contains("Resource not found", ex.Message);
    }

    [Fact]
    public void ProgressStore_TracksEachPicture()
    {
        var store = new PublishProgressStore();
        store.Start(5, "Checking the listing");
        store.SetImages(5, new[] { "https://supplier/a.jpg", "data:image/png;base64,AAAA" });
        store.SetImage(5, 0, PublishImageStates.Uploaded);
        store.SetImage(5, 1, PublishImageStates.Failed, "The supplier site answered 403 Forbidden instead of the picture");

        var progress = store.Get(5)!;
        Assert.Equal(PublishImageStates.Uploaded, progress.Images[0].State);
        Assert.Equal("Uploaded image", progress.Images[1].Url); // data: URLs aren't sent back to the page
        Assert.Contains("403", progress.Images[1].Message);
        Assert.Null(store.Get(6));
    }

    [Fact]
    public void ProgressStore_IgnoresPublishesThatWerentStarted()
    {
        var store = new PublishProgressStore(); // e.g. bulk publish, which has its own job progress
        store.SetImages(9, new[] { "https://supplier/a.jpg" });
        Assert.Null(store.Get(9));
    }
}
