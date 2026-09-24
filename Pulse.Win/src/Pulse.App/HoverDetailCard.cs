using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pulse.Core.Forecast;
using Pulse.Core.Providers;
using Pulse.Core.Usage;

namespace Pulse.App;

/// <summary>
/// The card that opens beside a ring. Same contents as the Mac card: the
/// provider's name, then one bar per limit. The rail itself only carries the
/// headline percent.
/// </summary>
public sealed class HoverDetailCard : System.Windows.Controls.Primitives.Popup
{
    private readonly Border _shell;
    public event Action? PointerEntered;
    public event Action? PointerLeft;
    private string? _lastCacheKey;

    public HoverDetailCard()
    {
        AllowsTransparency = true;
        StaysOpen = true;
        Placement = System.Windows.Controls.Primitives.PlacementMode.Left;
        _shell = new Border
        {
            Width = 260,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(20, 16, 20, 18),
            Background = Brush("CardBackground", Color.FromRgb(0x18, 0x18, 0x1C)),
            BorderBrush = Brush("CardBorder", Color.FromRgb(0x2A, 0x2A, 0x30)),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 36,
                Opacity = 0.55,
                ShadowDepth = 2,
                Color = Color.FromRgb(0, 0, 0),
            },
        };
        _shell.MouseEnter += (_, _) => PointerEntered?.Invoke();
        _shell.MouseLeave += (_, _) => PointerLeft?.Invoke();
        Child = _shell;
    }

    public void ShowFor(ProviderUsage usage, UIElement placementTarget, bool openToTheLeft, string? title = null)
    {
        PlacementTarget = placementTarget;
        Placement = openToTheLeft
            ? System.Windows.Controls.Primitives.PlacementMode.Left
            : System.Windows.Controls.Primitives.PlacementMode.Right;
        HorizontalOffset = openToTheLeft ? -8 : 8;

        // Rebuild only when the data changed — avoids recreating the visual tree
        // on every hover, which was the main source of perceived lag.
        var cacheKey = $"{usage.Provider}:{usage.AccountId}:{usage.ObservedAt}:{usage.State}:{usage.Windows.Count}:{title}";
        if (cacheKey != _lastCacheKey || _shell.Child is null)
        {
            _shell.Child = Build(usage, title);
            _lastCacheKey = cacheKey;
        }
        if (!placementTarget.IsVisible) return;
        try
        {
            // Show immediately at full opacity — a fade-in felt like lag on hover.
            _shell.Opacity = 1;
            IsOpen = true;
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>The card body <see cref="ShowFor"/> just built.</summary>
    public FrameworkElement? DetailRoot => _shell.Child as FrameworkElement;

    private static UIElement Build(ProviderUsage usage, string? title = null)
    {
        var stack = new StackPanel();
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        header.Children.Add(new GlyphView
        {
            Provider = usage.Provider,
            Width = 18,
            Height = 18,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = title is { Length: > 0 } ? Ui.UsageTitle(title) : Ui.UsageTitle(ProviderCatalog.DisplayName(usage.Provider)),
            FontFamily = Face,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("RailForeground", Colors.White),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(header);
        stack.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 14, 0, 4),
            Background = Brush("Divider", Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
        });

        if (usage.Windows.Count == 0)
        {
            stack.Children.Add(Note(
                usage.Unavailability?.Kind == UnavailabilityKind.NoCredits ? Ui.NoCredits : Ui.NoReading,
                0.55, new Thickness(0, 14, 0, 0)));
        }

        foreach (var window in usage.Windows)
        {
            stack.Children.Add(Row(window));
        }

        if (usage.Plan is { } plan)
            stack.Children.Add(Note(plan, 0.45, new Thickness(0, 14, 0, 0)));
        if (usage.CreditBalance is { } balance)
            stack.Children.Add(Note(balance, 0.9, new Thickness(0, 8, 0, 0)));
        if (usage.State == UsageState.Stale)
            stack.Children.Add(Note(Ui.CachedReading, 0.4, new Thickness(0, 8, 0, 0)));

        return stack;
    }

    private static UIElement Row(UsageWindow window)
    {
        var fraction = Math.Clamp(window.UsedFraction, 0, 1);
        var spent = window.IsExhausted || fraction >= 1;
        var accent = UsagePaint.BrushFor(fraction, window.IsExhausted);
        var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        block.Children.Add(new TextBlock
        {
            Text = Describe(window),
            FontFamily = Face,
            FontSize = DesignTokens.TypeCaption,
            Foreground = Brush("MutedForeground", Color.FromRgb(0x9A, 0x9A, 0x9A)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        block.Children.Add(new UsageMeter(fraction, accent));

        var facts = new Grid();
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var percent = new TextBlock
        {
            Text = spent
                ? Ui.Spent
                : window.UsedFraction >= UsagePaint.Warning
                    ? Ui.UsedLeftLine(Math.Round(window.UsedFraction * 100), Math.Round((1 - window.UsedFraction) * 100))
                    : Ui.UsedLine(Math.Round(window.UsedFraction * 100)),
            FontFamily = Face,
            FontSize = DesignTokens.TypeCaption,
            FontWeight = FontWeights.Medium,
            Foreground = spent
                ? UsagePaint.BrushFor(1, true)
                : Brush("RailForeground", Colors.White),
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
        Grid.SetColumn(percent, 0);
        facts.Children.Add(percent);
        // An expiry is not a reset: it takes the reset slot only when it comes
        // before the reset, or there is none (upstream v1.4.1).
        if (window.Expiry is { } expiry && (window.ResetsAt is not { } deadline || expiry.At < deadline))
        {
            var lapsing = new TextBlock
            {
                Text = Ui.CreditsExpire(expiry.At, Math.Round(expiry.Amount)),
                FontFamily = Face,
                FontSize = DesignTokens.TypeCaption,
                Opacity = 0.5,
                Foreground = Brush("MutedForeground", Color.FromRgb(0x9A, 0x9A, 0x9A)),
            };
            Grid.SetColumn(lapsing, 1);
            facts.Children.Add(lapsing);
        }
        else if (window.ResetsAt is { } reset)
        {
            var when = new TextBlock
            {
                Text = reset.ToLocalTime() switch
                {
                    var local when local.Date == DateTime.Today => Ui.ResetsToday(local.ToString("HH:mm")),
                    var local when local.Date == DateTime.Today.AddDays(1) => Ui.ResetsTomorrow(local.ToString("HH:mm")),
                    var local => Ui.ResetsOn(local.ToString("MMM d HH:mm")),
                },
                FontFamily = Face,
                FontSize = DesignTokens.TypeCaption,
                Opacity = 0.5,
                Foreground = Brush("MutedForeground", Color.FromRgb(0x9A, 0x9A, 0x9A)),
            };
            Grid.SetColumn(when, 1);
            facts.Children.Add(when);
        }

        block.Children.Add(facts);

        if (!spent && BurnRate.For(window, DateTimeOffset.Now) is { TimeToExhausted: { } eta } && eta > TimeSpan.Zero)
        {
            block.Children.Add(new TextBlock
            {
                Text = eta.TotalMinutes < 90
                    ? Ui.RunsOutInMinutes(Math.Max(1, (int)Math.Round(eta.TotalMinutes)))
                    : Ui.WontLastWindow,
                FontFamily = Face,
                FontSize = 11.5,
                Margin = new Thickness(0, 7, 0, 0),
                Foreground = UsagePaint.BrushFor(0.9, false),
                Opacity = 0.9,
            });
        }

        return block;
    }

    private static TextBlock Note(string text, double opacity, Thickness margin) => new()
    {
        Text = text,
        FontFamily = Face,
        FontSize = DesignTokens.TypeCaption,
        Opacity = opacity,
        Margin = margin,
        Foreground = Brush("MutedForeground", Color.FromRgb(0x9A, 0x9A, 0x9A)),
        TextWrapping = TextWrapping.Wrap,
    };

    private static string Describe(UsageWindow window)
    {
        var name = window.Kind switch
        {
            UsageWindowKind.FiveHour => Ui.LimitFiveHour,
            UsageWindowKind.Weekly => Ui.LimitWeekly,
            UsageWindowKind.Monthly => Ui.LimitMonthly,
            UsageWindowKind.Daily => Ui.LimitDaily,
            UsageWindowKind.Spend => Ui.LimitSpend,
            UsageWindowKind.Balance => Ui.LimitBalance,
            UsageWindowKind.Messages => Ui.LimitMessages,
            _ => Ui.LimitOther,
        };
        return window.Scope is { } scope ? $"{name} · {scope}" : name;
    }

    private static readonly FontFamily Face = new("Segoe UI Variable Display, Segoe UI");

    private static Brush Brush(string key, Color fallback)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush brush) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}

/// <summary>One limit's bar. A reading with no windows never creates one.</summary>
public sealed class UsageMeter : Border
{
    public double FillFraction { get; }

    public UsageMeter(double fraction, Brush accent)
    {
        FillFraction = Math.Clamp(fraction, 0, 1);
        Height = 5;
        Margin = new Thickness(0, 8, 0, 8);
        CornerRadius = new CornerRadius(3);
        var track = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        track.Freeze();
        Background = track;
        ClipToBounds = true;
        var fill = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = accent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        SizeChanged += (_, _) => fill.Width = Math.Max(0, ActualWidth * FillFraction);
        Child = fill;
    }
}
