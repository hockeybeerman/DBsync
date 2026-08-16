using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;

namespace DBsync.Cli;

/// <summary>Every verb the CLI exposes. This doubles as a worked example of the IPC contract.</summary>
internal static class Commands
{
    public static int Help()
    {
        Console.WriteLine(@"
dbsync — command-line client for the DBsync service

  status                     Show every folder pair and the header status line
  watch                      Stream live push events until Ctrl+C
  add                        Create a folder pair
      --name <text>          Display name (defaults to the local folder's name)
      --local <path>         Local folder (required)
      --share <path>         UNC or mapped-drive destination (required)
      --direction <mode>     two-way (default) | push | pull
      --exclude <list>       Comma-separated patterns, e.g. ""*.tmp,~$*,node_modules/""
      --limit <rate>         Upload limit, e.g. ""8 MB/s"". Omit for unlimited
      --versions <n>         File versions to keep (default 5)
      --vss                  Copy locked files through a volume shadow copy
      --no-watcher           Reconcile only on the periodic sweep
      --user / --password    UNC credentials, filed in Windows Credential Manager
  update --id <id> [...]     Same flags as add; only what you pass is changed
  remove --id <id>           Delete a pair (its activity history is kept)
  pause | resume             Global pause across every pair
  sync [--id <id>]           Queue an immediate full rescan
  activity [--id <id>] [--hours 24] [--limit 40]
  conflicts [--id <id>]
  resolve --conflict <n> --keep <local|share|both> [--all]
  probe --path <path> [--user <u> --password <p>]
  creds --id <id> --user <u> --password <p>

  install [--exe <path>]     Register the Windows service (run as administrator)
  uninstall                  Remove the service registration
  start | stop               Control the installed service

Add --json to status, activity, conflicts or probe for machine-readable output.
");
        return 0;
    }

    // ---- Read commands ------------------------------------------------------

    public static async Task<int> StatusAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();
        var state = await client.GetStateAsync();

        if (line.Has("json")) return Json(state);

        Console.WriteLine();
        Console.WriteLine($"  DBsync {state.ServiceVersion} — {state.StatusLine}");
        Console.WriteLine($"  {(state.PausedAll ? "Paused" : "Running")} · {state.Pairs.Count} folder pair(s)");
        Console.WriteLine();

        if (state.Pairs.Count == 0)
        {
            Console.WriteLine("  No folder pairs yet. Add one with:  dbsync add --local <path> --share <path>");
            Console.WriteLine();
            return 0;
        }

        foreach (var pair in state.Pairs)
        {
            var progress = pair.Status == PairStatus.Syncing ? $" {pair.Percent}%" : "";
            Console.WriteLine($"  {pair.Name}  [{Tag(pair.Status)}{progress}]");
            Console.WriteLine($"    {pair.LocalPath}  <->  {pair.SharePath}");
            Console.WriteLine($"    {pair.Detail}");
            Console.WriteLine($"    id {pair.Id} · {Describe(pair.Direction)}" +
                              (pair.Watcher ? " · realtime" : " · sweep only"));
            Console.WriteLine();
        }

        return 0;
    }

    public static async Task<int> WatchAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();

        var finished = new TaskCompletionSource<bool>();
        client.Disconnected += _ => finished.TrySetResult(true);
        client.EventReceived += message => Console.WriteLine(Render(message, line.Has("json")));

        await client.SubscribeAsync();
        Console.WriteLine("Watching DBsync events. Press Ctrl+C to stop.");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            finished.TrySetResult(true);
        };

