using System.Text;
using System.Web;

namespace Tally.Tests;

/// <summary>Signing in to the provider (docs/macos.md, "Signing in"): PKCE, the code exchange, and keeping the session.</summary>
public class AuthTests
{
    private static readonly Config Config = new(new Uri("https://auth.test/authorize"), new Uri(FakeHttp.Token), "tally", "Example", new Uri(FakeHttp.Api));
    private readonly FakeHttp http = new();
    private readonly FakePlatform platform = new();

    private Auth Make() => new(Config, platform, platform, new HttpClient(http));

    /// <summary>The S256 challenge (RFC 7636): unpadded base64url of the verifier's SHA-256, as Python's hashlib computes it.</summary>
    [Fact]
    public void The_pkce_challenge_is_rfc_7636s()
    {
        Assert.Equal("6zNL96ixlMmIwLrFpFfwC_r94rmpboqWEbu-UNx0Ya4", Auth.Challenge("dBjftJeZ4CVP-mJ92K9-cdEwQUBOTfmMt4o5aH2Z1Gw"));
    }

    /// <summary>Who's signed in, as their access token names them; a token that isn't a JWT names nobody.</summary>
    private async Task<string?> AccountFor(string accessToken)
    {
        platform.Secrets["refresh-token"] = "refresh-1"u8.ToArray();
        http.On($"POST {FakeHttp.Token}", 200, $$"""{"access_token": "{{accessToken}}", "expires_in": 300}""");
        var auth = Make();
        Assert.Equal(accessToken, await auth.Token());
        return auth.Account;
    }

    [Fact]
    public async Task The_account_is_named_from_its_token()
    {
        Assert.Equal("Ada Lovelace", await AccountFor(FakeHttp.Jwt("""{"sub": "x", "name": "Ada Lovelace", "email": "ada@example.com"}""")));
        Assert.Null(await AccountFor("not a token"));
    }

    [Fact]
    public async Task A_Cognito_account_is_named_by_its_username()
    {
        Assert.Equal("ada@example.com", await AccountFor(FakeHttp.Jwt("""{"sub": "x", "username": "ada@example.com"}""")));
    }

    [Fact]
    public void The_authorize_url_is_the_providers_own_with_any_query_it_has()
    {
        var config = new Config(new Uri("https://login.example.com/oauth2/v2.0/authorize?p=signin"), new Uri("https://login.example.com/token"), "tally", "Example", new Uri("https://api.test"));
        var url = Auth.AuthorizeUrl(config, "challenge", "state");
        Assert.StartsWith("https://login.example.com/oauth2/v2.0/authorize?p=signin&client_id=tally&", url.AbsoluteUri);
    }

    [Fact]
    public async Task Signing_in_sends_the_browser_with_a_challenge_and_exchanges_the_code_for_its_verifier()
    {
        Uri? opened = null;
        platform.Authorize = url =>
        {
            opened = url;
            return new Uri($"tally://callback?code=the-code&state={HttpUtility.ParseQueryString(url.Query)["state"]}");
        };
        http.SignedIn("user-1", "Ada Lovelace");
        var auth = Make();
        await auth.SignIn();

        Assert.StartsWith("https://auth.test/authorize?", opened!.AbsoluteUri);
        var asked = HttpUtility.ParseQueryString(opened.Query);
        Assert.Equal(("tally", "code", "tally://callback", "openid", "S256"),
            (asked["client_id"], asked["response_type"], asked["redirect_uri"], asked["scope"], asked["code_challenge_method"]));
        var exchange = HttpUtility.ParseQueryString(Assert.Single(http.Requests).Body);
        Assert.Equal(("authorization_code", "the-code", "tally://callback", "tally"),
            (exchange["grant_type"], exchange["code"], exchange["redirect_uri"], exchange["client_id"]));
        Assert.Equal(asked["code_challenge"], Auth.Challenge(exchange["code_verifier"]!));

        Assert.Equal(AuthState.SignedIn, auth.State);
        Assert.Equal(("Ada Lovelace", "user-1"), (auth.Account, auth.Subject));
        Assert.Equal("refresh-2", Encoding.UTF8.GetString(platform.Secrets["refresh-token"]));
        Assert.NotNull(await auth.Token());
        Assert.Single(http.Requests);  // the access token is kept, not fetched again
    }

    [Fact]
    public async Task A_callback_for_another_sign_in_or_a_closed_browser_signs_nobody_in()
    {
        var auth = Make();
        platform.Authorize = _ => new Uri("tally://callback?code=c&state=someone-elses");
        Assert.Equal("Sign-in didn't complete. Try again.", (await Assert.ThrowsAsync<AppError>(auth.SignIn)).Message);
        platform.Authorize = _ => null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(auth.SignIn);
        Assert.Equal(AuthState.SignedOut, auth.State);
        Assert.Empty(http.Requests);
    }

    /// <summary>A tab closed or "Open Tally?" declined never comes back: Sign In again replaces that sign-in.</summary>
    [Fact]
    public async Task A_sign_in_abandoned_in_the_browser_gives_way_to_the_next()
    {
        var browser = new Tabs();
        http.SignedIn();
        var auth = new Auth(Config, platform, browser, new HttpClient(http));
        var abandoned = auth.SignIn();
        var again = auth.SignIn();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal(AuthState.SigningIn, auth.State);  // the first's end leaves the second be

        browser.Return();
        await again;
        Assert.Equal(AuthState.SignedIn, auth.State);
    }

    /// <summary>The browser as Windows has it: a new sign-in's tab replaces the one before, whose wait ends with nothing.</summary>
    private sealed class Tabs : IBrowser
    {
        private TaskCompletionSource<Uri?>? waiting;
        private Uri? opened;

        public Task<Uri?> Authorize(Uri url)
        {
            waiting?.TrySetResult(null);
            (opened, waiting) = (url, new());
            return waiting.Task;
        }

        public void Return() => waiting!.TrySetResult(new Uri($"tally://callback?code=c&state={HttpUtility.ParseQueryString(opened!.Query)["state"]}"));
    }

    [Fact]
    public async Task The_access_token_is_renewed_thirty_seconds_before_it_expires()
    {
        platform.Secrets["refresh-token"] = "refresh-1"u8.ToArray();
        http.SignedIn(expiresIn: 29);
        var auth = Make();
        Assert.Equal(AuthState.SignedIn, auth.State);  // from the secret store, before any token
        await auth.Token();
        await auth.Token();
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("refresh-1", HttpUtility.ParseQueryString(http.Requests.First().Body)["refresh_token"]);
        Assert.Equal("refresh-2", HttpUtility.ParseQueryString(http.Requests.Last().Body)["refresh_token"]);  // the newer one, kept
    }

    [Fact]
    public async Task A_refused_refresh_ends_the_session_but_being_offline_doesnt()
    {
        platform.Secrets["refresh-token"] = "refresh-1"u8.ToArray();
        var auth = Make();
        http.Offline = true;
        Assert.Null(await auth.Token());
        Assert.Equal(AuthState.SignedIn, auth.State);
        Assert.False(auth.Expired);

        http.Offline = false;
        http.On($"POST {FakeHttp.Token}", 400, """{"error": "invalid_grant", "error_description": "Session not active"}""");
        Assert.Null(await auth.Token());
        Assert.Equal(AuthState.SignedOut, auth.State);
        Assert.True(auth.Expired);
        Assert.Empty(platform.Secrets);
    }
}
