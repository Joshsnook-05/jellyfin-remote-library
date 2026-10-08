using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RemoteLibrary.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public List<RemoteServerConfiguration> Servers { get; set; } = [];

    // Legacy single-server fields are retained for automatic migration.
    public string ServerUrl { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RemoteUserId { get; set; } = string.Empty;

    public string RemoteUserName { get; set; } = string.Empty;

    public string OutputPath { get; set; } = "/remote-library";

    public string MovieLibraryName { get; set; } = "Movies";

    public string SeriesLibraryName { get; set; } = "Shows";

    public string AnimeLibraryName { get; set; } = "Anime";

    public string SourceLabel { get; set; } = string.Empty;

    public string StreamSecret { get; set; } = Guid.NewGuid().ToString("N");

    public bool EnableMovies { get; set; } = true;

    public bool EnableSeries { get; set; } = true;

    public bool EnableCalendar { get; set; } = true;

    public bool EnableRecommendations { get; set; } = true;

    public bool SkipLocalMatches { get; set; } = true;

    public int MediaSyncIntervalMinutes { get; set; } = 60;

    public int CalendarSyncIntervalMinutes { get; set; } = 60;

    public int ReviewSyncIntervalMinutes { get; set; } = 5;
}

public sealed class RemoteServerConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ServerUrl { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RemoteUserId { get; set; } = string.Empty;

    public string RemoteUserName { get; set; } = string.Empty;

    public string SourceLabel { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