        await finished.Task;
        return 0;
    }

    public static async Task<int> ActivityAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();

        var hours = line.GetInt("hours", 24);
        var query = new ActivityQuery
        {
            PairId = line.Get("id"),
            Since = TimeSpan.FromHours(hours),
            Limit = line.GetInt("limit", 40),
        };

        var response = await client.GetActivityAsync(query);
        if (line.Has("json")) return Json(response);

        var summary = await client.GetActivitySummaryAsync(query);
        Console.WriteLine();
        Console.WriteLine($"  Activity & history — last {hours}h across {summary.PairCount} folder pair(s)");
        Console.WriteLine($"  {summary.FilesSent:N0} files sent · {summary.FilesReceived:N0} received · " +
                          $"{summary.Conflicts:N0} conflicts");
        Console.WriteLine();
        Console.WriteLine($"  {"Time",-8}{"Event",-10}{"File",-44}{"Folder pair",-20}Result");
        Console.WriteLine("  " + new string('-', 88));

        foreach (var entry in response.Entries)
        {
            Console.WriteLine($"  {entry.TimestampUtc.ToLocalTime():HH:mm}   " +
                              $"{entry.Kind,-10}{Truncate(entry.File, 44),-44}" +
                              $"{Truncate(entry.PairName, 20),-20}{entry.Result}");
        }

        if (response.Entries.Count == 0) Console.WriteLine("  (nothing logged in this window)");
        Console.WriteLine();
        return 0;
    }

    public static async Task<int> ConflictsAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();
        var response = await client.GetConflictsAsync(new ConflictQuery { PairId = line.Get("id") });

        if (line.Has("json")) return Json(response);

        Console.WriteLine();
        if (response.Conflicts.Count == 0)
        {
            Console.WriteLine("  No conflicts. Everything is in sync.");
            Console.WriteLine();
            return 0;
        }

        foreach (var conflict in response.Conflicts)
        {
            Console.WriteLine($"  #{conflict.Id}  {conflict.RelativePath}   ({conflict.PairName})");
            Console.WriteLine($"    this pc        {Local(conflict.Local.ModifiedUtc)}   " +
                              $"{Bytes(conflict.Local.Bytes)} · {Owner(conflict.Local.Owner)}");
            Console.WriteLine($"    network share  {Local(conflict.Share.ModifiedUtc)}   " +
                              $"{Bytes(conflict.Share.Bytes)} · {Owner(conflict.Share.Owner)}");
            Console.WriteLine();
        }

        Console.WriteLine("  Resolve with:  dbsync resolve --conflict <n> --keep local|share|both [--all]");
        Console.WriteLine();
        return 0;
    }

    public static async Task<int> ProbeAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();
        var path = line.Require("path");

        var probe = await client.ProbeDestinationAsync(new ProbeDestinationRequest
        {
            Path = path,
            Kind = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? DestinationKind.Unc
                : DestinationKind.Drive,
            Username = line.Get("user"),
            Password = line.Get("password"),
        });

        if (line.Has("json")) return Json(probe);

        Console.WriteLine();
        Console.WriteLine("  " + probe.Message);
        Console.WriteLine();
        return probe.Reachable && probe.Writable ? 0 : 2;
    }

    // ---- Write commands -----------------------------------------------------

    public static async Task<int> AddAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();

        var pair = new FolderPair
        {
            Name = line.Get("name") ?? "",
            LocalPath = line.Require("local"),
            SharePath = line.Require("share"),
        };

        ApplyOptions(pair, line);

        var response = await client.AddPairAsync(new SavePairRequest
        {
            Pair = pair,
            Username = line.Get("user"),
            Password = line.Get("password"),
        });

        Console.WriteLine($"Folder pair created — {response.Pair.LocalPath} is now syncing with " +
                          $"{response.Pair.SharePath}");
        Console.WriteLine($"id {response.Pair.Id}");
        return 0;
    }

    public static async Task<int> UpdateAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();

        var id = line.Require("id");
        var state = await client.GetStateAsync();
        var pair = state.Pairs.FirstOrDefault(p => p.Id == id)
                   ?? throw new ArgumentException($"No pair with id {id}.");

        if (line.Get("name") is { Length: > 0 } name) pair.Name = name;
        if (line.Get("local") is { Length: > 0 } local) pair.LocalPath = local;
        if (line.Get("share") is { Length: > 0 } share) pair.SharePath = share;

        ApplyOptions(pair, line);

        var response = await client.UpdatePairAsync(new SavePairRequest
        {
            Pair = pair,
            Username = line.Get("user"),
            Password = line.Get("password"),
        });

        Console.WriteLine($"Updated {response.Pair.Name}.");
        return 0;
    }

    public static async Task<int> RemoveAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();
        var response = await client.DeletePairAsync(line.Require("id"));
        Console.WriteLine(response.Message ?? "Pair removed.");
        return 0;
    }

    public static async Task<int> PauseAsync(bool paused)
    {
        await using var client = await DBsyncClient.ConnectAsync();
        var state = paused ? await client.PauseAllAsync() : await client.ResumeAllAsync();

        Console.WriteLine(paused
            ? "Syncing paused — DBsync will keep watching but send nothing until you resume."
            : $"Syncing resumed — {state.StatusLine}.");
        return 0;
    }

    public static async Task<int> SyncNowAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();
        var response = await client.SyncNowAsync(line.Get("id"));
        Console.WriteLine(response.Message ?? "Rescan queued.");
        return 0;
    }

    public static async Task<int> ResolveAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();

        var resolution = (line.Get("keep") ?? "").ToLowerInvariant() switch
        {
            "local" or "pc" or "this" => ConflictResolution.KeepLocal,
            "share" or "network" or "remote" => ConflictResolution.KeepShare,
            "both" => ConflictResolution.KeepBoth,
            _ => throw new ArgumentException("--keep must be local, share, or both."),
        };

        var response = await client.ResolveConflictAsync(new ResolveConflictRequest
        {
            ConflictId = long.Parse(line.Require("conflict"), CultureInfo.InvariantCulture),
            Resolution = resolution,
            ApplyToPair = line.Has("all"),
        });

        Console.WriteLine(response.Message ?? "Conflict resolved.");
        return 0;
    }

    public static async Task<int> CredentialsAsync(CommandLine line)
    {
        await using var client = await DBsyncClient.ConnectAsync();
        var response = await client.StoreCredentialsAsync(new StoreCredentialsRequest
        {
            PairId = line.Require("id"),
            Username = line.Require("user"),
            Password = line.Require("password"),
        });

        Console.WriteLine(response.Message ?? "Credentials stored.");
        return 0;
    }

    // ---- Service control ----------------------------------------------------

    public static int Install(CommandLine line)
    {
        var exe = line.Get("exe") ?? DefaultServiceExe();
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"Service executable not found at {exe}. Pass --exe <path>.");
            return 2;
        }

        var create = Run("sc.exe", $"create DBsync binPath= \"{exe}\" start= auto DisplayName= \"DBsync\"");
        if (create != 0) return create;

        Run("sc.exe", "description DBsync \"Keeps local folders in sync with network shares.\"");
        // Restart on failure rather than leaving folders silently un-synced.
        Run("sc.exe", "failure DBsync reset= 86400 actions= restart/5000/restart/15000/restart/60000");

        Console.WriteLine("Installed. Start it with:  dbsync start");
        return 0;
    }

    public static int Uninstall()
    {
        Run("sc.exe", "stop DBsync");
        return Run("sc.exe", "delete DBsync");
    }

    public static int ServiceControl(string verb) => Run("sc.exe", $"{verb} DBsync");

    private static string DefaultServiceExe()
    {
        var sibling = Path.Combine(AppContext.BaseDirectory, "DBsync.Service.exe");
        return File.Exists(sibling) ? sibling : Path.Combine(AppContext.BaseDirectory, "DBsync.Service.exe");
    }

    private static int Run(string file, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false });
        if (process is null)
        {
            Console.Error.WriteLine($"Could not run {file}.");
            return 2;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    // ---- Shared helpers -----------------------------------------------------

    private static void ApplyOptions(FolderPair pair, CommandLine line)
    {
        if (line.Get("direction") is { Length: > 0 } direction)
        {
            pair.Direction = direction.ToLowerInvariant() switch
            {
                "two-way" or "twoway" or "both" => SyncDirection.TwoWay,
                "push" or "up" => SyncDirection.Push,
                "pull" or "down" => SyncDirection.Pull,
                _ => throw new ArgumentException("--direction must be two-way, push, or pull."),
            };
        }

        if (line.Get("exclude") is { } excludes)
        {
            pair.Excludes = excludes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        if (line.Has("limit")) pair.UploadLimitBytesPerSecond = ParseRate(line.Get("limit"));
        if (line.Has("versions")) pair.VersionsKept = line.GetInt("versions", pair.VersionsKept);
        if (line.Has("vss")) pair.UseVss = true;
        if (line.Has("no-vss")) pair.UseVss = false;
        if (line.Has("no-watcher")) pair.Watcher = false;
        if (line.Has("watcher")) pair.Watcher = true;
    }

    /// <summary>Mirrors the service-side parser so "8 MB/s" means the same thing on both ends.</summary>
    private static long? ParseRate(string? text)
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

    private static string Render(IpcMessage message, bool json)
    {
        if (json) return FrameWriter.Describe(message);

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        return message.Event switch
        {
            IpcEventKind.PairChanged => RenderPair(stamp, message.PayloadAs<PairChangedEvent>()),
            IpcEventKind.LogAppended => RenderLog(stamp, message.PayloadAs<LogAppendedEvent>()),
            IpcEventKind.ConflictRaised => RenderConflict(stamp, message.PayloadAs<ConflictRaisedEvent>()),
            IpcEventKind.ReachabilityChanged => RenderReach(stamp, message.PayloadAs<ReachabilityEvent>()),
            _ => $"{stamp}  state changed",
        };
    }

    private static string RenderPair(string stamp, PairChangedEvent e) =>
        $"{stamp}  {Truncate(e.Pair.Name, 16),-16} {Tag(e.Pair.Status),-8} {e.Pair.Detail}";

    private static string RenderLog(string stamp, LogAppendedEvent e) =>
        $"{stamp}  {e.Entry.Kind,-9} {e.Entry.Result,-8} {e.Entry.File}" +
        (e.Entry.Message is { Length: > 0 } message ? $"  ({message})" : "");

    private static string RenderConflict(string stamp, ConflictRaisedEvent e) =>
        $"{stamp}  CONFLICT  #{e.Conflict.Id} {e.Conflict.RelativePath} in {e.Conflict.PairName} " +
        $"({e.PendingInPair} pending)";

    private static string RenderReach(string stamp, ReachabilityEvent e) =>
        $"{stamp}  {(e.Reachable ? "ONLINE " : "OFFLINE")}  {e.PairId}  {e.Detail}";

    private static int Json(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, value.GetType(),
            new JsonSerializerOptions(IpcProtocol.Json) { WriteIndented = true }));
        return 0;
    }

    private static string Tag(PairStatus status) => status switch
    {
        PairStatus.Syncing => "Syncing",
        PairStatus.InSync => "In sync",
        PairStatus.Conflict => "Conflict",
        PairStatus.Waiting => "Waiting",
        _ => "Paused",
    };

    private static string Describe(SyncDirection direction) => direction switch
    {
        SyncDirection.Push => "one-way push",
        SyncDirection.Pull => "one-way pull",
        _ => "two-way",
    };

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : "..." + value[^(width - 3)..];

    private static string Local(DateTimeOffset moment) => moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string Bytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB"
        : $"{bytes} bytes";

    private static string Owner(string owner) => string.IsNullOrEmpty(owner) ? "unknown" : owner;
}
