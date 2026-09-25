using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Pulse.Core;
using Pulse.Core.Accounts;
using Pulse.Core.Ledger;
using Pulse.Core.Notifications;
using Pulse.Core.Platform;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Pulse.Core.Refresh;
using Pulse.Providers.Devin;
using Pulse.Providers.Local;
using Pulse.Providers.Volcengine;
using Xunit;

namespace Pulse.Core.Tests;

public class ReleaseBarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pulse-release-{Guid.NewGuid():N}");

    public ReleaseBarTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Version_File_Matches_The_Msix_Identity()
    {
        var versionPath = FindRepoFile("Pulse.Win", "VERSION");
        var manifestPath = FindRepoFile("Pulse.Win", "packaging", "MSIX", "AppxManifest.xml");
        var version = ProductVersion.Read(versionPath);
        var document = XDocument.Load(manifestPath);
        var identity = document.Root!.Elements().First(element => element.Name.LocalName == "Identity");
        var manifestVersion = identity.Attribute("Version")!.Value;
        Assert.Equal(version, manifestVersion);
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void Every_Provider_Has_A_Human_Display_Name()
    {
        foreach (var id in ProviderCatalog.All)
        {
            var name = ProviderCatalog.DisplayName(id);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.DoesNotContain("ProviderId", name);
        }

        Assert.Equal("Claude Code", ProviderCatalog.DisplayName(ProviderId.ClaudeCode));
        Assert.Equal("Zhipu", ProviderCatalog.DisplayName(ProviderId.GlmCoding));
        Assert.Equal("Xiaomi Coding Plan", ProviderCatalog.DisplayName(ProviderId.XiaomiMiMo));
    }

    [Fact]
    public void Fresh_Profile_Enables_Only_Present_Paths_Or_Stored_Secrets()
    {
        var home = Path.Combine(_root, "home");
        var roaming = Path.Combine(_root, "roaming");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        Assert.True(ProviderPresence.Found(ProviderId.ClaudeCode, home, roaming, Exists));
        Assert.False(ProviderPresence.Found(ProviderId.Cursor, home, roaming, Exists));
        Assert.False(ProviderPresence.Found(ProviderId.OllamaCloud, home, roaming, _ => true));
        Assert.False(ProviderPresence.Found(ProviderId.XiaomiMiMo, home, roaming, _ => true));
        Assert.True(ProviderPresence.RequiresPastedCookie(ProviderId.OllamaCloud));
        Assert.True(ProviderPresence.RequiresPastedCookie(ProviderId.XiaomiMiMo));

        var overrides = ProviderEnablement.LoadOverrides(Path.Combine(_root, "missing.json"));
        Assert.Empty(overrides);
        Assert.True(ProviderEnablement.IsEnabled(ProviderId.ClaudeCode, overrides, pathPresent: true, secretStored: false));
        Assert.False(ProviderEnablement.IsEnabled(ProviderId.DeepSeek, overrides, pathPresent: false, secretStored: false));
        Assert.True(ProviderEnablement.IsEnabled(ProviderId.DeepSeek, overrides, pathPresent: false, secretStored: true));

        var off = new Dictionary<ProviderId, bool> { [ProviderId.ClaudeCode] = false };
        Assert.False(ProviderEnablement.IsEnabled(ProviderId.ClaudeCode, off, pathPresent: true, secretStored: true));

        var accounts = new[]
        {
            new MonitoredAccount { Provider = ProviderId.ClaudeCode, AccountId = "ClaudeCode" },
            new MonitoredAccount { Provider = ProviderId.DeepSeek, AccountId = "DeepSeek" },
        };
        var due = ProviderEnablement.DueAccounts(accounts, id => id == ProviderId.DeepSeek);
        var only = Assert.Single(due);
        Assert.Equal(ProviderId.DeepSeek, only.Provider);
    }

    [Fact]
    public void Alert_Threshold_Defaults_Off_And_Round_Trips()
    {
        var path = Path.Combine(_root, "alerts.json");
        Assert.Equal(AlertLevel.Off, AlertPreferences.Load(path));
        Assert.Empty(AlertPreferences.Thresholds(AlertPreferences.Load(path)));

        AlertPreferences.Save(path, AlertLevel.NinetyFive);
        Assert.Equal(AlertLevel.NinetyFive, AlertPreferences.Load(path));
        Assert.Equal([0.95], AlertPreferences.Thresholds(AlertLevel.NinetyFive));
    }

    [Fact]
    public void Cursor_OpenCode_Glm_And_Devin_Fixtures_Yield_Credentials()
    {
        var now = DateTimeOffset.UtcNow;
        var cursorDb = Path.Combine(_root, "state.vscdb");
        var token = Jwt(("sub", "auth0|user_abc"), ("exp", now.AddHours(2).ToUnixTimeSeconds()));
        WriteItem(cursorDb, CursorEditorLogin.TokenKey, token);
        var cookie = CursorEditorLogin.SessionCookie(cursorDb, now);
        Assert.Equal($"user_abc::{token}", cookie);

        var auth = Path.Combine(_root, "auth.json");
        File.WriteAllText(auth, """{"opencode-go":{"type":"api","key":"sk-go-fixture"}}""");
        Assert.Equal("sk-go-fixture", OpenCodeAuthFile.ReadKey(auth));

        var home = Path.Combine(_root, "glmhome");
        var keyFile = Path.Combine(home, ".config", "zhipu", "api_key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.WriteAllText(keyFile, "glm-key-line\r\nsecond\r\n");
        Assert.Equal("glm-key-line", GlmKeyFile.ReadKey(home));

        var devin = Path.Combine(_root, "devin.vscdb");
        WriteItem(devin, "windsurf.reactSettings.cachedPlanInfoData.user", """{"planName":"Pro","billingStrategy":"quota"}""");
        var plan = DevinPlanDatabase.Read(devin);
        Assert.NotNull(plan);
        Assert.Equal("Pro", plan!.PlanName);
    }

    [Fact]
    public void Volcengine_Prefers_A_Key_Pair_And_Only_Then_An_Invocable_Cli()
    {
        var called = new List<string>();
        bool CanInvoke(string path)
        {
            called.Add(path);
            return path.EndsWith("arkcli.exe", StringComparison.Ordinal);
        }

        Assert.Equal("AK:SK", VolcengineAccess.Choose("AK:SK", ["C:\\bin\\arkcli.exe"], CanInvoke));
        Assert.Empty(called);

        Assert.Equal("C:\\bin\\arkcli.exe", VolcengineAccess.Choose(null, ["C:\\missing.exe", "C:\\bin\\arkcli.exe"], CanInvoke));
        Assert.Equal("C:\\missing.exe", called[0]);
    }

    [Fact]
    public void Copilot_Token_Request_Uses_The_Device_Code_Handle()
    {
        var prompt = new Pulse.Auth.DevicePrompt(
            "USER-CODE",
            "https://github.com/login/device",
            TimeSpan.FromSeconds(5),
            DeviceCode: "device-handle");
        var fields = Pulse.Auth.GitHubDeviceLogin.TokenFields(prompt);
        Assert.Equal("device-handle", fields["device_code"]);
        Assert.NotEqual(prompt.UserCode, fields["device_code"]);
        Assert.DoesNotContain("http", fields["device_code"]);
    }

    [Fact]
    public void Pinned_Route_Does_Not_Return_The_Other_Routes_Success()
    {
        var endpoint = ProviderReadResult.Failed(ProviderReadHealth.Unauthorized, "endpoint refused");
        var alternate = ProviderReadResult.Ok(new ProviderUsage(
            ProviderId.ClaudeCode, "ClaudeCode",
            [new UsageWindow("alt", UsageWindowKind.FiveHour, null, 0.2, 5 * 3600, DateTimeOffset.UtcNow.AddHours(1))],
            DateTimeOffset.UtcNow, UsageState.Live, null, null, null, UsageRoute.StatusLine));

        var pinned = RoutePinning.Select(RoutePin.EndpointOnly, endpoint, () => alternate);
        Assert.Equal(ProviderReadHealth.Unauthorized, pinned.Health);
        Assert.Null(pinned.Usage);

        var endpointOk = ProviderReadResult.Ok(new ProviderUsage(
            ProviderId.Codex, "Codex",
            [new UsageWindow("live", UsageWindowKind.FiveHour, null, 0.4, 5 * 3600, DateTimeOffset.UtcNow.AddHours(1))],
            DateTimeOffset.UtcNow, UsageState.Live, null, null, null, UsageRoute.Endpoint));
        var pinnedAway = RoutePinning.Select(RoutePin.AlternateOnly, endpointOk, () => alternate);
        Assert.Equal(UsageRoute.StatusLine, pinnedAway.Usage!.Origin);
    }

    [Fact]
    public void Spend_Discovery_Stays_Off_Until_The_Switch_Is_On()
    {
        var calls = 0;
        var off = SpendReading.Read(false, _root, ModelPrices.Empty, null, null, _ =>
        {
            calls++;
            return true;
        });
        Assert.Equal(0, calls);
        Assert.Empty(off.Sources);

        WritePi("omp", "m-omp");
        WritePi("pi", "m-pi");
        WriteStore(Path.Combine(_root, ".local", "share", "opencode", "opencode.db"), "m-open");
        WriteStore(Path.Combine(_root, ".local", "share", "kilo", "kilo.db"), "m-kilo");
        Directory.CreateDirectory(Path.Combine(_root, ".senpi", "agent", "sessions"));
        Directory.CreateDirectory(Path.Combine(_root, ".config", "kimchi", "harness", "sessions"));
        File.WriteAllLines(Path.Combine(_root, ".senpi", "agent", "sessions", "s.jsonl"), PiLines("m-senpi"));
        File.WriteAllLines(Path.Combine(_root, ".config", "kimchi", "harness", "sessions", "s.jsonl"), PiLines("m-kimchi"));
        Directory.CreateDirectory(Path.Combine(_root, ".codebuff"));
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "antigravity", "conversations"));
        Directory.CreateDirectory(Path.Combine(_root, ".antigravity", "cache"));

        var prices = new ModelPrices(new Dictionary<string, ModelPrice>
        {
            ["m-kilo"] = new("m-kilo", "Kilo", 1, null, null, 2),
        });
        var on = SpendReading.Read(true, _root, prices, null, null);
        var ids = on.Sources.Select(source => source.SourceId).ToHashSet();
        Assert.Contains("kilo", ids);
        Assert.Contains("opencode", ids);
        Assert.Contains("omp", ids);
        Assert.Contains("pi", ids);
        Assert.Contains("senpi", ids);
        Assert.Contains("kimchi", ids);
        Assert.Contains("freebuff", ids);
        Assert.Contains("antigravity-ide", ids);
        Assert.Contains("antigravity", ids);

        var kilo = on.Sources.Single(source => source.SourceId == "kilo");
        var open = on.Sources.Single(source => source.SourceId == "opencode");
        Assert.DoesNotContain(kilo.Models, model => model.Model == "m-open");
        Assert.DoesNotContain(open.Models, model => model.Model == "m-kilo");
        Assert.DoesNotContain(on.Sources.Single(source => source.SourceId == "pi").Models, model => model.Model == "m-omp");

        var freebuff = on.Sources.Single(source => source.SourceId == "freebuff");
        Assert.True(freebuff.Recognized);
        Assert.Equal(0, freebuff.Tokens);
        Assert.Empty(freebuff.Models);

        Assert.NotEqual(
            on.Sources.Single(source => source.SourceId == "antigravity-ide").SourceId,
            on.Sources.Single(source => source.SourceId == "antigravity").SourceId);

        var priced = Assert.Single(kilo.Models);
        Assert.Equal("m-kilo", priced.Model);
        Assert.True(priced.Tokens > 0);
        Assert.NotNull(priced.PricedAmount);
        Assert.Equal(0, priced.UnpricedTokens);

        var unpriced = Assert.Single(open.Models);
        Assert.Null(unpriced.PricedAmount);
        Assert.True(unpriced.UnpricedTokens > 0);

        var narrow = SpendReading.Read(true, _root, prices, new DateOnly(1990, 1, 1), new DateOnly(1990, 1, 2));
        Assert.Equal(0, narrow.Sources.Single(source => source.SourceId == "kilo").Tokens);
    }

    [Fact]
    public void Durable_Cache_Reloads_Stale_And_Drops_Expired_Windows()
    {
        var path = Path.Combine(_root, "cache.json");
        var now = DateTimeOffset.UtcNow;
        var live = new ProviderUsage(
            ProviderId.ClaudeCode, "ClaudeCode",
            [new UsageWindow("five", UsageWindowKind.FiveHour, null, 0.4, 5 * 3600, now.AddHours(2))],
            now, UsageState.Live, "Pro", null, null, UsageRoute.Endpoint);
        DurableUsageCache.Save(path, live);
        var restored = DurableUsageCache.Load(path, now.AddMinutes(5));
        Assert.NotNull(restored);
        Assert.Equal(UsageState.Stale, restored!.State);
        Assert.Equal(0.4, restored.Windows[0].UsedFraction);

        var passed = live with
        {
            Windows = [new UsageWindow("five", UsageWindowKind.FiveHour, null, 0.4, 5 * 3600, now.AddMinutes(-1))],
        };
        DurableUsageCache.Save(path, passed);
        Assert.Null(DurableUsageCache.Load(path, now));

        var noReset = new ProviderUsage(
            ProviderId.DeepSeek, "DeepSeek",
            [new UsageWindow("balance", UsageWindowKind.Balance, null, 0.2, 0, null)],
            now.AddHours(-25), UsageState.Live, null, null, null, UsageRoute.Endpoint);
        DurableUsageCache.Save(path, noReset);
        Assert.Null(DurableUsageCache.Load(path, now));
    }

    [Fact]
    public async Task Volcengine_Read_Runs_The_Cli_Instead_Of_Signing_A_Path()
    {
        var ran = new List<string>();
        var provider = new VolcengineProvider(
            _ => null,
            () => [@"C:\bin\arkcli.exe"],
            _ => true,
            (path, _) =>
            {
                ran.Add(path);
                return Task.FromResult<string?>("""
                    {"items":[{"product":"coding-plan","subscribed":true,"periods":[{"label":"weekly","percent":40}]}]}
                    """);
            });
        var result = await provider.ReadAsync(
            new MonitoredAccount { Provider = ProviderId.Volcengine, AccountId = "Volcengine" },
            new ProviderReadContext { Now = DateTimeOffset.UtcNow, Services = EmptyServices.Instance },
            CancellationToken.None);

        Assert.Equal([@"C:\bin\arkcli.exe"], ran);
        Assert.Equal(ProviderReadHealth.Healthy, result.Health);
        Assert.Equal(UsageRoute.ArkCLI, result.Usage!.Origin);
        Assert.False(VolcengineAccess.IsKeyPair(@"C:\bin\arkcli.exe"));
    }

    [Fact]
    public async Task Devin_Read_Uses_The_Plan_Database_When_Nothing_Is_Pasted()
    {
        var database = Path.Combine(_root, "devin-read.vscdb");
        WriteItem(database, "windsurf.reactSettings.cachedPlanInfoData.user", """{"planName":"Pro","billingStrategy":"quota"}""");
        var provider = new DevinProvider(_ => null, () => database);
        var result = await provider.ReadAsync(
            new MonitoredAccount { Provider = ProviderId.Devin, AccountId = "Devin" },
            new ProviderReadContext { Now = DateTimeOffset.UtcNow, Services = EmptyServices.Instance },
            CancellationToken.None);

        Assert.Equal(ProviderReadHealth.Healthy, result.Health);
        Assert.Equal("Pro", result.Usage!.Plan);
        Assert.Equal(UsageRoute.AppCache, result.Usage.Origin);
        Assert.Empty(result.Usage.Windows);
    }

    [Fact]
    public async Task Devin_Plan_Row_Draws_The_Used_Fraction()
    {
        var context = new ProviderReadContext { Now = DateTimeOffset.UtcNow, Services = EmptyServices.Instance };
        var account = new MonitoredAccount { Provider = ProviderId.Devin, AccountId = "Devin" };

        var exact = Path.Combine(_root, "devin-exact.vscdb");
        WriteItem(exact, "windsurf.reactSettings.cachedPlanInfoData",
            """{"planName":"Pro","dailyRemainingPercent":40}""");
        var exactResult = await new DevinProvider(_ => null, () => exact).ReadAsync(account, context, CancellationToken.None);
        var exactWindow = Assert.Single(exactResult.Usage!.Windows);
        Assert.Equal("devin-daily", exactWindow.Id);
        Assert.Equal(0.6, exactWindow.UsedFraction, 5);
        Assert.Equal(UsageWindowKind.Daily, exactWindow.Kind);
        Assert.Equal(86_400, exactWindow.WindowSeconds);

        var database = Path.Combine(_root, "devin-windows.vscdb");
        WriteItem(database, "windsurf.reactSettings.cachedPlanInfoData.user",
            """{"planName":"Pro","dailyRemainingPercent":40,"weeklyRemainingPercent":75,"dailyResetAtUnix":1789372800,"totalMessages":10,"remainingMessages":4}""");
        var result = await new DevinProvider(_ => null, () => database).ReadAsync(account, context, CancellationToken.None);
        Assert.Equal(ProviderReadHealth.Healthy, result.Health);
        Assert.Equal("Pro", result.Usage!.Plan);
        Assert.Equal(UsageRoute.AppCache, result.Usage.Origin);

        var daily = Assert.Single(result.Usage.Windows, window => window.Id == "devin-daily");
        Assert.Equal(0.6, daily.UsedFraction, 5);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789372800), daily.ResetsAt);

        var weekly = Assert.Single(result.Usage.Windows, window => window.Id == "devin-weekly");
        Assert.Equal(0.25, weekly.UsedFraction, 5);
        Assert.Equal(604_800, weekly.WindowSeconds);

        var messages = Assert.Single(result.Usage.Windows, window => window.Id == "devin-messages");
        Assert.Equal(0.6, messages.UsedFraction, 5);
        Assert.Equal(UsageWindowKind.Messages, messages.Kind);
        Assert.Equal(2_592_000, messages.WindowSeconds);
        Assert.False(messages.ReportsLength);
        Assert.Null(messages.ResetsAt);

        var hidden = Path.Combine(_root, "devin-hidden.vscdb");
        WriteItem(hidden, "windsurf.settings.cachedPlanInfo",
            """{"planName":"Pro","hideDailyQuota":true,"dailyRemainingPercent":40,"hideWeeklyQuota":true,"weeklyRemainingPercent":10,"remainingMessages":-1,"totalMessages":8}""");
        var hiddenResult = await new DevinProvider(_ => null, () => hidden).ReadAsync(account, context, CancellationToken.None);
        Assert.Equal(ProviderReadHealth.Healthy, hiddenResult.Health);
        Assert.Empty(hiddenResult.Usage!.Windows);
    }

    [Fact]
    public async Task Volcengine_Default_Cli_Drains_Stderr_And_Kills_A_Hung_Child()
    {
        var exe = await FloodExecutable();
        var account = new MonitoredAccount { Provider = ProviderId.Volcengine, AccountId = "Volcengine" };
        var context = new ProviderReadContext { Now = DateTimeOffset.UtcNow, Services = EmptyServices.Instance };

        Environment.SetEnvironmentVariable(VolcFixture, "ok");
        try
        {
            var provider = new VolcengineProvider(_ => null, () => [exe], path => path == exe);
            var okRead = provider.ReadAsync(account, context, CancellationToken.None);
            var okDone = await Task.WhenAny(okRead, Task.Delay(TimeSpan.FromSeconds(20)));
            if (okDone != okRead)
            {
                StopFlood();
                Assert.Fail("default CLI runner did not return stdout");
            }
            var ok = await okRead;
            Assert.Equal(ProviderReadHealth.Healthy, ok.Health);
            Assert.Equal(UsageRoute.ArkCLI, ok.Usage!.Origin);
            Assert.Equal(0.4, ok.Usage.Windows[0].UsedFraction, 5);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VolcFixture, null);
            StopFlood();
        }

        var hung = new VolcengineProvider(_ => null, () => [exe], path => path == exe);
        var started = Environment.TickCount64;
        var read = hung.ReadAsync(account, context, CancellationToken.None);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(25)));
        StopFlood();
        if (finished != read)
            Assert.Fail("default CLI runner did not return");
        var result = await read;
        var elapsed = Environment.TickCount64 - started;
        Assert.InRange(elapsed, 10_000, 22_000);
        Assert.Equal(ProviderReadHealth.HelperNotRunning, result.Health);
    }

    [Fact]
    public void Saved_Off_Switch_Stays_Off()
    {
        var path = Path.Combine(_root, "enablement.json");
        ProviderEnablement.SaveOverrides(path, new Dictionary<ProviderId, bool> { [ProviderId.ClaudeCode] = false });
        var loaded = ProviderEnablement.LoadOverrides(path);
        Assert.False(ProviderEnablement.IsEnabled(ProviderId.ClaudeCode, loaded, pathPresent: true, secretStored: true));
    }

    [Fact]
    public async Task Refresh_Reloads_The_Durable_Cache_After_Restart()
    {
        var directory = Path.Combine(_root, "usage-cache");
        var account = new MonitoredAccount { Provider = ProviderId.KimiCode, AccountId = "kimi" };
        var provider = new OnceProvider();
        await using var first = new RefreshEngine(
            _ => provider, _ => new AdaptiveRefresh.Signals { PanelVisible = true }, new QuietLog(), cacheDirectory: directory);
        first.Schedule(account);
        await first.RunDueAsync([account], CancellationToken.None);

        provider.Fail = true;
        await using var second = new RefreshEngine(
            _ => provider, _ => new AdaptiveRefresh.Signals { PanelVisible = true }, new QuietLog(), cacheDirectory: directory);
        var results = new List<ProviderReadResult>();
        second.ReadingChanged += (_, result) => results.Add(result);
        second.Schedule(account);
        await second.RunDueAsync([account], CancellationToken.None);

        var restored = Assert.Single(results);
        Assert.Equal(UsageState.Stale, restored.Usage!.State);
        Assert.Equal(0.4, restored.Usage.Windows[0].UsedFraction);
    }

    [Fact]
    public void Update_Check_Selects_The_Stable_Tag_From_Release_Json()
    {
        const string json = """
            [
              {"tag_name":"windows-v1.3.0-beta.1"},
              {"tag_name":"windows-v1.2.0"},
              {"tag_name":"v9.0.0"}
            ]
            """;
        Assert.Equal("windows-v1.2.0", UpdateSelection.ChooseFromReleaseJson(json));
    }

    [Fact]
    public void Absurdly_Long_Tags_Are_Not_Release_Tags()
    {
        // The tag text ends up in tray copy; something enormous was never one
        // of ours and must not be offered or shown.
        Assert.False(UpdateSelection.TryParseStable("windows-v" + new string('1', 500), out _));
        Assert.False(UpdateSelection.IsNewer("0.9.0.0", "windows-v" + new string('1', 500)));
        Assert.True(UpdateSelection.IsNewer("0.9.0.0", "windows-v1.0.0"));
    }

    [Fact]
    public void Older_Stable_Tag_Stays_Quiet()
    {
        const string olderOnly = """[{"tag_name":"windows-v0.2.0"}]""";
        var selected = UpdateSelection.ChooseFromReleaseJson(olderOnly);
        Assert.Equal("windows-v0.2.0", selected);
        // The same pair CheckForUpdateAsync passes to IsNewer.
        Assert.False(UpdateSelection.IsNewer("0.9.0.0", selected));
        Assert.False(UpdateSelection.IsNewer("0.9.0.0", "windows-v0.9.0"));
        Assert.False(UpdateSelection.IsNewer("0.9.0.0", "windows-v0.9.0.0"));

        const string newer = """
            [
              {"tag_name":"windows-v1.1.0-beta.1"},
              {"tag_name":"windows-v1.0.0"}
            ]
            """;
        Assert.Equal("windows-v1.0.0", UpdateSelection.ChooseFromReleaseJson(newer));
        Assert.True(UpdateSelection.IsNewer("0.9.0.0", UpdateSelection.ChooseFromReleaseJson(newer)));
    }

    private sealed class OnceProvider : IUsageProvider
    {
        public bool Fail { get; set; }
        public ProviderId Id => ProviderId.KimiCode;
        public ProviderCapabilities Capabilities => ProviderCapabilities.For(Id);
        public Task<ProviderReadResult> ReadAsync(MonitoredAccount account, ProviderReadContext context, CancellationToken cancellationToken)
        {
            if (Fail)
                return Task.FromResult(ProviderReadResult.Failed(ProviderReadHealth.ProviderUnavailable, "down"));
            var window = new UsageWindow("kimi", UsageWindowKind.Weekly, null, 0.4, 7 * 86400, context.Now.AddDays(3));
            return Task.FromResult(ProviderReadResult.Ok(new ProviderUsage(
                Id, account.AccountId, [window], context.Now, UsageState.Live, null, null, null, UsageRoute.Endpoint)));
        }
    }

    private sealed class QuietLog : ILogger
    {
        public void Log(string message) { }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void Update_Selection_Ignores_A_Newer_Prerelease()
    {
        var selected = UpdateSelection.SelectStable(
        [
            "windows-v1.3.0-beta.1",
            "windows-v1.2.0",
            "v1.9.0",
            "windows-v1.0.0",
        ]);
        Assert.Equal("windows-v1.2.0", selected);
    }

    private void WritePi(string client, string model)
    {
        var dir = client switch
        {
            "pi" => Path.Combine(_root, ".pi", "agent", "sessions"),
            "omp" => Path.Combine(_root, ".omp", "agent", "sessions"),
            _ => Path.Combine(_root, client),
        };
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "s.jsonl"), PiLines(model));
    }

    private static string[] PiLines(string model) =>
    [
        """{"type":"session","id":"s1","cwd":"E:/Code/MyTrace"}""",
        "{\"type\":\"message\",\"id\":\"row-1\",\"timestamp\":\"2026-09-21T10:00:00Z\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r1\",\"model\":\"" + model + "\",\"provider\":\"p\",\"usage\":{\"input\":10,\"output\":4,\"cacheRead\":0,\"cacheWrite\":0}}}",
    ];

    private static void WriteStore(string path, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE session (id TEXT PRIMARY KEY, slug TEXT, title TEXT, directory TEXT);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT);
            """;
        schema.ExecuteNonQuery();
        using var session = connection.CreateCommand();
        session.CommandText = "INSERT INTO session (id, slug, title, directory) VALUES ('s', 'slug', 'title', 'E:/work')";
        session.ExecuteNonQuery();
        using var message = connection.CreateCommand();
        message.CommandText = "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('m', 's', 0, 0, $data)";
        message.Parameters.AddWithValue("$data",
            "{\"role\":\"assistant\",\"modelID\":\"" + model + "\",\"time\":{\"created\":1789981200000}," +
            "\"tokens\":{\"input\":100,\"output\":20,\"reasoning\":0,\"cache\":{\"write\":0,\"read\":0}}}");
        message.ExecuteNonQuery();
    }

    private const string VolcFixture = "PULSE_WIN_VOLC_FIXTURE";
    private static readonly SemaphoreSlim FloodBuild = new(1, 1);
    private static string? FloodExe;

    /// <summary>
    /// A real child the default <c>RunCli</c> starts. It fills stderr past a
    /// pipe buffer, then either prints a plan and exits or sleeps until killed.
    /// </summary>
    private static async Task<string> FloodExecutable()
    {
        await FloodBuild.WaitAsync();
        try
        {
            if (FloodExe is not null && File.Exists(FloodExe)) return FloodExe;
            var dir = Path.Combine(Path.GetTempPath(), "pulse-volc-flood");
            Directory.CreateDirectory(dir);
            var csproj = Path.Combine(dir, "Flood.csproj");
            var code = Path.Combine(dir, "Program.cs");
            await File.WriteAllTextAsync(csproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <Nullable>disable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(code, """
                using System;
                using System.Threading;
                internal static class Program
                {
                    private static void Main()
                    {
                        Console.Error.Write(new string('e', 256 * 1024));
                        Console.Error.Flush();
                        if (Environment.GetEnvironmentVariable("PULSE_WIN_VOLC_FIXTURE") == "ok")
                        {
                            Console.Write("{\"items\":[{\"product\":\"coding-plan\",\"subscribed\":true,\"periods\":[{\"label\":\"weekly\",\"percent\":40}]}]}");
                            return;
                        }
                        Thread.Sleep(Timeout.Infinite);
                    }
                }
                """);
            var exe = Path.Combine(dir, "bin", "Release", "net10.0", "Flood.exe");
            var start = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{csproj}\" -c Release -v q --nologo",
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var build = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start");
            var output = build.StandardOutput.ReadToEndAsync();
            var error = build.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, error);
            using var buildDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await build.WaitForExitAsync(buildDeadline.Token);
            if (build.ExitCode != 0 || !File.Exists(exe))
                throw new InvalidOperationException((await output) + (await error));
            FloodExe = exe;
            return exe;
        }
        finally
        {
            FloodBuild.Release();
        }
    }

    private static void StopFlood()
    {
        foreach (var process in Process.GetProcessesByName("Flood"))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception) { }
            process.Dispose();
        }
    }

    private static void WriteItem(string database, string key, string value)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ItemTable (key TEXT PRIMARY KEY, value TEXT);
            INSERT INTO ItemTable (key, value) VALUES ($key, $value);
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string Jwt(params (string Name, object Value)[] claims)
    {
        var payload = JsonSerializer.Serialize(claims.ToDictionary(claim => claim.Name, claim => claim.Value));
        static string Encode(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode("{\"alg\":\"none\"}")}.{Encode(payload)}.sig";
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
