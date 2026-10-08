using Jellyfin.Plugin.RemoteLibrary.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteLibrary.Tasks;

public sealed class SyncRemoteLibraryTask : IScheduledTask
{
    private readonly RemoteLibraryService _service;

    public SyncRemoteLibraryTask(RemoteLibraryService service)
    {
        _service = service;
    }

    public string Name => "Sync remote Jellyfin library";

    public string Key => "RemoteLibrarySync";

    public string Description => "Refreshes remote Jellyfin metadata pointers without downloading media.";

    public string Category => "Remote Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Math.Clamp(Plugin.Instance?.Configuration.MediaSyncIntervalMinutes ?? 60, 5, 10080);
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _service.SyncAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
