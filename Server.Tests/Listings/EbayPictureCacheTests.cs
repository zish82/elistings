using Server.Services;
using Xunit;

namespace Server.Tests;

public class EbayPictureCacheTests
{
    private const string Supplier1 = "https://supplier.example/a.jpg";
    private const string Supplier2 = "https://supplier.example/b.jpg";
    private const string OnEbay = "https://i.ebayimg.com/images/g/abc/s-l1600.jpg";

    /// <summary>A fake upload that counts calls and returns a predictable eBay URL.</summary>
    private sealed class FakeUpload
    {
        public List<string> Calls { get; } = new();
        public Task<string> Run(int index, string url)
        {
            Calls.Add(url);
            return Task.FromResult($"https://i.ebayimg.com/up/{Calls.Count}.jpg");
        }
    }

    [Fact]
    public async Task Republish_ReusesUploadedPictures_WithoutUploadingAgain()
    {
        using var db = TestHelpers.NewContext();
        var cache = new EbayPictureCache(db);
        var upload = new FakeUpload();

        var first = await cache.ResolveAsync(7, new[] { Supplier1, OnEbay, Supplier2 }, true, upload.Run);
        Assert.Equal(new[] { Supplier1, Supplier2 }, upload.Calls);
        Assert.Equal(new[] { "https://i.ebayimg.com/up/1.jpg", OnEbay, "https://i.ebayimg.com/up/2.jpg" }, first.Urls);
        Assert.Empty(first.ReusedSources);

        var second = await cache.ResolveAsync(7, new[] { Supplier1, OnEbay, Supplier2 }, true, upload.Run);
        Assert.Equal(2, upload.Calls.Count); // nothing uploaded again
        Assert.Equal(first.Urls, second.Urls);
        Assert.Equal(new[] { Supplier1, Supplier2 }, second.ReusedSources);
    }

    [Fact]
    public async Task OnlyNewPictures_AreUploaded()
    {
        using var db = TestHelpers.NewContext();
        var cache = new EbayPictureCache(db);
        var upload = new FakeUpload();

        await cache.ResolveAsync(7, new[] { Supplier1 }, true, upload.Run);
        await cache.ResolveAsync(7, new[] { Supplier1, Supplier2 }, true, upload.Run);

        Assert.Equal(new[] { Supplier1, Supplier2 }, upload.Calls);
    }

    [Fact]
    public async Task UploadsAreKeptPerAccount()
    {
        using var db = TestHelpers.NewContext();
        var cache = new EbayPictureCache(db);
        var upload = new FakeUpload();

        await cache.ResolveAsync(7, new[] { Supplier1 }, true, upload.Run);
        await cache.ResolveAsync(8, new[] { Supplier1 }, true, upload.Run);

        Assert.Equal(2, upload.Calls.Count);
        Assert.Equal(2, db.EbayPictureUploads.Count());
    }

    [Fact]
    public async Task WithoutCache_OrAfterForget_UploadsAgain_AndReplacesTheRow()
    {
        using var db = TestHelpers.NewContext();
        var cache = new EbayPictureCache(db);
        var upload = new FakeUpload();

        await cache.ResolveAsync(7, new[] { Supplier1 }, true, upload.Run);
        var noCache = await cache.ResolveAsync(7, new[] { Supplier1 }, false, upload.Run);
        Assert.Equal(2, upload.Calls.Count);
        Assert.Equal("https://i.ebayimg.com/up/2.jpg", Assert.Single(db.EbayPictureUploads).EbayUrl);
        Assert.Equal("https://i.ebayimg.com/up/2.jpg", noCache.Urls.Single());

        await cache.ForgetAsync(7, new[] { Supplier1 });
        Assert.Empty(db.EbayPictureUploads);
        await cache.ResolveAsync(7, new[] { Supplier1 }, true, upload.Run);
        Assert.Equal(3, upload.Calls.Count);
    }

    [Fact]
    public async Task FailedUpload_IsReported_AndNotRemembered()
    {
        using var db = TestHelpers.NewContext();
        var cache = new EbayPictureCache(db);

        var result = await cache.ResolveAsync(7, new[] { Supplier1, Supplier2 }, true,
            (i, url) => url == Supplier1 ? throw new Exception("Download timed out") : Task.FromResult("https://i.ebayimg.com/ok.jpg"));

        Assert.Equal(new[] { "https://i.ebayimg.com/ok.jpg" }, result.Urls);
        Assert.Contains("Download timed out", Assert.Single(result.Errors));
        Assert.Equal(EbayPictureCache.Hash(Supplier2), Assert.Single(db.EbayPictureUploads).SourceHash);
    }

    [Fact]
    public async Task HandUploadedDataUrl_IsReusedByHash_WithoutStoringTheData()
    {
        using var db = TestHelpers.NewContext();
        var cache = new EbayPictureCache(db);
        var upload = new FakeUpload();
        var dataUrl = "data:image/jpeg;base64," + new string('A', 5000);

        await cache.ResolveAsync(7, new[] { dataUrl }, true, upload.Run);
        await cache.ResolveAsync(7, new[] { dataUrl }, true, upload.Run);

        Assert.Single(upload.Calls);
        Assert.Null(Assert.Single(db.EbayPictureUploads).SourceUrl);
    }
}
