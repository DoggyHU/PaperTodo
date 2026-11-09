using System.Windows.Media;
using Microsoft.Win32;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// One concrete color set for the plugin UI. The host only hands plugins a
/// <see cref="PaperBodyTheme"/> (the user's current paper palette); the dialog reuses the last one it
/// saw so the popup looks like the paper it belongs to instead of a stock white dialog.
/// </summary>
internal sealed record BulkPalette(
    Color Paper,
    Color Surface,
    Color Border,
    Color Text,
    Color WeakText,
    Color Accent,
    Color Danger,
    string FontFamily,
    double FontScale)
{
    private const string DefaultFont = "Microsoft YaHei UI";

    /// <summary>Warm-paper fallback that matches the app's default palette.</summary>
    public static BulkPalette Fallback { get; } = IsSystemDark()
        ? new BulkPalette(
            Paper: Color.FromRgb(33, 31, 28),
            Surface: Color.FromRgb(45, 42, 38),
            Border: Color.FromRgb(76, 69, 61),
            Text: Color.FromRgb(231, 224, 212),
            WeakText: Color.FromRgb(146, 137, 123),
            Accent: Color.FromRgb(168, 142, 106),
            Danger: Color.FromRgb(230, 110, 90),
            FontFamily: DefaultFont,
            FontScale: 1.0)
        : new BulkPalette(
            Paper: Color.FromRgb(255, 249, 234),
            Surface: Color.FromRgb(247, 237, 210),
            Border: Color.FromRgb(224, 206, 167),
            Text: Color.FromRgb(51, 41, 30),
            WeakText: Color.FromRgb(138, 122, 99),
            Accent: Color.FromRgb(140, 115, 80),
            Danger: Color.FromRgb(176, 90, 70),
            FontFamily: DefaultFont,
            FontScale: 1.0);

    /// <summary>Last theme reported by a live body session; the app runtime uses it for the dialog.</summary>
    public static BulkPalette Current { get; set; } = Fallback;

    public static BulkPalette From(PaperBodyTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var paper = Parse(theme.PaperColor, Fallback.Paper);
        return new BulkPalette(
            Paper: paper,
            Surface: Mix(paper, Parse(theme.BorderColor, Fallback.Surface), 0.35),
            Border: Parse(theme.BorderColor, Fallback.Border),
            Text: Parse(theme.TextColor, Fallback.Text),
            WeakText: Parse(theme.WeakTextColor, Fallback.WeakText),
            Accent: Parse(theme.AccentColor, Fallback.Accent),
            Danger: Fallback.Danger,
            FontFamily: string.IsNullOrWhiteSpace(theme.FontFamily)
                ? DefaultFont
                : theme.FontFamily,
            FontScale: theme.FontScale > 0.1 && theme.FontScale < 4 ? theme.FontScale : 1.0);
    }

    public SolidColorBrush PaperBrush => Brush(Paper);
    public SolidColorBrush SurfaceBrush => Brush(Surface);
    public SolidColorBrush BorderBrush => Brush(Border);
    public SolidColorBrush TextBrush => Brush(Text);
    public SolidColorBrush WeakBrush => Brush(WeakText);
    public SolidColorBrush AccentBrush => Brush(Accent);
    public SolidColorBrush DangerBrush => Brush(Danger);
    public SolidColorBrush OnAccentBrush => Brush(ContrastOn(Accent));

    public FontFamily Font => new(FontFamily is { Length: > 0 } family ? family : DefaultFont);

    public double Scale(double value) => value * FontScale;

    private static readonly Dictionary<long, SolidColorBrush> Brushes = [];

    private static SolidColorBrush Brush(Color color)
    {
        long key = ((long)color.A << 24) | ((long)color.R << 16) | ((long)color.G << 8) | color.B;
        lock (Brushes)
        {
            if (Brushes.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[key] = brush;
            return brush;
        }
    }

    private static Color ContrastOn(Color background)
    {
        var luminance = (0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B);
        return luminance > 150 ? Color.FromRgb(32, 28, 22) : Color.FromRgb(255, 253, 247);
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Color Parse(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
