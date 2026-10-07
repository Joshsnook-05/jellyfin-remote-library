using Jellyfin.Plugin.RemoteLibrary.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteLibrary.Tasks;

public sealed class SyncRemoteCalendarTask : IScheduledTask
{
    private readonly RemoteLibraryService _service;

    public SyncRemoteCalendarTask(RemoteLibraryService service)
    {
        _service = service;
    }

    public string Name => "Sync remote Jellyfin calendar";

    public string Key => "RemoteLibraryCalendarSync";

    public string Description => "Imports upcoming remote episodes into the local Jellyfin calendar.";

    public string Category => "Remote Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Math.Clamp(Plugin.Instance?.Configuration.CalendarSyncIntervalMinutes ?? 60, 5, 10080);
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
        await _service.SyncCalendarAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
