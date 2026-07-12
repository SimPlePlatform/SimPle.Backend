namespace SimPle.Application.Common.Options;

/// <summary>
/// The server key used to compute keyed digests of lobby join codes and link tokens.
///
/// This is a secret and, like <c>Jwt:SecretKey</c> and <c>Recaptcha:SecretKey</c>, must be configured
/// <em>outside</em> committed appsettings (environment variable <c>LobbyCredential__Key</c>, user-secrets, or the
/// deployment's secret store). Startup validation fails closed when it is absent or a placeholder, so the module
/// can never silently fall back to an unkeyed or well-known digest — which would make every join code in the
/// database offline-guessable.
/// </summary>
public sealed class LobbyCredentialOptions
{
    public const string SectionName = "LobbyCredential";

    /// <summary>At least 32 characters, matching the JWT key's floor.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The region a lobby/ticket falls back to when neither the request nor the user's profile names an
    /// allow-listed one. Must itself be allow-listed (<c>LobbyAllowLists.Regions</c>).
    /// </summary>
    public string DefaultRegion { get; set; } = "eu-west";
}
