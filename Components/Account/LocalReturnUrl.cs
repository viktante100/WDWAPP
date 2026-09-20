namespace WDWAPP.Components.Account;

internal static class LocalReturnUrl
{
    public static string Get(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] == '/'
        && (value.Length == 1 || (value[1] != '/' && value[1] != '\\'))
        && !value.Contains('\\') && !value.Any(char.IsControl)
            ? value : "/";
}
