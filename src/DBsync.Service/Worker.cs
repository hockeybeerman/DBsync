using DBsync.Service.Configuration;
using DBsync.Service.Engine;
using DBsync.Service.Ipc;
using DBsync.Service.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DBsync.Service;

/// <summary>
/// Service entry point in the hosting sense: brings the engine and the pipe server up, keeps the
/// housekeeping timer running, and tears both down on stop.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly SyncEngine _engine;
    private readonly IpcServer _ipc;
    private readonly ConfigStore _config;
    private readonly ActivityLog _activity;
    private readonly ILogger<Worker> _log;

    public Worker(SyncEngine engine, IpcServer ipc, ConfigStore config, ActivityLog activity, ILogger<Worker> log)
    {
        _engine = engine;
        _ipc = ipc;
        _config = config;
        _activity = activity;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DBsync service starting. Data root: {Root}", ServicePaths.Root);

        _engine.Start();
        _ipc.Start(stoppingToken);

        try
        {
            // Housekeeping only — the engine and IPC server run on their own tasks.
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken).ConfigureAwait(false);

                var removed = _activity.Trim(_config.Snapshot().ActivityRetentionDays);
                if (removed > 0) _log.LogInformation("Trimmed {Count} expired activity row(s).", removed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on stop.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("DBsync service stopping.");

        await _ipc.DisposeAsync().ConfigureAwait(false);
        await _engine.StopAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("DBsync service stopped.");
    }
}
