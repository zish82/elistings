namespace Shared;

public static class AuthRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Lister = "Lister";

    public static readonly string[] All = [Admin, Manager, Lister];
}
