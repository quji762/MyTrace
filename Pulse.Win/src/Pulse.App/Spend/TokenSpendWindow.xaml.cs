using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.App;
using Pulse.Core.Accounts;
using Pulse.Core.Ledger;
using Pulse.Core.Providers;

namespace Pulse.App.Spend;

/// <summary>
/// Token Spend pane; the Windows counterpart of upstream's token spend view.
/// Daily token bars (gaps kept as zero-height days so the chart reads as a
/// calendar), the session list (one row per transcript), and the summary line
/// (all-time tokens/cost, busiest recent day, top model with its share).
///
/// Data comes from TranscriptScanner over %USERPROFILE%\.claude\projects and
/// .codex\sessions. The money is a translation, not a bill: the same tokens at
/// the providers' published API rates — which is why unpriced models are counted
/// in the bars but absent from the cost.
/// </summary>
public partial class TokenSpendWindow : Window
{
    private readonly TranscriptScanner _scanner;
    private readonly string? _vsCodeHome;
    private ModelPrices? _prices;
    private IReadOnlyDictionary<string, ModelPrice>? _priceTable;
    private TranscriptKind _kind = TranscriptKind.ClaudeCode;
    private UsageLedger _ledger = UsageLedger.EmptyLedger;
    private IReadOnlyDictionary<string, ScannedTranscript> _files =
        new Dictionary<string, ScannedTranscript>();

    private const int SummaryWindowDays = 7;

    /// <summary>One scannable row. The mark is the quota provider's when the source has one.</summary>
    public sealed record SpendSource(string Label, TranscriptKind Kind, ProviderId? Mark);

    public static IReadOnlyList<SpendSource> Catalog { get; } =
    [
        new("Claude Code", TranscriptKind.ClaudeCode, ProviderId.ClaudeCode),
        new("Codex", TranscriptKind.Codex, ProviderId.Codex),
        new("OpenCode", TranscriptKind.OpenCode, ProviderId.OpenCodeGo),
        new("Cherry Studio", TranscriptKind.CherryStudio, null),
        new("Cline", TranscriptKind.Cline, null),
        new("Amp", TranscriptKind.Amp, null),
        new("Goose", TranscriptKind.Goose, null),
        new("Copilot OTEL", TranscriptKind.CopilotOtel, ProviderId.Copilot),
        new("Copilot Desktop", TranscriptKind.CopilotDesktop, ProviderId.Copilot),
        new("Copilot VS Code", TranscriptKind.CopilotVsCode, ProviderId.Copilot),
        new("Kiro", TranscriptKind.Kiro, null),
        new("Qwen", TranscriptKind.Qwen, null),
        new("Gemini", TranscriptKind.Gemini, null),
        new("ZCode", TranscriptKind.ZCode, null),
        new("DSH", TranscriptKind.Dsh, null),
        new("Junie", TranscriptKind.Junie, null),
        new("Codebuff", TranscriptKind.Codebuff, null),
        new("Unsloth", TranscriptKind.Unsloth, null),
        new("Jcode", TranscriptKind.Jcode, null),
        new("Fx", TranscriptKind.Fx, null),
        new("OpenClaw", TranscriptKind.OpenClaw, null),
        new("Droid", TranscriptKind.Droid, null),
        new("Mux", TranscriptKind.Mux, null),
        new("GJC", TranscriptKind.Gjc, null),
        new("Pi", TranscriptKind.Pi, null),
        new("Prime Agent", TranscriptKind.PrimeAgent, null),
        new("Roo/Kilo/Cline", TranscriptKind.RooCode, null),
        new("CodeBuddy/WorkBuddy", TranscriptKind.Codebuddy, null),
        new("Hermes", TranscriptKind.Hermes, null),
        new("Zed", TranscriptKind.Zed, null),
        new("LM Studio", TranscriptKind.LmStudio, null),
        new("MiMo Code", TranscriptKind.Micode, null),
        new("OpenCode Review", TranscriptKind.OpenCodeReview, ProviderId.OpenCodeGo),
        new("Command Code", TranscriptKind.CommandCode, ProviderId.CommandCode),
        new("Crush", TranscriptKind.Crush, null),
        new("Hindsight", TranscriptKind.Hindsight, null),
        new("Mcode", TranscriptKind.Mcode, null),
        new("Trae", TranscriptKind.Trae, null),
        new("Copilot Log", TranscriptKind.CopilotCombined, ProviderId.Copilot),
        new("Cursor", TranscriptKind.CursorCaptured, ProviderId.Cursor),
        new("Reasonix", TranscriptKind.Reasonix, null),
        new("Augment", TranscriptKind.Augment, null),
        new("Warp", TranscriptKind.Warp, null),
        new("Devin CLI", TranscriptKind.DevinCli, ProviderId.Devin),
        new("Grok Build", TranscriptKind.Grok, ProviderId.Grok),
        new("Kimi CLI", TranscriptKind.KimiCli, ProviderId.KimiCode),
        new("Devin Desktop", TranscriptKind.DevinDesktop, ProviderId.Devin),
        new("Antigravity CLI", TranscriptKind.AntigravityCli, ProviderId.Antigravity),
        new("Antigravity IDE", TranscriptKind.AntigravityIde, ProviderId.Antigravity),
        new("Antigravity", TranscriptKind.AntigravityCaptured, ProviderId.Antigravity),
    ];

