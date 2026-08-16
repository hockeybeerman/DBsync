using DBsync.Service;
using DBsync.Service.Configuration;
using DBsync.Service.Engine;
using DBsync.Service.Ipc;
using DBsync.Service.Logging;
using DBsync.Service.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Run as a Windows Service when the SCM starts us, and as a plain console app when a developer
// or an admin runs the exe directly — the host detects which.
var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "DBsync")
    .ConfigureLogging((context, logging) =>
    {
        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        logging.AddProvider(new FileLoggerProvider(verbose ? LogLevel.Debug : LogLevel.Information));
        if (verbose) logging.SetMinimumLevel(LogLevel.Debug);
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<Database>();
        services.AddSingleton<SyncStateStore>();
        services.AddSingleton<ConflictStore>();
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<IpcServer>();
        services.AddHostedService<Worker>();
    });

await builder.Build().RunAsync();
