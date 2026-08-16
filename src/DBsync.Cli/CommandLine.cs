namespace DBsync.Cli;

/// <summary>Bare-bones <c>--flag value</c> / <c>--switch</c> parser. No dependency worth taking for this.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command, IReadOnlyList<string> positionals)
    {
        Command = command;
        Positionals = positionals;
    }

    public string Command { get; }
    public IReadOnlyList<string> Positionals { get; }

    public static CommandLine Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "help";
        var positionals = new List<string>();
        var line = new CommandLine(command, positionals);

        for (var i = command == "help" ? 0 : 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            var name = token[2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            line._options[name] = hasValue ? args[++i] : null;
        }

        return line;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string Require(string name) =>
        Get(name) ?? throw new ArgumentException($"--{name} is required.");

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), out var value) ? value : fallback;
}
