using System.Text;
using System.Windows;
using System.Windows.Media;
using DBsync.Tray.Theming;

namespace DBsync.Tray.Dev;

public sealed record TokenCheck(string Role, string Key, string Expected, string Actual, bool Passed);

/// <summary>
/// Checks the live resource values against the role table in <c>docs/README.md</c> §Theming.
/// <para>
/// The design is high-fidelity and the two palettes are specified colour by colour, so "the
/// colours match" is a checkable claim rather than something to eyeball. This runs in the
/// gallery and headless via <c>DBsync.Tray.exe --audit</c>.
/// </para>
/// </summary>
public static class TokenAudit
{
    /// <summary>Role → (dark, light), transcribed from the design's table.</summary>
    private static readonly (string Role, string Key, string Dark, string Light)[] Expected =
    {
        ("bg",                      "BackgroundBrush",             "#FF161826", "#FFE4E7F5"),
        ("surface",                 "SurfaceBrush",                "#FF232532", "#FFF3F5FE"),
        ("text",                    "TextBrush",                   "#FFE9E9ED", "#FF292B31"),
        ("accent",                  "AccentBrush",                 "#FF9184D9", "#FF796CBF"),
        ("accent text step (300)",  "AccentTextBrush",             "#FFD2CEFD", "#FF5D5294"),
        ("accent-200",              "AccentEmphasisBrush",         "#FFE7E5FE", "#FF423A6A"),
        ("neutral-400",             "CalmTextBrush",               "#FFB2B6CA", "#FF595D6C"),
        ("divider",                 "DividerBrush",                "#29E9E9ED", "#2E292B31"),
        ("shadow-sm edge",          "EdgeSmBrush",                 "#FF3F424D", "#FFCFD3E5"),
        ("shadow-md edge",          "EdgeMdBrush",                 "#FF595D6C", "#FFCFD3E5"),
        ("shadow-lg edge",          "EdgeLgBrush",                 "#FF9397AB", "#FFB2B6CA"),
        ("progress track",          "ProgressTrackBrush",          "#FF292B31", "#FFCFD3E5"),
        ("tag-neutral bg",          "TagNeutralBackgroundBrush",   "#FF3F424D", "#FFCFD3E5"),
        ("tag-neutral text",        "TagNeutralForegroundBrush",   "#FFF3F5FE", "#FF292B31"),
        ("tag-accent bg",           "TagAccentBackgroundBrush",    "#FF423A6A", "#FFD2CEFD"),
        ("tag-accent text",         "TagAccentForegroundBrush",    "#FFF5F4FF", "#FF2B2741"),
    };

    public static IReadOnlyList<TokenCheck> Run(ThemeVariant variant)
    {
        var results = new List<TokenCheck>(Expected.Length);

        foreach (var (role, key, dark, light) in Expected)
        {
            var expected = variant == ThemeVariant.Dark ? dark : light;
            var actual = Resolve(key);
            results.Add(new TokenCheck(role, key, expected, actual,
                string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)));
        }

        return results;
    }

    private static string Resolve(string key) =>
        Application.Current.TryFindResource(key) is SolidColorBrush brush
            ? brush.Color.ToString()
            : "(missing)";

    /// <summary>
    /// Applies each palette in turn and audits it. Returns the report and whether everything
    /// passed, so a caller can use the exit code.
    /// </summary>
    public static (string Report, bool Passed) RunBothVariants(ThemeManager theme)
    {
        var report = new StringBuilder();
        var allPassed = true;
        var restore = theme.Mode;

        foreach (var mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
        {
            theme.Mode = mode;
            var results = Run(theme.Resolved);
            var failures = results.Count(r => !r.Passed);
            allPassed &= failures == 0;

            report.AppendLine($"── {theme.Resolved} ── {results.Count - failures}/{results.Count} match");
            foreach (var check in results)
            {
                report.AppendLine(check.Passed
                    ? $"   ok    {check.Role,-24} {check.Key,-28} {check.Actual}"
                    : $"   FAIL  {check.Role,-24} {check.Key,-28} expected {check.Expected}, got {check.Actual}");
            }

            report.AppendLine();
        }

        // Match Windows must resolve to whatever the OS is set to right now.
        theme.Mode = AppearanceMode.MatchWindows;
        var followed = theme.Resolved == WindowsTheme.Current;
        allPassed &= followed;
        report.AppendLine($"── Match Windows ── OS reports {WindowsTheme.Current}, app resolved " +
                          $"{theme.Resolved} — {(followed ? "ok" : "FAIL")}");

        theme.Mode = restore;
        return (report.ToString(), allPassed);
    }
}
