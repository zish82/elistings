namespace Shared;

public class UpdateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = AuthRoles.Lister;
    public bool IsActive { get; set; } = true;
    public string? Password { get; set; }
}
