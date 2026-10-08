using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteLibrary.Services;

/// <summary>Runs one media reconciliation after Jellyfin starts, including after plugin upgrades.</summary>
public sealed class RemoteLibraryStartupSyncService : BackgroundService
{
    private readonly RemoteLibraryService _service;
    private readonly ILogger<RemoteLibraryStartupSyncService> _logger;

    public RemoteLibraryStartupSyncService(
        RemoteLibraryService service,
        ILogger<RemoteLibraryStartupSyncService> logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let Jellyfin finish loading its libraries and scheduled-task state.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            await _service.SyncAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
        catch (InvalidOperationException exception)
        {
            // A new installation may not have a configured peer yet.
            _logger.LogDebug(exception, "Remote Library startup sync skipped because no remote server is configured");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Remote Library startup sync failed");
        }
    }
}
