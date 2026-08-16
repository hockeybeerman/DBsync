using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DBsync.Tray.Text;

/// <summary>
/// Letter spacing for <see cref="TextBlock"/>, in em.
/// <para>
/// WPF has no equivalent of CSS <c>letter-spacing</c> (UWP's <c>CharacterSpacing</c> was never
/// brought across), but the design depends on it: uppercase kickers carry 0.08–0.1em and
/// headings carry −0.015em. This rebuilds the block's inlines, separating each character with a
/// zero-width spacer whose margin supplies the gap — which works for negative tracking too.
/// </para>
/// <para>
/// Only worth applying to short strings. It is used on kickers, table headings and h4, never on
/// body copy or paths.
/// </para>
/// </summary>
public static class Tracking
{
    /// <summary>Spacing in em, multiplied by the block's font size at build time.</summary>
    public static readonly DependencyProperty AmountProperty = DependencyProperty.RegisterAttached(
        "Amount", typeof(double), typeof(Tracking),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure, OnAmountChanged));

    /// <summary>The untracked source string. Cached because rebuilding inlines rewrites Text.</summary>
    private static readonly DependencyProperty SourceTextProperty = DependencyProperty.RegisterAttached(
        "SourceText", typeof(string), typeof(Tracking), new PropertyMetadata(null));

    /// <summary>Re-entrancy guard: our own writes to Text/Inlines must not re-trigger a rebuild.</summary>
    private static readonly DependencyProperty IsRebuildingProperty = DependencyProperty.RegisterAttached(
        "IsRebuilding", typeof(bool), typeof(Tracking), new PropertyMetadata(false));

    public static void SetAmount(DependencyObject element, double value) =>
        element.SetValue(AmountProperty, value);

    public static double GetAmount(DependencyObject element) =>
        (double)element.GetValue(AmountProperty);

    private static void OnAmountChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block) return;

        var textDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        var sizeDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.FontSizeProperty, typeof(TextBlock));

        // Tracking is applied declaratively in a style, so the element outlives any one value.
        // Subscribe once when tracking is switched on and drop the handlers when it goes to zero,
        // rather than leaving a listener on every TextBlock in the app.
        var wasTracking = (double)e.OldValue != 0d;
        var isTracking = (double)e.NewValue != 0d;

        if (isTracking && !wasTracking)
        {
            textDescriptor.AddValueChanged(block, OnSourceChanged);
            sizeDescriptor.AddValueChanged(block, OnSourceChanged);
            SetSourceText(block, block.Text);
        }
        else if (!isTracking && wasTracking)
        {
            textDescriptor.RemoveValueChanged(block, OnSourceChanged);
            sizeDescriptor.RemoveValueChanged(block, OnSourceChanged);
        }

        Rebuild(block);
    }

    private static void OnSourceChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock block) return;
        if ((bool)block.GetValue(IsRebuildingProperty)) return;

        SetSourceText(block, block.Text);
        Rebuild(block);
    }

    private static void SetSourceText(TextBlock block, string? text) =>
        block.SetValue(SourceTextProperty, text ?? "");

    private static void Rebuild(TextBlock block)
    {
        var source = (string?)block.GetValue(SourceTextProperty) ?? "";
        var em = GetAmount(block);

        block.SetValue(IsRebuildingProperty, true);
        try
        {
            if (em == 0d || source.Length < 2)
            {
                block.Inlines.Clear();
                block.Text = source;
                return;
            }

            var gap = em * block.FontSize;
            block.Inlines.Clear();

            for (var i = 0; i < source.Length; i++)
            {
                block.Inlines.Add(new Run(source[i].ToString()));

                // No spacer after the last character — trailing tracking would throw centred and
                // right-aligned text off by one gap.
                if (i < source.Length - 1) block.Inlines.Add(Spacer(gap));
            }
        }
        finally
        {
            block.SetValue(IsRebuildingProperty, false);
        }
    }

    /// <summary>
    /// A zero-width inline whose left margin is the gap. A margin can go negative, which a Width
    /// cannot — that is what lets the same mechanism handle the −0.015em on headings.
    /// </summary>
    private static InlineUIContainer Spacer(double gap) => new(new FrameworkElement
    {
        Width = 0,
        Height = 0,
        Margin = new Thickness(gap, 0, 0, 0),
    })
    {
        BaselineAlignment = BaselineAlignment.Baseline,
    };
}
