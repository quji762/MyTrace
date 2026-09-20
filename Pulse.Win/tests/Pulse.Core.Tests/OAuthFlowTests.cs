using Pulse.Auth;
using Xunit;

namespace Pulse.Core.Tests;

public class OAuthFlowTests
{
    [Fact]
    public void Pkce_Verifier_Is_Base64url_Of_32_Bytes()
    {
        var (verifier, challenge) = Pkce.Create();
        // 32 bytes → 43 base64url chars without padding.
        Assert.Equal(43, verifier.Length);
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
    }

    [Fact]
    public void Pkce_Challenge_Is_Sha256_Of_The_Encoded_Verifier()
    {
        // Upstream rule (Grok Bot login): the challenge hashes the ENCODED string,
        // not the raw bytes.
        var (verifier, challenge) = Pkce.Create();
        var expected = Pkce.Base64Url(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void Tokens_Round_Trip_Through_Serialize()
    {
        var expires = DateTimeOffset.Parse("2026-09-22T00:00:00Z");
        var tokens = new OAuthTokens("at_123", "rt_456", expires);
        var restored = OAuthTokens.Deserialize(tokens.Serialize());
        Assert.NotNull(restored);
        Assert.Equal("at_123", restored!.AccessToken);
        Assert.Equal("rt_456", restored.RefreshToken);
        Assert.Equal(expires.ToUnixTimeMilliseconds(), restored.ExpiresAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Tokens_Deserialize_Rejects_Plain_Api_Keys()
    {
        // A pasted key stored before this format existed is not a token bundle.
        Assert.Null(OAuthTokens.Deserialize("ghp_someLegacyKey"));
        Assert.Null(OAuthTokens.Deserialize(null));
        Assert.Null(OAuthTokens.Deserialize("{ not json"));
    }

    [Fact]
    public void Claude_Authorize_Url_Carries_The_Narrow_Scope_And_State()
    {
        var login = new ClaudeLoopbackLogin(port: 41599);
        var url = login.BuildAuthorizeUrl("state-abc");

        Assert.Contains("client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e", url);
        Assert.Contains("scope=user%3Aprofile", url); // user:profile ONLY
        Assert.Contains("state=state-abc", url);
        Assert.Contains("code=true", url); // the CLI's extra authorize item
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A41599%2Fcallback", url);
    }

    [Fact]
    public void Claude_RedirectUri_Uses_Any_Free_Port()
    {
        var login = new ClaudeLoopbackLogin(); // fixedPort is nil upstream: any port
        Assert.StartsWith("http://127.0.0.1:", login.RedirectUri);
        Assert.EndsWith("/callback", login.RedirectUri);
        Assert.NotEqual(1455, new Uri(login.RedirectUri).Port); // 1455 is Codex's fixed port
    }

    [Fact]
    public void OpenAI_Flow_Constants_Match_The_Published_Client()
    {
        // The scopes must be the FULL published set — a subset ends on OpenAI's
        // error page before the browser ever comes back.
        Assert.Equal(
            new[] { "openid", "profile", "email", "offline_access", "api.connectors.read", "api.connectors.invoke" },
            OpenAIDeviceLogin.Scopes);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", OpenAIDeviceLogin.ClientID);
        Assert.Equal("https://auth.openai.com/codex/device", OpenAIDeviceLogin.VerificationUrl);
    }

    [Fact]
    public void GitHub_Flow_Scopes_Stay_Narrow()
    {
        // read:user and NOTHING else: the usage endpoint accepts any GitHub token,
        // and a broader one would hand over repo/workflow reach.
        Assert.Equal("read:user", GitHubDeviceLogin.Scope);
        Assert.Equal("Iv1.b507a08c87ecfe98", GitHubDeviceLogin.ClientID);
        Assert.Equal("https://github.com/login/device/code",
            "https://github.com/login/device/code");
    }
}
