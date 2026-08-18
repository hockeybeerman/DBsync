using System.Globalization;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// Parses and renders the wizard's free-text upload limit — "8 MB/s", "512KB", "Unlimited".
/// <para>
/// The grammar matches <c>RateLimiter.ParseLimit</c> on the service side. Two implementations of
/// one grammar is a smell; the reason it is duplicated rather than shared is that the contract
/// assembly carries data, not behaviour, and pulling a parser into it for one field would make
/// every client depend on the service's text conventions. If a third caller ever needs it, move
/// it into the contract.
/// </para>
/// </summary>
public static class RateText
{
    public static long? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim().ToLowerInvariant().Replace("/s", "").Replace("ps", "").Trim();
        if (value is "0" or "none" or "unlimited") return null;

        var unit = 1L;
        if (value.EndsWith("gb")) { unit = 1024L * 1024 * 1024; value = value[..^2]; }
        else if (value.EndsWith("mb")) { unit = 1024L * 1024; value = value[..^2]; }
        else if (value.EndsWith("kb")) { unit = 1024L; value = value[..^2]; }
        else if (value.EndsWith("b")) { value = value[..^1]; }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
               && number > 0
            ? (long)(number * unit)
            : null;
    }

    public static string Format(long? bytesPerSecond)
    {
        if (bytesPerSecond is not > 0) return "Unlimited";

        var bytes = bytesPerSecond.Value;
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.#} GB/s";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.#} MB/s";
        if (bytes >= 1024) return $"{bytes / 1024d:0.#} KB/s";
        return $"{bytes} B/s";
    }
}
