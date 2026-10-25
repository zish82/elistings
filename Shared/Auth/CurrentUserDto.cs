namespace Shared;

public class CurrentUserDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = AuthRoles.Lister;
    public bool CanManageUsers { get; set; }
    public bool CanViewAllListings { get; set; }
}
