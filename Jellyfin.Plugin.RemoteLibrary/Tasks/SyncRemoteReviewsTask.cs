using Jellyfin.Plugin.RemoteLibrary.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteLibrary.Tasks;

public sealed class SyncRemoteReviewsTask : IScheduledTask
{
    private readonly RemoteLibraryService _service;

    public SyncRemoteReviewsTask(RemoteLibraryService service)
    {
        _service = service;
    }

    public string Name => "Sync remote Jellyfin Enhanced reviews";

    public string Key => "RemoteLibraryReviewSync";

    public string Description => "Imports original reviews from paired servers and marks them so they are never re-exported.";

    public string Category => "Remote Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Math.Clamp(Plugin.Instance?.Configuration.ReviewSyncIntervalMinutes ?? 5, 5, 10080);
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _service.SyncReviewsAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