    public TokenSpendWindow(TranscriptScanner? scanner = null, ModelPrices? prices = null, string? userProfile = null)
    {
        InitializeComponent();
        Settings.ThemeManager.Apply(Settings.ThemeManager.Current);
        _scanner = scanner ?? new TranscriptScanner();
        _prices = prices;
        _vsCodeHome = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var source in Catalog)
            SourceList.Items.Add(Row(source));
        SourceList.SelectedIndex = 0;
        if (_prices is null) _ = LoadPricesAsync();
    }

    private static ListBoxItem Row(SpendSource source)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        if (source.Mark is { } mark)
        {
            row.Children.Add(new GlyphView
            {
                Provider = mark,
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = source.Label,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            FontSize = 13,
        });
        return new ListBoxItem { Content = row, Tag = source };
    }

    /// <summary>The plan vendor to fall back to for a model no first-party
    /// provider publishes; the plan the tokens were bought on is the last
    /// word, never the first.</summary>
    private static string? PriceVendor(TranscriptKind kind) => kind switch
    {
        TranscriptKind.OpenCode or TranscriptKind.OpenCodeReview => "opencode-go",
        TranscriptKind.Cline => "cline-pass",
        _ => null,
    };

    /// <summary>Fetch the live price list once; until it lands (or fails) the
    /// pane shows tokens with every model unpriced, which is the honest empty
    /// state. A later tab switch prices with whatever arrived.</summary>
    private async Task LoadPricesAsync()
    {
        try
        {
            var table = await ModelPriceCatalog.LoadAsync().ConfigureAwait(true);
            if (table.Count == 0) return;
            _priceTable = table;
            _prices = new CatalogModelPrices(table, PriceVendor(_kind));
            if (IsActiveTab) Refresh();
        }
        catch (Exception) { }
    }

    private bool IsActiveTab => IsLoaded;

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList?.SelectedItem is not ListBoxItem { Tag: SpendSource source }) return;
        _kind = source.Kind;
        // Vendor is per source (opencode-go / cline-pass); rebuild before refresh.
        if (_priceTable is not null)
            _prices = new CatalogModelPrices(_priceTable, PriceVendor(_kind));
        if (SummaryText is null) return;
        Refresh();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>The pane's read: opt-in off does no discovery; on attributes each source through <see cref="SpendReading"/>.</summary>
    public static SpendReading.Report ReadSources(string home, bool enabled, ModelPrices? prices, DateOnly? spanStart, DateOnly? spanEnd) =>
        SpendReading.Read(enabled, home, prices ?? ModelPrices.Empty, spanStart, spanEnd);

    private void Refresh()
    {
        if (SummaryText is null) return;
        // The selected row owns the chart and the session list. Claude Code and
        // Codex are transcript scans; every other row is that source's records.
        if (_kind is TranscriptKind.ClaudeCode or TranscriptKind.Codex)
        {
            var scanned = _scanner.Scan(_kind, _prices ?? ModelPrices.Empty, userProfile: _vsCodeHome);
            _ledger = scanned.Ledger;
            _files = scanned.Files;
            RenderSessions();
            RenderSummary();
            RenderChart();
        }
        else if (_kind == TranscriptKind.OpenCode)
        {
            var root = TranscriptLocator.OpenCodeRoot(_vsCodeHome);
            var database = root is null ? null : Path.Combine(root, "opencode.db");
            _ledger = database is null
                ? UsageLedger.EmptyLedger
                : OpenCodeStoreReader.LedgerAt(database, _prices ?? ModelPrices.Empty);
            _files = new Dictionary<string, ScannedTranscript>();
            SessionList.ItemsSource = Array.Empty<SessionRow>();
            RenderSummary();
            RenderChart();
        }
        else
        {
            RenderRecordBased(_kind);
        }

        // The multi-source report stays behind the opt-in switch, and it is
        // added under the selected reading rather than instead of it.
        var optIn = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseWin", "spend-opt-in.txt");
        if (!SpendOptIn.Load(optIn)) return;
        var home = string.IsNullOrEmpty(_vsCodeHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _vsCodeHome;
        var spanEnd = DateOnly.FromDateTime(DateTime.Now);
        var report = ReadSources(home, enabled: true, _prices, spanEnd.AddDays(-6), spanEnd);
        var extra = FormatReport(report);
        if (extra.Length == 0) return;
        SummaryText.Text = string.IsNullOrEmpty(SummaryText.Text)
            ? extra
            : SummaryText.Text + Environment.NewLine + extra;
    }

    /// <summary>Record-based sources: CherryStudio, Cline, Amp — each reader
    /// normalizes its store into AgentUsageRecords, one Build prices them all.</summary>
    private void RenderRecordBased(TranscriptKind kind)
    {
        var records = kind switch
        {
            TranscriptKind.CherryStudio => CherryStudioReader.Records(),
            TranscriptKind.Cline => ClineCliReader.Records(),
            TranscriptKind.Amp => AmpSessionReader.Records(),
            TranscriptKind.Goose => GooseReader.Records(),
            TranscriptKind.CopilotOtel => CopilotOtelReader.Records(),
            TranscriptKind.CopilotDesktop => CopilotDesktopReader.Records(),
            TranscriptKind.CopilotVsCode => CopilotVsCodeReader.Records(),
            TranscriptKind.Kiro => KiroReader.Records(),
            TranscriptKind.Qwen => QwenSessionReader.Records(),
            TranscriptKind.Gemini => GeminiSessionReader.Records(),
            TranscriptKind.ZCode => ZCodeReader.Records(),
            TranscriptKind.Dsh => DshUsageReader.Records(),
            TranscriptKind.Junie => JunieUsageReader.Records(),
            TranscriptKind.Codebuff => CodebuffUsageReader.Records(),
            TranscriptKind.Unsloth => UnslothReader.Records(),
            TranscriptKind.Jcode => JcodeUsageReader.Records(),
            TranscriptKind.Fx => FxUsageReader.Records(),
            TranscriptKind.OpenClaw => OpenClawSessionReader.Records(_vsCodeHome),
            TranscriptKind.Droid => DroidSessionReader.Records(),
            TranscriptKind.Mux => MuxUsageReader.Records(),
            TranscriptKind.Gjc => GjcUsageReader.Records(),
            TranscriptKind.Pi => PiFamilySessionReader.Records("pi"),
            TranscriptKind.PrimeAgent => PrimeAgentSessionReader.Records(
                PrimeAgentSessionReader.Roots(_vsCodeHome)),
            TranscriptKind.RooCode => VsCodeTaskLogReader.AllClients(_vsCodeHome),
            TranscriptKind.Codebuddy => new[] { "codebuddy", "workbuddy" }
                .SelectMany(client => TencentBuddyReader.Records(client, _vsCodeHome)).ToList(),
            TranscriptKind.OpenCodeReview => OpenCodeReviewReader.Records(),
            TranscriptKind.CommandCode => CommandCodeSpendReader.Records(),
            TranscriptKind.Hindsight => CapturedHindsightReader.Records(),
            TranscriptKind.Mcode => CapturedMcodeReader.Records(),
            TranscriptKind.Trae => CapturedTraeReader.Records(),
            TranscriptKind.CopilotCombined => CopilotLogReader.Records(),
            TranscriptKind.CursorCaptured => CapturedCursorReader.Records(),
            TranscriptKind.AntigravityCaptured => CapturedAntigravityReader.Records(),
            TranscriptKind.LmStudio => LmStudioUsageReader.Records(),
            TranscriptKind.Zed => ZedReader.Records(),
            TranscriptKind.Hermes => HermesReader.Records(),
            TranscriptKind.Micode => MicodeReader.Records(),
            TranscriptKind.Crush => CrushReader.Records(),
            TranscriptKind.Reasonix => ReasonixUsageReader.Records(),
            TranscriptKind.Augment => AugmentUsageReader.Records(),
            TranscriptKind.Warp => CapturedWarpReader.Records(),
            TranscriptKind.DevinCli => LegacyStores.DevinCliRecords(_vsCodeHome),
            TranscriptKind.Grok => LegacyStores.GrokRecords(_vsCodeHome),
            TranscriptKind.KimiCli => LegacyStores.KimiRecords(_vsCodeHome),
            TranscriptKind.DevinDesktop => DevinDesktopReader.Records(_vsCodeHome),
            TranscriptKind.AntigravityCli => AntigravityCliReader.Records(_vsCodeHome),
            TranscriptKind.AntigravityIde => AntigravityCliReader.Records(_vsCodeHome, client: "antigravity-ide"),
            _ => Array.Empty<AgentUsageRecord>(),
        };
        var built = AgentUsageLedger.Build(records, _prices ?? ModelPrices.Empty);
        _ledger = built.Ledger;
        SessionList.ItemsSource = built.Sessions
            .Select(s => new SessionRow
            {
                Name = s.Title ?? s.Name,
                Project = s.Project ?? "",
                TokensText = s.Tokens.ToString("N0"),
                EndText = s.End.ToLocalTime().ToString("MMM d HH:mm"),
                EndSort = s.End,
            })
            .ToList();
        RenderSummary();
        RenderChart();
    }

    private static string FormatReport(SpendReading.Report report)
    {
        if (report.Sources.Count == 0) return "";
        return string.Join(Environment.NewLine, report.Sources.Select(source =>
        {
            var models = source.Models.Count == 0
                ? ""
                : " " + string.Join(", ", source.Models.Select(model =>
                    model.PricedAmount is { } amount
                        ? $"{model.Model} {model.Tokens} (${amount:0.00})"
                        : $"{model.Model} {model.Tokens} unpriced {model.UnpricedTokens}"));
            return source.Recognized && source.Tokens == 0 && source.Models.Count == 0
                ? $"{source.SourceId}: recognized, no token records"
                : $"{source.SourceId}: {source.Tokens} tokens{models}";
        }));
    }

    // --- Summary -----------------------------------------------------------------

    private void RenderSummary()
    {
        var (allTokens, allCost) = _ledger.AllTime;
        var (weekTokens, weekCost) = _ledger.TotalOverLast(SummaryWindowDays);

        var parts = new List<string>
        {
            $"All time: {allTokens:N0} tokens ({allCost:C2} equivalent)",
            $"Last {SummaryWindowDays} days: {weekTokens:N0} tokens ({weekCost:C2})",
        };

        if (_ledger.BusiestDayOverLast(SummaryWindowDays) is { } busiest)
            parts.Add($"Busiest: {busiest.Date:MMM d} ({busiest.Tokens:N0})");

        if (_ledger.TopModelOverLast(SummaryWindowDays) is { } top)
            parts.Add($"Top model: {top.Name} ({top.Share:P0})");

        if (_ledger.UnpricedModels.Count > 0)
            parts.Add($"Unpriced: {string.Join(", ", _ledger.UnpricedModels.Take(3))}" +
                      (_ledger.UnpricedModels.Count > 3 ? "…" : ""));

        SummaryText.Text = string.Join("   ·   ", parts);
    }

    // --- Chart ---------------------------------------------------------------------

    private void RenderChart()
    {
        ChartCanvas.Children.Clear();
        if (_ledger.Days.Count == 0)
        {
            ChartCanvas.Children.Add(new TextBlock
            {
                Text = UiLanguage.IsChinese
                    ? "未找到会话记录。CLI 会话扫描后会出现在这里。"
                    : "No transcripts found. Sessions scanned from the CLI appear here.",
                Foreground = MutedBrush(),
                Margin = new Thickness(8),
            });
            return;
        }

        var maxTokens = Math.Max(1, _ledger.Days.Max(day => day.Tokens));
        // Actual size is 0 until the window is laid out. A reading still has to
        // draw; the resize handler paints again at the real width.
        var width = ChartCanvas.ActualWidth > 0 ? ChartCanvas.ActualWidth
            : (double.IsNaN(ChartCanvas.Width) || ChartCanvas.Width <= 0 ? 480 : ChartCanvas.Width);
        var height = ChartCanvas.ActualHeight > 0 ? ChartCanvas.ActualHeight
            : (double.IsNaN(ChartCanvas.Height) || ChartCanvas.Height <= 0 ? 180 : ChartCanvas.Height);

        var barAndGap = width / _ledger.Days.Count;
        var barWidth = Math.Max(2, barAndGap * 0.7);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(70.0 / barAndGap));

        for (var index = 0; index < _ledger.Days.Count; index++)
        {
            var day = _ledger.Days[index];
            var x = index * barAndGap + (barAndGap - barWidth) / 2;
            var barHeight = day.Tokens > 0 ? day.Tokens / (double)maxTokens * (height - 24) : 0;

            if (barHeight > 0)
            {
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = barWidth,
                    Height = barHeight,
                    Fill = ProgressBrush(),
                    RadiusX = 1,
                    RadiusY = 1,
                };
                System.Windows.Controls.Canvas.SetLeft(bar, x);
                System.Windows.Controls.Canvas.SetTop(bar, height - 18 - barHeight);
                ChartCanvas.Children.Add(bar);

                // Unpriced share rides on the bar as a lighter cap: counted, but
                // absent from the cost line.
                if (day.UnpricedTokens > 0)
                {
                    var unpricedHeight = day.UnpricedTokens / (double)maxTokens * (height - 24);
                    var cap = new System.Windows.Shapes.Rectangle
                    {
                        Width = barWidth,
                        Height = Math.Min(unpricedHeight, barHeight),
                        Fill = MutedBrush(),
                        Opacity = 0.45,
                        RadiusX = 1,
                        RadiusY = 1,
                    };
                    System.Windows.Controls.Canvas.SetLeft(cap, x);
                    System.Windows.Controls.Canvas.SetTop(cap, height - 18 - barHeight);
                    ChartCanvas.Children.Add(cap);
                }

                var tooltip = $"{day.Date:MMM d}: {day.Tokens:N0} tokens";
                if (day.Cost > 0) tooltip += $" · {day.Cost:C2}";
                if (day.UnpricedTokens > 0) tooltip += $" · {day.UnpricedTokens:N0} unpriced";
                ToolTipService.SetToolTip(bar, tooltip);
            }

            // Date labels every few bars so months stay readable.
            if (index % labelEvery == 0)
            {
                var label = new TextBlock
                {
                    Text = day.Date.ToString("MM-dd"),
                    FontSize = 9,
                    Foreground = MutedBrush(),
                };
                System.Windows.Controls.Canvas.SetLeft(label, x);
                System.Windows.Controls.Canvas.SetTop(label, height - 16);
                ChartCanvas.Children.Add(label);
            }
        }
    }

    private void OnChartResized(object sender, SizeChangedEventArgs e) => RenderChart();

    // --- Sessions --------------------------------------------------------------------

    private void RenderSessions()
    {
        var rows = new List<SessionRow>();
        foreach (var (path, scanned) in _files)
        {
            int tokens = 0;
            DateTimeOffset? start = null;
            DateTimeOffset? end = null;
            foreach (var (key, models) in scanned.Buckets)
            {
                if (TranscriptParser.ParseSlotKey(key) is not { } at) continue;
                start = start is { } existing && existing <= at ? existing : at;
                end = end is { } existingEnd && existingEnd >= at ? existingEnd : at;
                tokens += models.Values.Sum(tally => tally.Total);
            }
            if (tokens <= 0) continue;

            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var project = scanned.Cwd is { } cwd
                ? System.IO.Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : TranscriptLocator.ProjectFromClaudeFolder(path);

            rows.Add(new SessionRow
            {
                Name = scanned.Title ?? name,
                Project = project ?? "",
                TokensText = tokens.ToString("N0"),
                EndText = end?.ToLocalTime().ToString("MMM d HH:mm") ?? "",
                EndSort = end ?? DateTimeOffset.MinValue,
            });
        }

        SessionList.ItemsSource = rows.OrderByDescending(row => row.EndSort).ToList();
    }

    public sealed record SessionRow
    {
        public string Name { get; init; } = "";
        public string Project { get; init; } = "";
        public string TokensText { get; init; } = "";
        public string EndText { get; init; } = "";
        public DateTimeOffset EndSort { get; init; }
    }

    // --- Brushes -----------------------------------------------------------------------

    private static System.Windows.Media.Brush ProgressBrush() => UsagePaint.BrushFor(0.2, false);

    private static System.Windows.Media.Brush MutedBrush() =>
        (System.Windows.Application.Current?.TryFindResource("MutedForeground") as System.Windows.Media.Brush)
        ?? System.Windows.Media.Brushes.Gray;
}
