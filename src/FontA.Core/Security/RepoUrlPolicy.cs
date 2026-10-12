namespace FontA.Core.Security;

/// <summary>Decides which update addresses FontA is willing to use.</summary>
public static class RepoUrlPolicy
{
    /// <summary>
    /// Updates are only ever fetched from github.com over HTTPS, as exactly "owner/repo", with no embedded credentials
    /// and no other host, whatever the project property says.
    /// </summary>
    public static bool IsTrusted(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && u.IsDefaultPort
        && string.IsNullOrEmpty(u.UserInfo)
        && string.IsNullOrEmpty(u.Query)
        && string.IsNullOrEmpty(u.Fragment)
        && u.AbsolutePath.Trim('/').Split('/') is [{ Length: > 0 }, { Length: > 0 }];
}
