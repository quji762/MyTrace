using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;
using Pulse.Core.Providers;

namespace Pulse.App;

/// <summary>
/// The provider marks the Mac rail draws, loaded from the same SVG files.
/// They are template shapes: fill them with the label colour.
/// </summary>
public static class ProviderGlyph
{
    private static readonly Dictionary<string, Geometry?> Cache = new();
    private static readonly object Gate = new();

    public static string ResourceName(ProviderId id) => id switch
    {
        ProviderId.ClaudeCode => "claude",
        ProviderId.Codex => "openai",
        ProviderId.Antigravity => "antigravity",
        ProviderId.Cursor => "cursor",
        ProviderId.OpenCodeGo => "opencode",
        ProviderId.KimiCode => "kimi",
        ProviderId.OllamaCloud => "ollama",
        ProviderId.Zai => "zai",
        ProviderId.GlmCoding => "qingyan",
        ProviderId.MiniMax or ProviderId.MiniMaxCN => "minimax",
        ProviderId.Copilot => "github",
        ProviderId.Grok => "grok",
        ProviderId.GrokBot => "xai",
        ProviderId.Volcengine => "volcengine",
        ProviderId.CommandCode => "commandcode",
        ProviderId.DeepSeek => "deepseek",
        ProviderId.Devin => "devin",
        ProviderId.XiaomiMiMo => "xiaomimimo",
        _ => "claude",
    };

    public static Geometry? GeometryFor(ProviderId id) => GeometryFor(ResourceName(id));

    public static Geometry? GeometryFor(string name)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            var geometry = Load(name);
            if (geometry is not null && geometry.CanFreeze) geometry.Freeze();
            Cache[name] = geometry;
            return geometry;
        }
    }

    public static void Draw(DrawingContext dc, ProviderId id, Rect box, Brush brush)
    {
        var geometry = GeometryFor(id);
        if (geometry is null || box.Width <= 0 || box.Height <= 0) return;
        var view = ViewBox(name: ResourceName(id));
        var scale = Math.Min(box.Width / view.Width, box.Height / view.Height);
        var x = box.X + (box.Width - view.Width * scale) / 2 - view.X * scale;
        var y = box.Y + (box.Height - view.Height * scale) / 2 - view.Y * scale;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
        dc.DrawGeometry(brush, null, geometry);
        dc.Pop();
    }

    private static Rect ViewBox(string name)
    {
        var svg = Read(name);
        if (svg is null) return new Rect(0, 0, 24, 24);
        var match = Regex.Match(svg, "viewBox\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (!match.Success) return new Rect(0, 0, 24, 24);
        var parts = match.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var h)
            || w <= 0 || h <= 0)
            return new Rect(0, 0, 24, 24);
        return new Rect(x, y, w, h);
    }

    private static Geometry? Load(string name)
    {
        var svg = Read(name);
        if (svg is null) return null;
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        foreach (Match match in Regex.Matches(svg, "\\sd\\s*=\\s*\"([^\"]+)\""))
        {
            try
            {
                group.Children.Add(Geometry.Parse(match.Groups[1].Value));
            }
            catch (FormatException)
            {
                // One bad path should not drop the rest of the mark.
            }
        }

        return group.Children.Count == 0 ? null : group;
    }

    private static string? Read(string name)
    {
        var stream = typeof(ProviderGlyph).Assembly.GetManifestResourceStream($"Pulse.Icons.{name}.svg");
        if (stream is null) return null;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>A template icon for one provider, used in settings rows and the hover card.</summary>
public sealed class GlyphView : FrameworkElement
{
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(ProviderId), typeof(GlyphView),
        new FrameworkPropertyMetadata(ProviderId.ClaudeCode, FrameworkPropertyMetadataOptions.AffectsRender));

    public ProviderId Provider
    {
        get => (ProviderId)GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsNaN(Width) ? 16 : Width;
        var height = double.IsNaN(Height) ? 16 : Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = System.Windows.Application.Current?.TryFindResource("RailForeground") as Brush
                    ?? Brushes.White;
        ProviderGlyph.Draw(dc, Provider, new Rect(RenderSize), brush);
    }
}
