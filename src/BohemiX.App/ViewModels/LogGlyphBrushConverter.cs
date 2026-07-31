using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BohemiX.App.ViewModels;

/// <summary>
/// Converts a log entry's <c>Applied</c> boolean into a brush so the ✓/✗ glyph
/// is green when an action executed and red when it was rejected. Used by the
/// Alchemy bench action-log template.
/// </summary>
public sealed class LogGlyphBrushConverter : IValueConverter
{
    /// <summary>Shared instance for XAML <c>{x:Static}</c> binding.</summary>
    public static readonly LogGlyphBrushConverter Instance = new();

    private static readonly IBrush AppliedBrush = new SolidColorBrush(0xFF16A34A);
    private static readonly IBrush RejectedBrush = new SolidColorBrush(0xFFB91C1C);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool applied && applied ? AppliedBrush : RejectedBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
