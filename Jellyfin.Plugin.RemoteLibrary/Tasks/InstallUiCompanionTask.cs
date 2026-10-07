using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteLibrary.Tasks;

public sealed class InstallUiCompanionTask : IScheduledTask
{
    private const string ScriptTag = "<script plugin=\"Remote Library\" defer src=\"../RemoteLibrary/Integration.js?v=1.0.0\"></script>";
    private static readonly Regex ExistingTag = new(
        @"<script[^>]*(?:plugin=[""']Remote Library[""']|RemoteLibrary/Integration\.js)[^>]*>\s*</script>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<InstallUiCompanionTask> _logger;

    public InstallUiCompanionTask(IApplicationPaths applicationPaths, ILogger<InstallUiCompanionTask> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public string Name => "Install Remote Library web companion";

    public string Key => "RemoteLibraryInstallUiCompanion";

    public string Description => "Ensures the bundled Jellyfin Enhanced calendar compatibility bridge is loaded by Jellyfin Web.";

    public string Category => "Remote Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var indexPath = Path.Combine(_applicationPaths.WebPath, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("Remote Library web companion could not find {IndexPath}; request-time injection remains enabled", indexPath);
            progress.Report(100);
            return Task.CompletedTask;
        }

        try
        {
            var original = File.ReadAllText(indexPath);
            var withoutOldTag = ExistingTag.Replace(original, string.Empty);
            var close = withoutOldTag.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (close < 0) throw new InvalidDataException("Jellyfin Web index.html has no closing body tag.");
            var updated = withoutOldTag[..close] + ScriptTag + Environment.NewLine + withoutOldTag[close..];
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, updated);
                _logger.LogInformation("Remote Library installed its bundled Jellyfin Enhanced compatibility bridge into Jellyfin Web.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Remote Library could not update Jellyfin Web index.html; request-time and Enhanced-bundle injection remain enabled");
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}
