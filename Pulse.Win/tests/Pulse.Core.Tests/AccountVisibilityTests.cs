using Pulse.Core.Accounts;
using Pulse.Core.Platform;
using Pulse.Core.Providers;
using Xunit;

namespace Pulse.Core.Tests;

public class AccountVisibilityTests
{
    private static string TempPath() => Path.Combine(
        Path.GetTempPath(), $"pulse-visibility-{Guid.NewGuid():N}.json");

    [Fact]
    public void Unknown_Slots_Are_Visible_By_Default()
    {
        var path = TempPath();
        Assert.True(AccountVisibility.IsVisible(ProviderId.Codex, "Codex#abc12345", path));
        Assert.True(AccountVisibility.IsVisible(ProviderId.Codex, "Codex", path)); // no file at all
    }

    [Fact]
    public void Hidden_Stays_Hidden_Others_Stay_Visible()
    {
        var path = TempPath();
        AccountVisibility.Set(ProviderId.Codex, "Codex#a", visible: false, path);
        AccountVisibility.Set(ProviderId.Codex, "Codex#b", visible: true, path);

        Assert.False(AccountVisibility.IsVisible(ProviderId.Codex, "Codex#a", path));
        Assert.True(AccountVisibility.IsVisible(ProviderId.Codex, "Codex#b", path));
        // A different provider's same-named slot is unaffected: keys are per account.
        Assert.True(AccountVisibility.IsVisible(ProviderId.ClaudeCode, "Codex#a", path));
    }

    [Fact]
    public void Set_Is_Idempotent_And_File_Is_Valid_Json()
    {
        var path = TempPath();
        AccountVisibility.Set(ProviderId.Grok, "Grok#1", false, path);
        AccountVisibility.Set(ProviderId.Grok, "Grok#1", false, path);
        Assert.False(AccountVisibility.IsVisible(ProviderId.Grok, "Grok#1", path));
    }
}

public class JwtIdentityTests
{
    private static string Jwt(object claims)
    {
        var payload = Convert.ToBase64String(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(claims))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"eyJhbGciOiJIUzI1NiJ9.{payload}.sig";
    }

    [Fact]
    public void Email_Claim_Is_Read_From_The_Payload()
    {
        Assert.Equal("a@b.c", JwtIdentity.Email(Jwt(new { email = "a@b.c", sub = "1" })));
        // Unpadded base64url payloads decode the same way.
        Assert.Equal("a@b.c", JwtIdentity.Email(Jwt(new { email = "a@b.c" })));
    }

    [Fact]
    public void Missing_Or_Invalid_Tokens_Are_Null_Not_Throws()
    {
        Assert.Null(JwtIdentity.Email(null));
        Assert.Null(JwtIdentity.Email(""));
        Assert.Null(JwtIdentity.Email("not-a-jwt"));
        Assert.Null(JwtIdentity.Email("header.@@invalid@@.sig"));
        Assert.Null(JwtIdentity.Email(Jwt(new { sub = "1" })));
    }
}
