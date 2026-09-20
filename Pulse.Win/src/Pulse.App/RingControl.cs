using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.Core.Providers;

namespace Pulse.App;

/// <summary>
/// One usage ring on the rail. Rendered with an ArcSegment Path rebuilt only when
/// progress changes (not per frame) per the upstream "no idle animation" constraint.
/// Lives in its own file to keep RailWindow.xaml.cs focused on window behavior.
/// </summary>
public sealed class RingControl : StackPanel
{
    private const double Radius = 26;
    private const double CenterOffset = 26;

    private readonly ArcSegment _arc = new()
    {
        SweepDirection = SweepDirection.Clockwise,
        IsLargeArc = false,
        Size = new System.Windows.Size(Radius, Radius),
    };
    private readonly PathFigure _figure;
    private readonly TextBlock _percent = CreateCenteredText(13, FontWeights.SemiBold, 1.0);
    private readonly TextBlock _caption = CreateCenteredText(9, FontWeights.Normal, 0.7);
    private readonly Path _ringPath;

    public RingControl(string title)
    {
        Margin = new Thickness(0, 6, 0, 6);

        var figure = new PathFigure
        {
            StartPoint = new System.Windows.Point(Radius, CenterOffset),
            Segments = { _arc },
        };
        _figure = figure;
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        _ringPath = new Path
        {
            StrokeThickness = 5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = geometry,
            Stroke = ResourceBrush("RingProgress"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Width = Radius * 2 + 10,
            Height = Radius * 2 + 10,
            Margin = new Thickness(0, 2, 0, 2),
        };

        var titleBlock = CreateCenteredText(9, FontWeights.Normal, 0.7);
        titleBlock.Text = title;

        Children.Add(titleBlock);
        Children.Add(_ringPath);
        Children.Add(_percent);
        Children.Add(_caption);
    }

    public void SetProgress(double usedFraction, bool exhausted)
    {
        var brush = ResourceBrush(exhausted ? "RingExhausted" : "RingProgress");
        _percent.Text = $"{Math.Round(usedFraction * 100)}%";
        var angle = Math.Clamp(usedFraction, 0.0, 1.0) * 2 * Math.PI;
        _arc.Point = new System.Windows.Point(
            Radius + Radius * Math.Sin(angle),
            CenterOffset - Radius * Math.Cos(angle));
        _arc.IsLargeArc = angle > Math.PI;
        _ringPath.Stroke = brush;
        _percent.Foreground = brush;
    }

    public void SetCaption(string text) => _caption.Text = text;

    public void SetStale()
    {
        _percent.Text = "–";
        _caption.Text = "stale";
    }

    private static TextBlock CreateCenteredText(double size, FontWeight weight, double opacity) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Opacity = opacity,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        Foreground = ResourceBrush("RailForeground"),
    };

    private static System.Windows.Media.Brush ResourceBrush(string key) =>
        (System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush)
        ?? new SolidColorBrush(Colors.Gray);

    private static string FormatPercent(double fraction) =>
        fraction.ToString("0%", CultureInfo.InvariantCulture);
}
