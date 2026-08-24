using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using UpdateHub.Core;
using UpdateHub.Models;
// WinForms is referenced for the tray notification icon, which puts System.Drawing.Color
// in scope alongside the WPF one.
using Color = System.Windows.Media.Color;

namespace UpdateHub.UI.Converters;

/// <summary>
/// Status colours, tuned for a light surface. Each is dark enough that white text on it
/// stays legible as a pill, and saturated enough to read against white as a swatch.
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static Color ColorFor(AppStatus status) => status switch
    {
        AppStatus.Current => Color.FromRgb(0x17, 0x84, 0x4B),        // green
        AppStatus.Minor => Color.FromRgb(0xB2, 0x6A, 0x00),          // amber
        AppStatus.Outdated => Color.FromRgb(0xC4, 0x55, 0x1F),       // orange
        AppStatus.VeryOutdated => Color.FromRgb(0xC0, 0x39, 0x2B),   // red
        AppStatus.Major => Color.FromRgb(0x9E, 0x22, 0x54),          // deep rose
        AppStatus.Ignored => Color.FromRgb(0x8A, 0x91, 0x9C),        // grey
        _ => Color.FromRgb(0x6B, 0x74, 0x82),                        // unknown
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = new SolidColorBrush(ColorFor(value is AppStatus s ? s : AppStatus.Unknown));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Priority colours. This is the loudest thing on screen by design — priority is the
/// answer to "what needs my attention", so nothing else competes with it.
/// </summary>
public sealed class PriorityBrushConverter : IValueConverter
{
    public static Color ColorFor(UpdatePriority priority) => priority switch
    {
        UpdatePriority.Critical => Color.FromRgb(0xC0, 0x2A, 0x2A),
        UpdatePriority.High => Color.FromRgb(0xD1, 0x6C, 0x10),
        UpdatePriority.Recommended => Color.FromRgb(0xB2, 0x8A, 0x00),
        UpdatePriority.Optional => Color.FromRgb(0x5A, 0x77, 0x96),
        UpdatePriority.Ignored => Color.FromRgb(0x9A, 0xA1, 0xAC),
        _ => Color.FromRgb(0x17, 0x84, 0x4B),   // up to date
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = new SolidColorBrush(ColorFor(value is UpdatePriority p ? p : UpdatePriority.None));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Health score colour, matching the same green-to-red progression.</summary>
public sealed class HealthBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int pct = value is int i ? i : 0;
        var color = pct >= 90 ? Color.FromRgb(0x17, 0x84, 0x4B)
                  : pct >= 75 ? Color.FromRgb(0x5A, 0x8F, 0x2E)
                  : pct >= 55 ? Color.FromRgb(0xB2, 0x6A, 0x00)
                  : Color.FromRgb(0xC0, 0x39, 0x2B);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Category tint. Deliberately desaturated: categories are context, not urgency, and
/// colouring them as loudly as status trained the eye to stop noticing what mattered.
/// Used only for the thin stripe beside a name.
/// </summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    public static Color ColorFor(AppCategory category) => category switch
    {
        AppCategory.Development => Color.FromRgb(0x7C, 0x93, 0xB8),
        AppCategory.Creative => Color.FromRgb(0x9E, 0x8C, 0xC0),
        AppCategory.Game => Color.FromRgb(0xC0, 0x9A, 0x7C),
        AppCategory.Media => Color.FromRgb(0x7C, 0xAF, 0xB0),
        AppCategory.Productivity => Color.FromRgb(0x86, 0xA8, 0x99),
        AppCategory.Communication => Color.FromRgb(0x88, 0xA4, 0xC0),
        AppCategory.Browser => Color.FromRgb(0x93, 0x9C, 0xC4),
        AppCategory.Security => Color.FromRgb(0xBC, 0x94, 0xA9),
        AppCategory.Utility => Color.FromRgb(0xA3, 0xAB, 0xB8),
        AppCategory.Runtime => Color.FromRgb(0xAE, 0xAC, 0xBC),
        AppCategory.Driver => Color.FromRgb(0xB4, 0xAC, 0x9C),
        AppCategory.System => Color.FromRgb(0xA8, 0xAE, 0xB8),
        _ => Color.FromRgb(0xC2, 0xC8, 0xD2),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = new SolidColorBrush(ColorFor(value is AppCategory c ? c : AppCategory.Other));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Maps a 0-100 confidence onto the pixel width of a 62px track.</summary>
public sealed class ConfidenceWidthConverter : IValueConverter
{
    private const double Track = 62;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int pct = value is int i ? i : 0;
        return Math.Max(2, Track * Math.Clamp(pct, 0, 100) / 100.0);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>How many of the six gauge segments are lit for a given age band.</summary>
public static class AgeGauge
{
    public const int Segments = 6;

    public static int FilledFor(AgeBand band) => band switch
    {
        AgeBand.Current => 1,
        AgeBand.Recent => 2,
        AgeBand.GettingOld => 3,
        AgeBand.Old => 4,
        AgeBand.VeryOutdated => 6,
        _ => 0,
    };
}

/// <summary>
/// Lights one segment of the age gauge. The gauge is drawn from real rectangles rather
/// than block-drawing characters, because glyph width and font fallback made the text
/// version render as an unreadable smudge a few pixels wide.
/// </summary>
public sealed class AgeSegmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var band = value is AgeBand b ? b : AgeBand.Unknown;
        if (!int.TryParse(parameter as string, out var index)) return 0.18;
        // Unlit segments stay faintly visible so the gauge reads as a scale, not a bar.
        return index <= AgeGauge.FilledFor(band) ? 1.0 : 0.18;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Install footprint as a four-step weight bar.</summary>
public sealed class SizeTierOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tier = value is SizeTier t ? t : SizeTier.Unknown;
        if (!int.TryParse(parameter as string, out var index)) return 0.18;
        int filled = tier switch
        {
            SizeTier.Small => 1,
            SizeTier.Medium => 2,
            SizeTier.Large => 3,
            SizeTier.VeryLarge => 4,
            _ => 0,
        };
        return index <= filled ? 1.0 : 0.18;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Green through red as confidence drops, so a weak number reads as weak.</summary>
public sealed class ConfidenceBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int pct = value is int i ? i : 0;
        var color = pct >= 90 ? Color.FromRgb(0x17, 0x84, 0x4B)
                  : pct >= 70 ? Color.FromRgb(0x5A, 0x8F, 0x2E)
                  : pct >= 50 ? Color.FromRgb(0xB2, 0x6A, 0x00)
                  : pct >= 30 ? Color.FromRgb(0xC4, 0x55, 0x1F)
                  : Color.FromRgb(0xC0, 0x39, 0x2B);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility.Collapsed;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value is null
            || (value is string s && string.IsNullOrWhiteSpace(s))
            || (value is int n && n == 0);

        // ConverterParameter=invert shows the element only when the value IS empty,
        // which is how the "nothing selected" placeholder is driven.
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
