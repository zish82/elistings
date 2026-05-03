using Shared;

namespace Server.Services;

public interface ICurrentUserService
{
    bool IsAuthenticated { get; }
    int? UserId { get; }
    string? Email { get; }
    string? Role { get; }
    bool CanManageUsers { get; }
    bool CanViewAllListings { get; }
    CurrentUserDto? GetCurrentUserDto();
}
