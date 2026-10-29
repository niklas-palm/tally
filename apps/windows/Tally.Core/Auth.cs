using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tally;

public enum AuthState { SignedOut, SigningIn, SignedIn }

/// <summary>
/// Signing in to the OIDC provider, once: authorization code with PKCE in the system's browser, which comes back at
/// once when the browser is already signed in there. The refresh token is kept in the platform's secret store and
/// renews the short-lived access tokens for as long as the provider's session lasts.
/// </summary>
public sealed class Auth : ObservableObject
{
    public const string Redirect = "tally://callback";
    private const string Secret = "refresh-token";

    private readonly Config config;
    private readonly ISecrets secrets;
    private readonly IBrowser browser;
    private readonly HttpClient http;
    private string? refreshToken;
    private (string Value, DateTimeOffset Expires)? accessToken;
    private Task<string?>? refreshing;  // one renewal at a time: every caller awaits the same one
    private int session;  // counts sign-outs: an answer that comes back after one is dropped
    private int attempts;  // counts Sign Ins: one a newer one replaced leaves the state to it

    public Auth(Config config, ISecrets secrets, IBrowser browser, HttpClient http)
    {
        (this.config, this.secrets, this.browser, this.http) = (config, secrets, browser, http);
        refreshToken = secrets.Load(Secret) is { } saved ? Encoding.UTF8.GetString(saved) : null;
        if (refreshToken != null) State = AuthState.SignedIn;
    }

    public AuthState State { get; private set => SetProperty(ref field, value); }

    /// <summary>Signed out because the session ended, not by choice.</summary>
    public bool Expired { get; private set => SetProperty(ref field, value); }

    /// <summary>Who is signed in, as their token names them: known once a token has come.</summary>
    public string? Account { get; private set => SetProperty(ref field, value); }

    /// <summary>Their id with the provider: what the meetings folder setting belongs to.</summary>
    public string? Subject { get; private set => SetProperty(ref field, value); }

    /// <summary>Signs in in the browser. A sign-in abandoned there never returns, so Sign In again replaces it. Throws
    /// <see cref="OperationCanceledException"/> when replaced or the browser closed, <see cref="AppError"/> when it
    /// fails.</summary>
    public async Task SignIn()
    {
        var attempt = ++attempts;
        State = AuthState.SigningIn;
        try
        {
            var verifier = RandomUrlSafe(32);
            var nonce = RandomUrlSafe(16);
            var callback = await browser.Authorize(AuthorizeUrl(config, Challenge(verifier), nonce))
                ?? throw new OperationCanceledException("Sign-in was cancelled.");
            var query = HttpUtility.ParseQueryString(callback.Query);
            if (query["state"] != nonce || query["code"] is not { } code) throw new AppError("Sign-in didn't complete. Try again.");
            try
            {
                await RequestTokens(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier });
            }
            catch (SessionEnded)  // the code expired or was used already
            {
                throw new AppError("Sign-in didn't complete. Try again.");
            }
            Expired = false;
            State = AuthState.SignedIn;
        }
        finally
        {
            if (attempt == attempts && State == AuthState.SigningIn) State = AuthState.SignedOut;
        }
    }

    internal static Uri AuthorizeUrl(Config config, string challenge, string state)
    {
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId, ["response_type"] = "code", ["redirect_uri"] = Redirect, ["scope"] = "openid",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new(config.Authorization.AbsoluteUri + (config.Authorization.Query.Length > 0 ? "&" : "?") + query);
    }

    public void SignOut()
    {
        session++;
        refreshing = null;
        refreshToken = null;
        accessToken = null;
        Account = null;
        Subject = null;
        secrets.Delete(Secret);
        State = AuthState.SignedOut;
    }

    /// <summary>A valid access token, renewed when it's about to expire. Null when signed out, offline, or when the
    /// session has ended (then the state turns to signed out).</summary>
    public async Task<string?> Token()
    {
        if (accessToken is { } access && access.Expires > DateTimeOffset.UtcNow.AddSeconds(30)) return access.Value;
        if (refreshing != null) return await refreshing;
        if (refreshToken is not { } refresh) return null;
        var task = Renew(refresh);
        refreshing = task;
        try
        {
            return await task;
        }
        finally
        {
            refreshing = null;
        }
    }

    private async Task<string?> Renew(string refresh)
    {
        try
        {
            await RequestTokens(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh });
            return accessToken?.Value;
        }
        catch (SessionEnded)
        {
            if (refreshToken != refresh) return null;  // only the session that ended
            SignOut();
            Expired = true;
            return null;
        }
        catch (Exception)
        {
            return null;  // offline: try again later
        }
    }

    private sealed class SessionEnded : Exception;

    private async Task RequestTokens(Dictionary<string, string> form)
    {
        var session = this.session;
        form.TryAdd("client_id", config.ClientId);
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Token) { Content = new FormUrlEncodedContent(form) };
        int code;
        byte[] data;
        try
        {
            (code, data) = await Http.Send(http, request, Http.Answer);
        }
        catch (Api.Offline)
        {
            throw new AppError("You're offline, so Tally couldn't sign in. Try again when you're back online.");
        }
        var answer = Read<TokenAnswer>(data);
        if (code != 200 || answer?.AccessToken is not { } access)
        {
            if (answer?.Error == "invalid_grant") throw new SessionEnded();
            throw new AppError($"Couldn't sign in to {config.SignInName}. Try again in a moment.");
        }
        if (session != this.session) return;  // signed out while this was on its way: keep nothing
        accessToken = (access, DateTimeOffset.UtcNow.AddSeconds(answer.ExpiresIn ?? 300));
        var claims = ClaimsOf(access);
        Account = claims?.Name ?? claims?.Email ?? claims?.PreferredUsername ?? claims?.Username;  // username: Cognito's
        Subject = claims?.Sub;
        if (answer.RefreshToken is { } newRefresh)
        {
            refreshToken = newRefresh;
            secrets.Save(Secret, Encoding.UTF8.GetBytes(newRefresh));
        }
    }

    /// <summary>The token endpoint's answer, or its error.</summary>
    private sealed record TokenAnswer(string? AccessToken, string? RefreshToken, double? ExpiresIn, string? Error);

    /// <summary>Who an access token is for, read for showing only: the API checks the token itself.</summary>
    private sealed record Claims(string? Sub, string? Name, string? Email, string? PreferredUsername, string? Username);

    private static Claims? ClaimsOf(string token)
    {
        try
        {
            return token.Split('.') is [_, var payload, _] ? Read<Claims>(Base64Url.DecodeFromChars(payload)) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The S256 challenge (RFC 7636): unpadded base64url of the verifier's SHA-256.</summary>
    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    private static string RandomUrlSafe(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    private static T? Read<T>(byte[] json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Api.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>An error with a message a person can read.</summary>
public sealed class AppError(string message) : Exception(message);
