using System.Security.Claims;
using Shared;

namespace Server.Services;

/// <summary>
/// Scoped holder that lets background jobs run "as" a user, since there is no HttpContext there.
/// </summary>
public class BackgroundUserContext
{
    public ClaimsPrincipal? User { get; set; }
}

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly BackgroundUserContext _backgroundUser;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor, BackgroundUserContext backgroundUser)
    {
        _httpContextAccessor = httpContextAccessor;
        _backgroundUser = backgroundUser;
    }

    private ClaimsPrincipal? User => _backgroundUser.User ?? _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public int? UserId
    {
        get
        {
            var raw = User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? Email => User?.FindFirstValue(ClaimTypes.Email);
    public string? Role => User?.FindFirstValue(ClaimTypes.Role);
    public bool CanManageUsers => string.Equals(Role, AuthRoles.Admin, StringComparison.OrdinalIgnoreCase);
    public bool CanViewAllListings => string.Equals(Role, AuthRoles.Admin, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(Role, AuthRoles.Manager, StringComparison.OrdinalIgnoreCase);

    public CurrentUserDto? GetCurrentUserDto()
    {
        if (!IsAuthenticated || UserId == null || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Role))
        {
            return null;
        }

        return new CurrentUserDto
        {
            Id = UserId.Value,
            Email = Email,
            Role = Role,
            CanManageUsers = CanManageUsers,
            CanViewAllListings = CanViewAllListings
        };
    }
}
