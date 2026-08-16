using System.Text;
using System.Text.RegularExpressions;
using DBsync.Service.Configuration;

namespace DBsync.Service.Engine;

/// <summary>
/// Matches the pair's exclude patterns against relative paths. Supports the three shapes the
/// wizard's placeholder advertises — <c>*.tmp</c> (name glob), <c>~$*</c> (name prefix glob),
/// and <c>node_modules/</c> (directory subtree) — plus path globs containing a separator.
/// </summary>
public sealed class ExclusionMatcher
{
    private readonly List<Regex> _nameRules = new();
    private readonly List<Regex> _pathRules = new();
    private readonly HashSet<string> _directoryNames = new(StringComparer.OrdinalIgnoreCase);

    public ExclusionMatcher(IEnumerable<string> patterns)
    {
        // DBsync's own artefacts are never candidates for sync, whatever the user configured.
        _directoryNames.Add(ServicePaths.VersionFolderName);
        _nameRules.Add(Compile("*" + ServicePaths.TempSuffix));

        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim();
            if (string.IsNullOrEmpty(pattern)) continue;

            if (pattern.EndsWith('/') || pattern.EndsWith('\\'))
            {
                var name = pattern.TrimEnd('/', '\\');
                if (name.Length == 0) continue;

                if (name.IndexOfAny(new[] { '*', '?' }) < 0) _directoryNames.Add(name);
                else _nameRules.Add(Compile(name));
                continue;
            }

            if (pattern.Contains('/') || pattern.Contains('\\')) _pathRules.Add(Compile(Normalise(pattern)));
            else _nameRules.Add(Compile(pattern));
        }
    }

    /// <summary>True when a relative path (file or directory) should be skipped entirely.</summary>
    public bool IsExcluded(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;

        var normalised = Normalise(relativePath);
        if (normalised.Length == 0) return false;

        // A hit on any segment excludes that node and everything beneath it, so an excluded
        // directory never has to be re-tested for each file inside it.
        foreach (var segment in normalised.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_directoryNames.Contains(segment)) return true;
            if (_nameRules.Any(rule => rule.IsMatch(segment))) return true;
        }

        return _pathRules.Any(rule => rule.IsMatch(normalised));
    }

    private static string Normalise(string path) => path.Replace('\\', '/').Trim('/');

    private static Regex Compile(string glob)
    {
        var builder = new StringBuilder("^");
        foreach (var character in glob)
        {
            builder.Append(character switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(character.ToString()),
            });
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
