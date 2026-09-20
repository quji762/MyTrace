using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core.Accounts;
using Pulse.Core.Ledger;

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
    private readonly ModelPrices _prices;
    private TranscriptKind _kind = TranscriptKind.ClaudeCode;
    private UsageLedger _ledger = UsageLedger.EmptyLedger;
    private IReadOnlyDictionary<string, ScannedTranscript> _files =
        new Dictionary<string, ScannedTranscript>();

    private const int SummaryWindowDays = 7;

    public TokenSpendWindow(TranscriptScanner? scanner = null, ModelPrices? prices = null)
    {
        InitializeComponent();
        _scanner = scanner ?? new TranscriptScanner();
        _prices = prices ?? ModelPrices.Empty;
        ClaudeTab.IsChecked = true;
    }

    private void OnProviderTabChanged(object sender, RoutedEventArgs e)
    {
        if (ClaudeTab is null) return; // XAML still initializing
        _kind = CodexTab.IsChecked == true ? TranscriptKind.Codex
            : OpenCodeTab.IsChecked == true ? TranscriptKind.OpenCode
            : CherryTab.IsChecked == true ? TranscriptKind.CherryStudio
            : TranscriptKind.ClaudeCode;
        Refresh();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_kind == TranscriptKind.CherryStudio)
        {
            // Cherry Studio: Claude Code-shaped JSONL trees, stream-deduped.
            var records = CherryStudioReader.Records();
            var built = AgentUsageLedger.Build(records, _prices);
            _ledger = built.Ledger;
            SessionList.ItemsSource = built.Sessions
                .Select(s => new SessionRow
                {
                    Name = s.Title ?? s.Name,
                    Project = s.Project ?? "",
                    TokensText = s.Tokens.ToString("N0"),
                    EndText = s.End.ToLocalTime().ToString("MMM d HH:mm"),
                })
                .ToList();
            RenderSummary();
            RenderChart();
            return;
        }

        if (_kind == TranscriptKind.OpenCode)
        {
            // OpenCode (and Kilo) keeps one SQLite store, not per-session files.
            var root = TranscriptLocator.OpenCodeRoot();
            var store = root is null ? null : Directory.EnumerateFiles(root, "db.sqlite",
                SearchOption.AllDirectories).FirstOrDefault() is { } found ? found : null;
            _ledger = store is null
                ? UsageLedger.EmptyLedger
                : OpenCodeStoreReader.LedgerAt(store, _prices);
            _files = new Dictionary<string, ScannedTranscript>();
        }
        else
        {
            var result = _scanner.Scan(_kind, _prices, refresh: true);
            _ledger = result.Ledger;
            _files = result.Files;
        }

        RenderSummary();
        RenderChart();
        RenderSessions();
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
                Text = "No transcripts found. Sessions scanned from the CLI appear here.",
                Foreground = MutedBrush(),
                Margin = new Thickness(8),
            });
            return;
        }

        var maxTokens = Math.Max(1, _ledger.Days.Max(day => day.Tokens));
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

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
            });
        }

        SessionList.ItemsSource = rows.OrderByDescending(row => row.EndText).ToList();
    }

    public sealed record SessionRow
    {
        public string Name { get; init; } = "";
        public string Project { get; init; } = "";
        public string TokensText { get; init; } = "";
        public string EndText { get; init; } = "";
    }

    // --- Brushes -----------------------------------------------------------------------

    private static System.Windows.Media.Brush ProgressBrush() =>
        (System.Windows.Application.Current?.TryFindResource("RingProgress") as System.Windows.Media.Brush)
        ?? System.Windows.Media.Brushes.SteelBlue;

    private static System.Windows.Media.Brush MutedBrush() =>
        (System.Windows.Application.Current?.TryFindResource("MutedForeground") as System.Windows.Media.Brush)
        ?? System.Windows.Media.Brushes.Gray;
}
