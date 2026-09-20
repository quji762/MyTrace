using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Pulse.Core.Forecast;
using Pulse.Core.Providers;
using Pulse.Core.Usage;

namespace Pulse.App;

/// <summary>
/// Hover detail card: a non-activating popup listing ALL windows for a provider
/// (the rail ring shows only the first), with plan, reset times, credit balance,
/// and the burn-rate ETA when the data honestly supports one. Uses a WPF Popup
/// with StaysOpen=false so clicking elsewhere dismisses it — no window activation
/// (the rail must not steal focus; upstream hover cards behave the same way).
/// </summary>
public sealed class HoverDetailCard : System.Windows.Controls.Primitives.Popup
{
    private readonly TextBlock _content = new()
    {
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 260,
    };

    public HoverDetailCard()
    {
        AllowsTransparency = true;
        StaysOpen = false;
        PlacementTarget = null;
        Child = new System.Windows.Controls.Border
        {
            Background = TryFind("RailBackground"),
            CornerRadius = new System.Windows.CornerRadius(8),
            Padding = new Thickness(12),
            Child = _content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                Opacity = 0.35,
                ShadowDepth = 2,
            },
        };
    }

    private static System.Windows.Media.Brush? TryFind(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;

    /// <summary>Populate from a full reading and open beside the given ring.</summary>
    public void ShowFor(ProviderUsage usage, UIElement placementTarget)
    {
        PlacementTarget = placementTarget;
        Placement = System.Windows.Controls.Primitives.PlacementMode.Right;

        _content.Inlines.Clear();
        var lines = new List<System.Windows.Documents.Inline>();

        if (usage.Plan is { } plan)
            lines.Add(Header($"{plan}"));

        foreach (var window in usage.Windows)
        {
            var name = DescribeKind(window);
            var line = new System.Windows.Documents.Run(
                $"{name}: {Math.Round(window.UsedFraction * 100)}% used");
            lines.Add(new System.Windows.Documents.Run(line.Text + "\n") { Foreground = ForegroundBrush() });

            if (window.ResetsAt is { } reset)
                lines.Add(new System.Windows.Documents.Run($"  resets {reset.ToLocalTime():MM-dd HH:mm}\n")
                {
                    Foreground = MutedBrush(),
                    FontSize = 10,
                });

            if (BurnRate.For(window, DateTimeOffset.Now) is { } estimate &&
                estimate.TimeToExhausted is { } eta &&
                eta > TimeSpan.Zero)
            {
                lines.Add(new System.Windows.Documents.Run(
                    $"  ~{Math.Floor(eta.TotalMinutes)} min to exhausted at this pace\n")
                {
                    Foreground = WarnBrush(),
                    FontSize = 10,
                });
            }
        }

        if (usage.CreditBalance is { } balance)
            lines.Add(new System.Windows.Documents.Run($"Balance: {balance}\n"));

        if (lines.Count == 0)
            lines.Add(new System.Windows.Documents.Run("No usage reported\n"));

        _content.Inlines.AddRange(lines);
        IsOpen = true;
    }

    private static string DescribeKind(UsageWindow window)
    {
        if (window.Scope is { } scope)
            return $"{WindowName(window)} · {scope}";
        return WindowName(window);
    }

    private static string WindowName(UsageWindow window) => window.Kind switch
    {
        UsageWindowKind.FiveHour => "5-hour limit",
        UsageWindowKind.Weekly => "Weekly limit",
        UsageWindowKind.Monthly => "Monthly limit",
        UsageWindowKind.Daily => "Daily limit",
        UsageWindowKind.Spend => "Spend limit",
        UsageWindowKind.Balance => "Balance",
        UsageWindowKind.Messages => "Messages",
        _ => $"Limit ({TimeSpan.FromSeconds(window.WindowSeconds).TotalHours:0}h)",
    };

    private static System.Windows.Media.Brush ForegroundBrush() =>
        TryFind("RailForeground") ?? System.Windows.Media.Brushes.White;

    private static System.Windows.Media.Brush MutedBrush() =>
        TryFind("MutedForeground") ?? System.Windows.Media.Brushes.Gray;

    private static System.Windows.Media.Brush WarnBrush() =>
        TryFind("RingExhausted") ?? System.Windows.Media.Brushes.OrangeRed;

    private static System.Windows.Documents.Run Header(string text) => new(text + "\n")
    {
        FontWeight = FontWeights.SemiBold,
        Foreground = ForegroundBrush(),
    };
}
