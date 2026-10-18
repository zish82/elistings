using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Server.Data;
using Server.Services;
using Shared;

namespace Server.Tests;

/// <summary>Shared builders for tests: in-memory database, fake users, seeded suppliers.</summary>
internal static class TestHelpers
{
    /// <summary>A fresh, isolated in-memory database per test.</summary>
    public static AppDbContext NewContext()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"test-{Guid.NewGuid():N}")
            .Options);

    public static ICurrentUserService User(int userId = 1, string role = AuthRoles.Admin)
    {
        var isAdmin = role == AuthRoles.Admin;
        var canViewAll = isAdmin || role == AuthRoles.Manager;
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(x => x.IsAuthenticated).Returns(true);
        user.SetupGet(x => x.UserId).Returns(userId);
        user.SetupGet(x => x.Email).Returns("tester@example.com");
        user.SetupGet(x => x.Role).Returns(role);
        user.SetupGet(x => x.CanManageUsers).Returns(isAdmin);
        user.SetupGet(x => x.CanViewAllListings).Returns(canViewAll);
        return user.Object;
    }

    /// <summary>Gives a controller an HttpContext with a signed-in principal (needed by ControllerBase.User).</summary>
    public static T WithHttpContext<T>(this T controller, int userId = 1) where T : ControllerBase
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    public static Supplier AddSupplier(AppDbContext ctx, string code = "DropShipMall", bool pricesIncludeVat = false,
        string? skuPrefix = "DSM", string? host = "dropshipmall.co.uk")
    {
        var supplier = new Supplier
        {
            Code = code,
            Name = code,
            WebsiteHost = host,
            SkuPrefix = skuPrefix,
            PricesIncludeVat = pricesIncludeVat,
            VatRatePercent = 20m,
            DefaultMarginPercent = 20m,
            IsActive = true
        };
        ctx.Suppliers.Add(supplier);
        ctx.SaveChanges();
        return supplier;
    }

    public static int StatusCodeOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}")
    };
}
