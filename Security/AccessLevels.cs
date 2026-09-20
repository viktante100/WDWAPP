using Microsoft.AspNetCore.Authorization;

namespace WDWAPP.Security;

public static class AccessLevels
{
    public const string LoggedIn = "LoggedIn";
    public const string Member = "Member";
    public const string KeyMember = "KeyMember";
    public const string Admin = "Admin";

    public static readonly string[] Roles = [Member, KeyMember, Admin];

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(LoggedIn, policy => policy.RequireAuthenticatedUser());
        options.AddPolicy(Member, policy => policy.RequireAuthenticatedUser().RequireRole(Member, KeyMember, Admin));
        options.AddPolicy(KeyMember, policy => policy.RequireAuthenticatedUser().RequireRole(KeyMember, Admin));
        options.AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().RequireRole(Admin));
    }
}
