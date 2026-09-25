using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pulse.Core.Providers;

namespace Pulse.App;

/// <summary>
/// One rail ring: a 36px usage arc, the provider's mark in the middle, and the
/// percent underneath. The name stays on the hover card. Colour is how close
/// the limit is, the same steps as the Mac panel.
/// </summary>
public sealed class RingControl : StackPanel
{
    public const double Diameter = 36;
    public const double Stroke = 4;
    public const double ItemHeight = Diameter + 6 + 16;

    private readonly Face _face;
    private readonly TextBlock _percent;

    public ProviderId Provider { get; }

    public RingControl(ProviderId provider)
    {
        Provider = provider;
        Width = 44;
        Orientation = System.Windows.Controls.Orientation.Vertical;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        Margin = new Thickness(0, 0, 0, 18);
        // Transparent (not null) so the WHOLE ring hit-tests: the face draws a
        // fill-less ellipse in OnRender, and a panel without a background brush
        // is hit-testable only where its children are — hovering the circle
        // itself did nothing, only the number underneath summoned the card.
        Background = Brushes.Transparent;

        _face = new Face(provider)
        {
            Width = Diameter,
            Height = Diameter,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        _percent = new TextBlock
        {
            Text = "—",
            FontFamily = DesignTokens.UiFont,
            FontSize = DesignTokens.TypePercent,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
            Opacity = 0.4,
            Foreground = LabelBrush(),
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(_percent, FontNumeralAlignment.Tabular);
        Children.Add(_face);
        Children.Add(_percent);

        MouseEnter += (_, _) => _face.Hovered = true;
        MouseLeave += (_, _) => _face.Hovered = false;
        System.Windows.Automation.AutomationProperties.SetName(this, provider.ToString());
        System.Windows.Automation.AutomationProperties.SetHelpText(this, "Usage ring");
    }

    public void SetProgress(double usedFraction, bool exhausted)
    {
        var fraction = Math.Clamp(usedFraction, 0, 1);
        _face.SetReading(fraction, exhausted);
        _percent.Text = Figure(fraction, exhausted);
        _percent.Opacity = 1;
        _percent.Foreground = exhausted || fraction >= UsagePaint.Warning
            ? Frozen(UsagePaint.For(fraction, exhausted))
            : LabelBrush();
    }

    public void SetStale()
    {
        _face.ClearReading();
        _percent.Text = "—";
        _percent.Opacity = 0.4;
        _percent.Foreground = LabelBrush();
    }

    /// <summary>
    /// The number under the ring. Below the warning step it is how much is used.
    /// From there up it is how much is left, which is the same reported figure.
    /// A spent limit says so instead of "0% left".
    /// </summary>
    public static string Figure(double usedFraction, bool exhausted)
    {
        var used = Math.Clamp(usedFraction, 0, 1);
        if (exhausted || used >= 1) return Ui.Spent;
        if (used >= UsagePaint.Warning)
            return Ui.PercentLeft(Pulse.Core.JsonReport.DisplayPercent(1 - used));
        return Ui.PercentUsed(Pulse.Core.JsonReport.DisplayPercent(used));
    }

    /// <summary>The figure under the ring: a percent, or a dash when nothing was read.</summary>
    public string FigureText => _percent.Text;

    /// <summary>True once a reading has put a number on this ring.</summary>
    public bool HasFigure => _percent.Text != "—";

    /// <summary>A later read came back empty. Keep the last number instead of blanking it.</summary>
    public void Hold() => _percent.Opacity = 0.55;

    /// <summary>The colour the arc is stroked with, or null when the track is empty.</summary>
    public Color? ArcColor => _face.ArcColor;

    private static Brush LabelBrush() =>
        (System.Windows.Application.Current?.TryFindResource("RailForeground") as Brush) ?? Brushes.White;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Face : FrameworkElement
    {
        private readonly ProviderId _provider;
        private double _fraction;
        private bool _hasReading;
        private bool _exhausted;
        private bool _hovered;
        private Color? _arcColor;

        public Face(ProviderId provider) => _provider = provider;

        public Color? ArcColor => _arcColor;

        public bool Hovered
        {
            set
            {
                if (_hovered == value) return;
                _hovered = value;
                RenderTransformOrigin = new Point(0.5, 0.5);
                var to = value ? 1.06 : 1.0;
                var scale = new ScaleTransform(1, 1);
                RenderTransform = scale;
                var duration = TimeSpan.FromMilliseconds(DesignTokens.MotionQuick);
                var ease = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(to, duration) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(to, duration) { EasingFunction = ease });
            }
        }

        public void SetReading(double fraction, bool exhausted)
        {
            _fraction = fraction;
            _exhausted = exhausted;
            _hasReading = true;
            _arcColor = fraction > 0 || exhausted ? UsagePaint.For(fraction, exhausted) : null;
            InvalidateVisual();
        }

        public void ClearReading()
        {
            _hasReading = false;
            _fraction = 0;
            _exhausted = false;
            _arcColor = null;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var radius = (Diameter - Stroke) / 2;
            var center = new Point(Diameter / 2, Diameter / 2);
            var track = (System.Windows.Application.Current?.TryFindResource("RingTrack") as Brush)
                        ?? new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

            // Track is a hair thinner than the progress stroke so the live arc
            // reads as sitting on top of it.
            dc.DrawEllipse(null, new Pen(track, Stroke - 1.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            }, center, radius, radius);

            if (_arcColor is { } colour && _fraction > 0)
            {
                var glow = Frozen(Color.FromArgb(0x30, colour.R, colour.G, colour.B));
                dc.DrawGeometry(null, Pen(glow, Stroke + 3.5), Arc(center, radius, _fraction));
                dc.DrawGeometry(null, Pen(Frozen(colour)), Arc(center, radius, _fraction));
            }

            var label = (System.Windows.Application.Current?.TryFindResource("RailForeground") as Brush) ?? Brushes.White;
            if (!_hasReading && label is SolidColorBrush solid)
            {
                var dim = solid.Color;
                label = new SolidColorBrush(Color.FromArgb(0x55, dim.R, dim.G, dim.B));
            }

            const double icon = 16;
            ProviderGlyph.Draw(dc, _provider, new Rect(center.X - icon / 2, center.Y - icon / 2, icon, icon), label);
        }

        private static Pen Pen(Brush brush, double stroke = Stroke) => new(brush, stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        private static Geometry Arc(Point center, double radius, double fraction)
        {
            var sweep = fraction >= 1 ? Math.PI * 2 - 0.001 : fraction * Math.PI * 2;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + radius * Math.Sin(sweep), center.Y - radius * Math.Cos(sweep));
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = sweep > Math.PI,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
