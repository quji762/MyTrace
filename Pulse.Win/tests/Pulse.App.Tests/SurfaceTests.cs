using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pulse.App;
using Pulse.App.Settings;
using Pulse.App.Spend;
using Pulse.Core.Accounts;
using Pulse.Core.Ledger;
using Pulse.Core.Providers;
using Pulse.Core.Usage;
using Xunit;

namespace Pulse.App.Tests;

public class SurfaceTests
{
    private static readonly Dispatcher Sta = StartSta();

    private static Dispatcher StartSta()
    {
        Dispatcher? dispatcher = null;
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            void Add(string key, string color)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                brush.Freeze();
                app.Resources[key] = brush;
            }
            Add("RailBackground", "#E8141414");
            Add("RailForeground", "#FFF2F2F2");
            Add("RingTrack", "#33FFFFFF");
            Add("MutedForeground", "#FF9E9E9E");
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        Sta.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        if (error is not null) throw error;
    }

    [Fact]
    public void Rail_Update_Uses_The_Usage_Steps_And_A_Dash_When_Nothing_Was_Read()
    {
        OnSta(() =>
        {
            var rail = new RailWindow();
            var account = new MonitoredAccount { Provider = ProviderId.Cursor, AccountId = "Cursor" };

            Apply(rail, account, 0.12, false);
            var ring = rail.RingFor(ProviderId.Cursor);
            Assert.NotNull(ring);
            Assert.Equal("12%", ring!.FigureText);
            Assert.Equal(UsagePaint.Good, ring.ArcColor);

            Apply(rail, account, 0.6, false);
            Assert.Equal("60%", ring.FigureText);
            Assert.Equal(UsagePaint.Amber, ring.ArcColor);

            Apply(rail, account, 0.9, false);
            Assert.Equal(Ui.PercentLeft(10), ring.FigureText);
            Assert.Equal(UsagePaint.WarningRed, ring.ArcColor);

            Apply(rail, account, 0.97, false);
            Assert.Equal(Ui.PercentLeft(3), ring.FigureText);
            Assert.Equal(UsagePaint.WarningRed, ring.ArcColor);

            Apply(rail, account, 0.4, true);
            Assert.Equal(Ui.Spent, ring.FigureText);
            Assert.Equal(UsagePaint.Spent, ring.ArcColor);

            rail.UpdateProvider(account, ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "missing"));
            Assert.Equal(Ui.Spent, ring.FigureText);
            Assert.Equal(UsagePaint.Spent, ring.ArcColor);

            var fresh = new MonitoredAccount { Provider = ProviderId.Codex, AccountId = "Codex" };
            rail.UpdateProvider(fresh, ProviderReadResult.Failed(ProviderReadHealth.CredentialExpired, "missing"));
            var empty = rail.RingFor(ProviderId.Codex);
            Assert.NotNull(empty);
            Assert.Equal("—", empty!.FigureText);
            Assert.Null(empty.ArcColor);
            rail.Close();
        });
    }

    [Fact]
    public void Two_Accounts_Of_One_Provider_Get_Distinct_Rings_With_Labels()
    {
        OnSta(() =>
        {
            var rail = new RailWindow();
            var primary = new MonitoredAccount { Provider = ProviderId.ClaudeCode, AccountId = "ClaudeCode", IsBorrowed = true };
            var added = new MonitoredAccount
            {
                Provider = ProviderId.ClaudeCode,
                AccountId = "ClaudeCode#abc12345",
                Label = "工作",
                IsBorrowed = false,
            };

            Apply(rail, primary, 0.20, false);
            Apply(rail, added, 0.80, false);

            var primaryRing = rail.RingFor(primary);
            var addedRing = rail.RingFor(added);
            Assert.NotNull(primaryRing);
            Assert.NotNull(addedRing);
            Assert.NotSame(primaryRing, addedRing);
            Assert.Equal("20%", primaryRing!.FigureText);
            // 80% used is past the "N% left" step.
            Assert.Equal(Ui.PercentLeft(20), addedRing!.FigureText);
            Assert.Contains("工作", (string)addedRing.ToolTip!);
            Assert.DoesNotContain("工作", (string)primaryRing.ToolTip!);
            rail.Close();
        });
    }

    [Fact]
    public void Detail_Draws_One_Bar_Per_Window_And_None_When_The_Reading_Is_Empty()
    {
        OnSta(() =>
        {
            var card = new HoverDetailCard();
            var reset = new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
            var windows = new UsageWindow[]
            {
                new("five", UsageWindowKind.FiveHour, null, 0.25, 5 * 3600, reset),
                new("week", UsageWindowKind.Weekly, "Claude", 0.8, 7 * 86400, reset.AddDays(3)),
            };
            card.ShowFor(Reading(windows), new Border(), openToTheLeft: true);
            var text = string.Join("\n", Find<TextBlock>(card.DetailRoot!).Select(block => block.Text));
            Assert.Contains(Ui.UsageTitle("Claude Code"), text);
            Assert.Contains(UiLanguage.IsChinese ? "重置" : "Resets", text);
            var meters = Find<UsageMeter>(card.DetailRoot!).ToList();
            Assert.Equal(2, meters.Count);
            Assert.Equal(windows[0].UsedFraction, meters[0].FillFraction, 5);
            Assert.Equal(windows[1].UsedFraction, meters[1].FillFraction, 5);
            Assert.DoesNotContain(meters, meter => meter.FillFraction == 0);

            card.ShowFor(Reading([]), new Border(), openToTheLeft: true);
            Assert.Empty(Find<UsageMeter>(card.DetailRoot!));
        });
    }

    [Fact]
    public void Selecting_Claude_And_Codex_Shows_Each_Sources_Own_Reading()
    {
        OnSta(() =>
        {
            var home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pulse-spend-" + Guid.NewGuid().ToString("N"));
            var cache = System.IO.Path.Combine(home, "cache");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(home, ".claude", "projects", "demo"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(home, ".codex", "sessions"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(home, ".local", "share", "opencode"));
            WriteOpenCodeStore(System.IO.Path.Combine(home, ".local", "share", "opencode", "opencode.db"));
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(home, ".claude", "projects", "demo", "claude-session.jsonl"),
                """
                {"type":"assistant","message":{"id":"msg_01","model":"claude-sonnet-4-5","usage":{"input_tokens":100,"cache_creation_input_tokens":50,"cache_read_input_tokens":200,"output_tokens":30}},"timestamp":"2026-09-20T09:01:23Z"}
                """);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(home, ".codex", "sessions", "codex-session.jsonl"),
                """
                {"timestamp":"2026-09-20T10:00:00Z","payload":{"type":"token_count","model":"gpt-5","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":4}}}}
                """);

            var spend = new TokenSpendWindow(new TranscriptScanner(cache), ModelPrices.Empty, home)
            {
                ShowActivated = false,
                Left = -4000,
                Top = -4000,
            };
            try
            {
                spend.Show();
                spend.UpdateLayout();

                var claude = spend.SummaryText.Text;
                var claudeSessions = spend.SessionList.Items.Cast<TokenSpendWindow.SessionRow>().Select(row => row.TokensText).ToList();
                Assert.Contains("380", claude);
                Assert.Contains("380", claudeSessions);
                Assert.True(ChartBars(spend) > 0);
                Assert.False(ChartIsEmpty(spend));

                spend.SourceList.SelectedIndex = 1;
                spend.UpdateLayout();
                var codex = spend.SummaryText.Text;
                var codexSessions = spend.SessionList.Items.Cast<TokenSpendWindow.SessionRow>().Select(row => row.TokensText).ToList();
                Assert.NotEqual(claude, codex);
                Assert.Contains("14", codex);
                Assert.DoesNotContain("380", codex);
                Assert.Contains("14", codexSessions);
                Assert.DoesNotContain("380", codexSessions);
                Assert.NotEqual(claudeSessions, codexSessions);
                Assert.True(ChartBars(spend) > 0);
                Assert.False(ChartIsEmpty(spend));

                spend.SourceList.SelectedIndex = 2;
                spend.UpdateLayout();
                var openCode = spend.SummaryText.Text;
                Assert.NotEqual(codex, openCode);
                Assert.Contains("222", openCode);
                Assert.DoesNotContain("380", openCode);
                Assert.True(ChartBars(spend) > 0);
                Assert.False(ChartIsEmpty(spend));
            }
            finally
            {
                spend.Close();
                try { System.IO.Directory.Delete(home, recursive: true); } catch (System.IO.IOException) { }
            }
        });
    }

    private static void WriteOpenCodeStore(string path)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE session (id TEXT PRIMARY KEY, slug TEXT, title TEXT, directory TEXT);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT);
            INSERT INTO session (id, slug, title, directory) VALUES ('s', 'slug', 'OpenCode row', 'E:/work');
            INSERT INTO message (id, session_id, time_created, time_updated, data)
            VALUES ('m', 's', 0, 0, $data);
            """;
        schema.Parameters.AddWithValue("$data",
            """{"role":"assistant","modelID":"m-open","time":{"created":1789981200000},"tokens":{"input":200,"output":22,"reasoning":0,"cache":{"write":0,"read":0}}}""");
        schema.ExecuteNonQuery();
    }

    private static bool ChartIsEmpty(TokenSpendWindow window) =>
        window.ChartCanvas.Children.OfType<TextBlock>().Any(block =>
            block.Text != null && block.Text.Contains("No transcripts found", StringComparison.Ordinal));

    private static int ChartBars(TokenSpendWindow window) =>
        window.ChartCanvas.Children.OfType<System.Windows.Shapes.Rectangle>().Count(bar => bar.Height > 0);

    [Fact]
    public void Every_Provider_Mark_Is_A_Shape()
    {
        foreach (var id in ProviderCatalog.All)
        {
            var geometry = ProviderGlyph.GeometryFor(id);
            Assert.NotNull(geometry);
            Assert.True(geometry!.Bounds.Width > 0 && geometry.Bounds.Height > 0, id.ToString());
        }
    }

    [Fact]
    public void Spend_Sources_Stack_And_Settings_Rows_Carry_Marks()
    {
        OnSta(() =>
        {
            var home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pulse-surface-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(home);
            var spend = new TokenSpendWindow(new TranscriptScanner(), ModelPrices.Empty, home)
            {
                ShowActivated = false,
                Left = -4000,
                Top = -4000,
            };
            var settings = new SettingsWindow(new InMemoryCredentialStore())
            {
                ShowActivated = false,
                Left = -4000,
                Top = -4000,
            };
            try
            {
                spend.Show();
                settings.Show();
                spend.UpdateLayout();
                settings.UpdateLayout();

                Assert.Empty(Find<RadioButton>(spend));
                var list = Assert.Single(Find<ListBox>(spend), box => box is not ListView);
                var first = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
                var second = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.NotNull(first);
                Assert.NotNull(second);
                var top = first!.TranslatePoint(new Point(0, 0), list);
                var below = second!.TranslatePoint(new Point(0, 0), list);
                Assert.True(below.Y > top.Y, $"sources sit side by side ({top.Y}, {below.Y})");
                Assert.True(Math.Abs(below.X - top.X) < 2);

                var marks = Find<GlyphView>(settings).Select(glyph => glyph.Provider).ToHashSet();
                foreach (var id in ProviderCatalog.All)
                    Assert.Contains(id, marks);
            }
            finally
            {
                spend.Close();
                settings.Close();
                try { System.IO.Directory.Delete(home, recursive: true); } catch (System.IO.IOException) { }
            }
        });
    }

    [Fact]
    public void Four_Ring_Rail_Renders()
    {
        OnSta(() =>
        {
            var rail = new RailWindow { ShowActivated = false, Left = -4000, Top = -4000 };
            (ProviderId Id, double Used, bool Spent)[] samples =
            [
                (ProviderId.ClaudeCode, 0.12, false),
                (ProviderId.Codex, 0.6, false),
                (ProviderId.Cursor, 0.9, false),
                (ProviderId.Grok, 1, true),
            ];
            foreach (var sample in samples)
            {
                var account = new MonitoredAccount { Provider = sample.Id, AccountId = sample.Id.ToString() };
                Apply(rail, account, sample.Used, sample.Spent);
            }

            var root = (FrameworkElement)rail.Content;
            root.Measure(new Size(rail.Width, rail.Height));
            root.Arrange(new Rect(0, 0, rail.Width, rail.Height));
            root.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(root.RenderSize.Width));
            var height = Math.Max(1, (int)Math.Ceiling(root.RenderSize.Height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var buffer = new System.IO.MemoryStream();
            encoder.Save(buffer);
            Assert.True(buffer.Length > 500);
            var preview = Environment.GetEnvironmentVariable("PULSE_RAIL_PREVIEW");
            if (!string.IsNullOrEmpty(preview))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(preview)!);
                System.IO.File.WriteAllBytes(preview, buffer.ToArray());
            }

            rail.Close();
        });
    }

    private static void Apply(RailWindow rail, MonitoredAccount account, double used, bool exhausted)
    {
        var window = new UsageWindow(
            "w", UsageWindowKind.FiveHour, null, used, 5 * 3600,
            DateTimeOffset.UtcNow.AddHours(3), IsExhausted: exhausted);
        var usage = new ProviderUsage(
            account.Provider, account.AccountId, [window], DateTimeOffset.UtcNow,
            UsageState.Live, null, null, null, UsageRoute.Endpoint);
        rail.UpdateProvider(account, ProviderReadResult.Ok(usage));
    }

    private static ProviderUsage Reading(IReadOnlyList<UsageWindow> windows) =>
        new(ProviderId.ClaudeCode, "ClaudeCode", windows, DateTimeOffset.UtcNow,
            UsageState.Live, "Pro", null, null, UsageRoute.Endpoint);

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
