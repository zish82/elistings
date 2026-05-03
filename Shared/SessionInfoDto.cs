namespace Shared;

public class SessionInfoDto
{
    public bool IsAuthenticated { get; set; }
    public bool HasUsers { get; set; }
    public CurrentUserDto? User { get; set; }
}
