using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DBsync.Tray.Icons;
using DBsync.Tray.Theming;

namespace DBsync.Tray.Dev;

public partial class ThemeGalleryWindow : Window
{
    private readonly ThemeManager _theme = ThemeManager.Current;

    public ThemeGalleryWindow()
    {
        InitializeComponent();

        _theme.ThemeChanged += OnThemeChanged;
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => _theme.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, ThemeVariant variant) => Refresh();

    private void OnLight(object sender, RoutedEventArgs e) => _theme.Mode = AppearanceMode.Light;

    private void OnDark(object sender, RoutedEventArgs e) => _theme.Mode = AppearanceMode.Dark;

    private void OnSystem(object sender, RoutedEventArgs e) => _theme.Mode = AppearanceMode.MatchWindows;

    private void Refresh()
    {
        SublineText.Text = _theme.AppearanceSubline;

        var results = TokenAudit.Run(_theme.Resolved);
        var passed = results.Count(r => r.Passed);

        AuditSummary.Text = passed == results.Count
            ? $"{passed}/{results.Count} match the design's {_theme.Resolved.ToString().ToLowerInvariant()} column."
            : $"{passed}/{results.Count} match — {results.Count - passed} differ from the design's table.";
        AuditSummary.Foreground = (Brush)FindResource(
            passed == results.Count ? "TextMuted55Brush" : "AccentBrush");

        TokenList.Items.Clear();
        foreach (var check in results) TokenList.Items.Add(BuildRow(check));

        IconSheet.Items.Clear();
        foreach (var key in IconGlyphs.All) IconSheet.Items.Add(BuildIconTile(key));
    }

    /// <summary>One icon tile: the glyph, the design's name for it, and the codepoint.</summary>
    private UIElement BuildIconTile(IconKey key)
    {
        var stack = new StackPanel
        {
            Width = 132,
            Margin = new Thickness(0, 0, 8.4, 11.2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        stack.Children.Add(new Icon
        {
            Key = key,
            Size = 22,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        });

        var name = Text(key.ToString(), "MetaTextStyle");
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.TextAlignment = TextAlignment.Center;
        name.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(name);

        var code = Text(IconGlyphs.Codepoint(key), "MonoTextStyle");
        code.HorizontalAlignment = HorizontalAlignment.Center;
        code.FontSize = 10;
        code.Foreground = (Brush)FindResource("TextMuted50Brush");
        stack.Children.Add(code);

        return stack;
    }

    /// <summary>One swatch row: colour chip, role name, resource key, and the resolved value.</summary>
    private UIElement BuildRow(TokenCheck check)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5.6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chip = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = FindResource(check.Key) as Brush ?? Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("DividerBrush"),
        };
        Grid.SetColumn(chip, 0);

        var role = Text(check.Role, "LabelTextStyle");
        Grid.SetColumn(role, 1);

        var key = Text(check.Key, "MonoTextStyle");
        Grid.SetColumn(key, 2);

        var value = Text(
            check.Passed ? check.Actual : $"{check.Actual}  ≠  {check.Expected}",
            "MonoTextStyle");
        value.Foreground = (Brush)FindResource(check.Passed ? "TextMuted55Brush" : "AccentBrush");
        Grid.SetColumn(value, 3);

        row.Children.Add(chip);
        row.Children.Add(role);
        row.Children.Add(key);
        row.Children.Add(value);
        return row;
    }

    private TextBlock Text(string content, string styleKey) => new()
    {
        Text = content,
        Style = (Style)FindResource(styleKey),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
