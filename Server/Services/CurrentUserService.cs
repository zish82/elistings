using System.Security.Claims;
using Shared;

namespace Server.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

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
