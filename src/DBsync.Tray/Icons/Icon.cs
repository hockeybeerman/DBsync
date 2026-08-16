using System.Windows;
using System.Windows.Controls;

namespace DBsync.Tray.Icons;

/// <summary>
/// Renders one design icon. Use this rather than a bare glyph string so every surface goes
/// through the same mapping and a font change is one edit.
/// <para>
/// Usage: <c>&lt;icons:Icon Key="ArrowsClockwise" Size="15"
/// Foreground="{DynamicResource AccentBrush}" /&gt;</c>
/// </para>
/// </summary>
public sealed class Icon : TextBlock
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(IconKey), typeof(Icon),
        new FrameworkPropertyMetadata(IconKey.Check, OnKeyChanged));

    /// <summary>
    /// Glyph size in pixels. The design sizes icons independently of the text beside them
    /// (18px header glyph next to 15px text, 12px inline arrow next to 11px paths), so this is
    /// separate from FontSize rather than inherited.
    /// </summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(14d, OnSizeChanged));

    public Icon()
    {
        FontFamily = IconGlyphs.IconFont;
        FontSize = 14;
        VerticalAlignment = VerticalAlignment.Center;
        TextAlignment = TextAlignment.Center;

        // The icon font's own metrics carry the alignment; letting the app-wide TextBlock style
        // apply tracking or a line height here would nudge glyphs off centre.
        LineHeight = double.NaN;
        Text = IconGlyphs.Glyph(Key);
    }

    public IconKey Key
    {
        get => (IconKey)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnKeyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Icon icon) icon.Text = IconGlyphs.Glyph((IconKey)e.NewValue);
    }

    private static void OnSizeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Icon icon) icon.FontSize = (double)e.NewValue;
    }
}
