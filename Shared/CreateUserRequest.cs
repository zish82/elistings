namespace Shared;

public class CreateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = AuthRoles.Lister;
    public bool IsActive { get; set; } = true;
}
