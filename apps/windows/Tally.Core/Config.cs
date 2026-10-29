using System.Text.Json;

namespace Tally;

/// <summary>
/// Where Tally signs in and which API it talks to. Read from <c>tally.json</c> beside the app, which
/// <c>make windows-settings</c> (or apps/windows/settings.ps1) writes from the deployed stack and the sign-in provider's
/// discovery document, so nothing deployment-specific lives in the code.
/// </summary>
/// <param name="Authorization">The provider's authorization endpoint, where the browser signs in.</param>
/// <param name="Token">And its token endpoint.</param>
/// <param name="SignInName">What people know the sign-in by: "Sign In to &lt;name&gt;".</param>
/// <param name="Site">The install page, where updates come from.</param>
/// <param name="Version">This build's, from the install page; null for a build from the repository.</param>
public sealed record Config(Uri Authorization, Uri Token, string ClientId, string SignInName, Uri Api, Uri? Site = null, string? Version = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Config Load(string path) =>
        JsonSerializer.Deserialize<Config>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException($"{path} is empty: run `make windows-settings` (or settings.ps1)");
}
