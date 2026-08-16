using DBsync.Cli;

// The service hands back the product's own copy — em dashes, middots, arrows. Without this the
// console renders them as mojibake on a default OEM codepage.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected output */ }

var line = CommandLine.Parse(args);

try
{
    return line.Command switch
    {
        "status" => await Commands.StatusAsync(line),
        "watch" => await Commands.WatchAsync(line),
        "add" => await Commands.AddAsync(line),
        "update" => await Commands.UpdateAsync(line),
        "remove" => await Commands.RemoveAsync(line),
        "pause" => await Commands.PauseAsync(true),
        "resume" => await Commands.PauseAsync(false),
        "sync" => await Commands.SyncNowAsync(line),
        "activity" => await Commands.ActivityAsync(line),
        "conflicts" => await Commands.ConflictsAsync(line),
        "resolve" => await Commands.ResolveAsync(line),
        "probe" => await Commands.ProbeAsync(line),
        "creds" => await Commands.CredentialsAsync(line),
        "install" => Commands.Install(line),
        "uninstall" => Commands.Uninstall(),
        "start" => Commands.ServiceControl("start"),
        "stop" => Commands.ServiceControl("stop"),
        _ => Commands.Help(),
    };
}
catch (TimeoutException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Start it with:  sc start DBsync    (or run DBsync.Service.exe directly)");
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
