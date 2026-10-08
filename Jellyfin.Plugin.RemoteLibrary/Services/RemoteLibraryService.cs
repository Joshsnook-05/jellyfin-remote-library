using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.RemoteLibrary.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteLibrary.Services;

public sealed partial class RemoteLibraryService
{
    public const string HttpClientName = "RemoteLibrary";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RemoteLibraryService> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly SemaphoreSlim _pairingLock = new(1, 1);
    private readonly SemaphoreSlim _reachabilityLock = new(1, 1);
    private readonly object _syncStateLock = new();
    private readonly Dictionary<string, string> _pendingQuickConnectSecrets = new(StringComparer.OrdinalIgnoreCase);
    private SyncJobStatus _syncStatus = new("Idle", "No sync has run since Jellyfin started.", null, null, null);
    private RemoteReachabilitySummary _reachability = new([], DateTimeOffset.MinValue);

    public RemoteLibraryService(
        IHttpClientFactory httpClientFactory,
        ILogger<RemoteLibraryService> logger,
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
    }

    public async Task<RemoteStatus> TestAsync(string serverId, CancellationToken cancellationToken)
    {
        var serverConfig = GetServer(serverId);
        var session = GetConfiguredSession(serverConfig);
        var client = CreateClient(serverConfig.ServerUrl, session.AccessToken);
        var user = await GetAsync<RemoteUser>(client, "Users/Me", cancellationToken).ConfigureAwait(false);
        var server = await GetAsync<PublicSystemInfo>(client, "System/Info/Public", cancellationToken).ConfigureAwait(false);
        return new RemoteStatus(true, server.ServerName ?? serverConfig.ServerUrl, user.Name, null);
    }

    public async Task<RemoteReachabilitySummary> GetReachabilityAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _reachability.CheckedUtc < TimeSpan.FromSeconds(8))
        {
            return _reachability;
        }

        await _reachabilityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (now - _reachability.CheckedUtc < TimeSpan.FromSeconds(8))
            {
                return _reachability;
            }

            var checks = GetServers(GetConfiguration()).Where(server => server.Enabled).Select(async server =>
            {
                var sourceLabel = SourceLabel(server);
                try
                {
                    var client = CreateClient(server.ServerUrl, null);
                    await GetAsync<PublicSystemInfo>(client, "System/Info/Public", cancellationToken).ConfigureAwait(false);
                    return new RemoteServerReachability(server.Id, sourceLabel, true);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogDebug(exception, "Remote library reachability check failed for {ServerUrl}", server.ServerUrl);
                    return new RemoteServerReachability(server.Id, sourceLabel, false);
                }
            });
            _reachability = new RemoteReachabilitySummary(await Task.WhenAll(checks).ConfigureAwait(false), now);

            return _reachability;
        }
        finally
        {
            _reachabilityLock.Release();
        }
    }

    public async Task<QuickConnectStart> InitiateQuickConnectAsync(string serverId, CancellationToken cancellationToken)
    {
        var serverConfig = GetServer(serverId);
        ValidateServerUrl(serverConfig.ServerUrl);
        var client = CreateClient(serverConfig.ServerUrl, null);
        using var response = await client.PostAsync("QuickConnect/Initiate", content: null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<QuickConnectResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The remote Jellyfin returned an empty Quick Connect response.");
        if (string.IsNullOrWhiteSpace(result.Secret) || string.IsNullOrWhiteSpace(result.Code))
        {
            throw new InvalidOperationException("The remote Jellyfin did not return a valid Quick Connect code.");
        }

        await _pairingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pendingQuickConnectSecrets[serverConfig.Id] = result.Secret;
        }
        finally
        {
            _pairingLock.Release();
        }

        return new QuickConnectStart(result.Code, "Open the remote Jellyfin user menu, choose Quick Connect, and enter this code. Then click Finish pairing here.");
    }

    public async Task<RemoteStatus> CompleteQuickConnectAsync(string serverId, CancellationToken cancellationToken)
    {
        await _pairingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_pendingQuickConnectSecrets.TryGetValue(serverId, out var secret))
            {
                throw new InvalidOperationException("Start Quick Connect first to generate a pairing code.");
            }

            var serverConfig = GetServer(serverId);
            ValidateServerUrl(serverConfig.ServerUrl);
            var client = CreateClient(serverConfig.ServerUrl, null);
            var state = await GetAsync<QuickConnectResult>(client, $"QuickConnect/Connect?Secret={Uri.EscapeDataString(secret)}", cancellationToken)
                .ConfigureAwait(false);
            if (!state.Authenticated)
            {
                throw new InvalidOperationException("That code has not been approved on the remote Jellyfin server yet.");
            }

            using var response = await client.PostAsJsonAsync(
                "Users/AuthenticateWithQuickConnect",
                new { Secret = secret },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var auth = await response.Content.ReadFromJsonAsync<AuthenticationResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The remote Jellyfin returned an empty authentication response.");

            serverConfig.AccessToken = auth.AccessToken;
            serverConfig.RemoteUserId = auth.User.Id;
            serverConfig.RemoteUserName = auth.User.Name;
            Plugin.Instance!.SaveConfiguration();
            _pendingQuickConnectSecrets.Remove(serverId);
            return await TestAsync(serverId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pairingLock.Release();
        }
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var config = GetConfiguration();
        var servers = GetServers(config).Where(server => server.Enabled).ToList();
        if (servers.Count == 0)
        {
            throw new InvalidOperationException("Add and enable at least one remote Jellyfin server.");
        }

        var outputRoot = ResolveOutputRoot(config);
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(Path.Combine(outputRoot, "Movies"));
        Directory.CreateDirectory(Path.Combine(outputRoot, "Shows"));
        Directory.CreateDirectory(Path.Combine(outputRoot, "Anime"));
        await File.WriteAllTextAsync(Path.Combine(outputRoot, ".remote-library-managed"), "Managed by Jellyfin Remote Library.\n", cancellationToken)
            .ConfigureAwait(false);

        var local = config.SkipLocalMatches ? ScanLocalMedia("/media") : LocalInventory.Empty;
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moviesAdded = 0;
        var episodesAdded = 0;
        var calendarEpisodesAdded = 0;
        var localMatchesSkipped = 0;
        var localGapsFilled = 0;
        var invalidItemsSkipped = 0;
        var onlineServers = 0;
        var offlineServers = 0;
        var seenRemoteMovies = new HashSet<string>(StringComparer.Ordinal);
        var seenRemoteEpisodes = new HashSet<string>(StringComparer.Ordinal);
        // A series entry alone is not evidence that a server can stream the show.
        // Keep its NFO tied to the first episode pointer actually selected for sync.
        var selectedRemoteSeries = new Dictionary<string, RemoteSeriesMetadata>(StringComparer.OrdinalIgnoreCase);
        // Keep a single deterministic library root for a show across all paired
        // servers. One server may tag a series as anime while another does not;
        // choosing the folder once prevents seasons from becoming separate shows.
        var remoteSeriesFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var desiredLocalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string RemoteSeriesFolder(RemoteItem? series, string seriesName)
        {
            var key = SeriesLookupKey(seriesName);
            if (remoteSeriesFolders.TryGetValue(key, out var existingFolder))
            {
                return existingFolder;
            }

            var folder = IsAnime(series) ? "Anime" : "Shows";
            remoteSeriesFolders[key] = folder;
            return folder;
        }

        bool ProcessEpisode(
            RemoteItem episode,
            RemoteItem? series,
            RemoteServerConfiguration serverConfig,
            string sourceLabel,
            bool fromCalendar)
        {
            var seriesName = series?.Name ?? episode.SeriesName;
            var seriesYear = series?.ProductionYear;
            if (episode.ParentIndexNumber is null || episode.IndexNumber is null || string.IsNullOrWhiteSpace(seriesName))
            {
                invalidItemsSkipped++;
                return false;
            }

            var key = EpisodeKey(seriesName, seriesYear, episode.ParentIndexNumber, episode.IndexNumber);
            var yearlessKey = EpisodeKey(seriesName, null, episode.ParentIndexNumber, episode.IndexNumber);
            var localSeriesKey = SeriesKey(seriesName, seriesYear);
            var localSeriesWithoutYear = SeriesKey(seriesName, null);
            string? localSeriesPath = null;
            var hasLocalSeries = config.SkipLocalMatches
                && (local.SeriesPaths.TryGetValue(localSeriesKey, out localSeriesPath)
                    || local.SeriesPaths.TryGetValue(localSeriesWithoutYear, out localSeriesPath));
            if (config.SkipLocalMatches
                && (local.Episodes.Contains(key)
                    || local.Episodes.Contains(yearlessKey)))
            {
                localMatchesSkipped++;
                return false;
            }

            if (!seenRemoteEpisodes.Add(key))
            {
                return false;
            }

            if (hasLocalSeries && !string.IsNullOrWhiteSpace(localSeriesPath))
            {
                WriteEpisodeInSeriesDirectory(localSeriesPath, episode, seriesName, serverConfig.Id, config.StreamSecret, sourceLabel, desiredLocalFiles);
                localGapsFilled++;
            }
            else
            {
                var libraryFolder = RemoteSeriesFolder(series, seriesName);
                var seriesOutputKey = $"{libraryFolder}:{SeriesKey(seriesName, seriesYear)}";
                selectedRemoteSeries.TryAdd(
                    seriesOutputKey,
                    new RemoteSeriesMetadata(
                        libraryFolder,
                        SeriesMetadataFor(series, episode, seriesName, seriesYear),
                        serverConfig.Id,
                        sourceLabel));

                WriteEpisode(outputRoot, libraryFolder, episode, seriesName, seriesYear, serverConfig.Id, config.StreamSecret, sourceLabel, desired);
            }

            episodesAdded++;
            if (fromCalendar)
            {
                calendarEpisodesAdded++;
            }

            return true;
        }

        foreach (var serverConfig in servers)
        {
            try
            {
                var session = GetConfiguredSession(serverConfig);
                var client = CreateClient(serverConfig.ServerUrl, session.AccessToken);
                _logger.LogInformation("Remote library sync reading {ServerUrl}", serverConfig.ServerUrl);
                UpdateSyncMessage($"Connecting to {SourceLabel(serverConfig)}…");
                var remoteManagedMedia = await GetRemoteManagedMediaAsync(client, cancellationToken).ConfigureAwait(false);
                var views = await GetAsync<QueryResult>(client, $"Users/{Uri.EscapeDataString(session.UserId)}/Views", cancellationToken)
                    .ConfigureAwait(false);
                var publicInfo = await GetAsync<PublicSystemInfo>(client, "System/Info/Public", cancellationToken).ConfigureAwait(false);
                var sourceLabel = string.IsNullOrWhiteSpace(serverConfig.SourceLabel)
                    ? publicInfo.ServerName ?? new Uri(serverConfig.ServerUrl).Host
                    : serverConfig.SourceLabel.Trim();
                onlineServers++;
                _logger.LogInformation("Remote library received {Count} views from {Source}", views.Items.Count, sourceLabel);
                var seriesById = new Dictionary<string, RemoteItem>(StringComparer.OrdinalIgnoreCase);
                var seriesByName = new Dictionary<string, RemoteItem>(StringComparer.OrdinalIgnoreCase);
                var managedSeriesNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var managedSeriesIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var view in views.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (config.EnableMovies && string.Equals(view.CollectionType, "movies", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Remote library reading movies from {Source}/{ViewName}", sourceLabel, view.Name);
                        UpdateSyncMessage($"Reading movies from {sourceLabel}/{view.Name}…");
                        var movies = await GetAllItemsAsync(client, session.UserId, view.Id, "Movie", cancellationToken).ConfigureAwait(false);
                        foreach (var movie in movies)
                        {
                            if (IsManagedRemoteItem(movie, remoteManagedMedia))
                            {
                                continue;
                            }
                            var movieKey = MovieKey(movie.Name, movie.ProductionYear);
                            if (config.SkipLocalMatches
                                && (local.Movies.Contains(movieKey)
                                    || local.Movies.Contains(MovieKey(movie.Name, null))))
                            {
                                localMatchesSkipped++;
                                continue;
                            }

                            if (!seenRemoteMovies.Add(movieKey))
                            {
                                continue;
                            }

                            WriteMovie(outputRoot, movie, serverConfig.Id, config.StreamSecret, sourceLabel, desired);
                            moviesAdded++;
                        }
                    }

                    if (config.EnableSeries && IsSeriesView(view, config))
                    {
                        _logger.LogInformation("Remote library reading shows from {Source}/{ViewName}", sourceLabel, view.Name);
                        UpdateSyncMessage($"Reading shows from {sourceLabel}/{view.Name}…");
                        var seriesItems = await GetAllItemsAsync(client, session.UserId, view.Id, "Series", cancellationToken).ConfigureAwait(false);
                        var episodes = await GetAllItemsAsync(client, session.UserId, view.Id, "Episode", cancellationToken).ConfigureAwait(false);
                        var nativeSeriesIds = episodes
                            .Where(episode => !IsManagedRemoteItem(episode, remoteManagedMedia) && !string.IsNullOrWhiteSpace(episode.SeriesId))
                            .Select(episode => episode.SeriesId!)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var nativeSeriesNames = episodes
                            .Where(episode => !IsManagedRemoteItem(episode, remoteManagedMedia) && !string.IsNullOrWhiteSpace(episode.SeriesName))
                            .Select(episode => SeriesLookupKey(episode.SeriesName))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var seriesItem in seriesItems)
                        {
                            // A series can retain a Remote Source tag after genuine
                            // episodes are added to it. Treat it as remote-only only
                            // when all of its episodes are managed pointers too.
                            if (IsManagedRemoteItem(seriesItem, remoteManagedMedia)
                                && !nativeSeriesIds.Contains(seriesItem.Id)
                                && !nativeSeriesNames.Contains(SeriesLookupKey(seriesItem.Name)))
                            {
                                managedSeriesIds.Add(seriesItem.Id);
                                managedSeriesNames.Add(SeriesLookupKey(seriesItem.Name));
                                continue;
                            }
                            managedSeriesIds.Remove(seriesItem.Id);
                            managedSeriesNames.Remove(SeriesLookupKey(seriesItem.Name));
                            seriesById.TryAdd(seriesItem.Id, seriesItem);
                            var nameKey = SeriesLookupKey(seriesItem.Name);
                            if (!seriesByName.TryGetValue(nameKey, out var existingSeries)
                                || (!IsAnime(existingSeries) && IsAnime(seriesItem)))
                            {
                                seriesByName[nameKey] = seriesItem;
                            }
                        }

                        foreach (var episode in episodes)
                        {
                            if (IsManagedRemoteItem(episode, remoteManagedMedia)
                                || (episode.SeriesId is not null && managedSeriesIds.Contains(episode.SeriesId))
                                || (!string.IsNullOrWhiteSpace(episode.SeriesName)
                                    && managedSeriesNames.Contains(SeriesLookupKey(episode.SeriesName))))
                            {
                                continue;
                            }
                            seriesById.TryGetValue(episode.SeriesId ?? string.Empty, out var series);
                            ProcessEpisode(episode, series, serverConfig, sourceLabel, fromCalendar: false);
                        }
                    }
                }

                if (config.EnableSeries && config.EnableCalendar)
                {
                    UpdateSyncMessage($"Reading upcoming episodes from {sourceLabel}…");
                    // Enhanced is the source of Sonarr/Radarr calendar events. The
                    // native endpoint can contain unrelated library episodes, so do
                    // not use a non-empty native response as a reason to skip it.
                    var upcomingEpisodes = await GetEnhancedCalendarEpisodesAsync(client, cancellationToken).ConfigureAwait(false);
                    if (upcomingEpisodes.Count == 0)
                    {
                        // Fall back for remotes without Jellyfin Enhanced or its calendar API.
                        upcomingEpisodes = await GetUpcomingEpisodesAsync(client, session.UserId, cancellationToken).ConfigureAwait(false);
                    }
                    foreach (var episode in upcomingEpisodes)
                    {
                        if (IsManagedRemoteItem(episode, remoteManagedMedia)
                            || (!string.IsNullOrWhiteSpace(episode.SeriesName)
                                && managedSeriesNames.Contains(SeriesLookupKey(episode.SeriesName))))
                        {
                            continue;
                        }
                        seriesById.TryGetValue(episode.SeriesId ?? string.Empty, out var series);
                        if (series is null && !string.IsNullOrWhiteSpace(episode.SeriesName))
                        {
                            seriesByName.TryGetValue(SeriesLookupKey(episode.SeriesName), out series);
                        }
                        ProcessEpisode(episode, series, serverConfig, sourceLabel, fromCalendar: true);
                    }

                    _logger.LogInformation(
                        "Remote library received {Count} upcoming episodes from {Source}",
                        upcomingEpisodes.Count,
                        sourceLabel);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                offlineServers++;
                _logger.LogWarning(exception, "Skipping unreachable remote library {Source} ({ServerUrl})", SourceLabel(serverConfig), serverConfig.ServerUrl);
            }
        }

        foreach (var selection in selectedRemoteSeries.Values)
        {
            WriteSeriesMetadata(
                outputRoot,
                selection.LibraryFolder,
                selection.Series,
                selection.ServerId,
                config.StreamSecret,
                selection.SourceLabel,
                desired);
        }

        var removed = 0;
        if (offlineServers == 0)
        {
            UpdateSyncMessage("Removing stale pointers…");
            removed = RemoveStaleGeneratedFiles(outputRoot, desired);
            removed += SyncManagedLocalFiles(outputRoot, desiredLocalFiles);
            removed += PruneOrphanedDirectories(outputRoot);
        }
        else
        {
            UpdateSyncMessage("Keeping pointers from offline servers until they can be checked again…");
        }
        _logger.LogInformation(
            "Remote library sync completed: {Movies} movies, {Episodes} episodes ({CalendarEpisodes} from calendar), {Skipped} local matches skipped, {Gaps} local-series gaps filled, {Invalid} invalid items skipped, {Removed} stale files removed",
            moviesAdded,
            episodesAdded,
            calendarEpisodesAdded,
            localMatchesSkipped,
            localGapsFilled,
            invalidItemsSkipped,
            removed);
        _libraryManager.QueueLibraryScan();
        return new SyncResult(moviesAdded, episodesAdded, calendarEpisodesAdded, localMatchesSkipped, localGapsFilled, invalidItemsSkipped, removed, outputRoot, onlineServers, offlineServers);
    }

    public async Task SyncCalendarAsync(CancellationToken cancellationToken)
    {
        var config = GetConfiguration();
        if (!config.EnableSeries || !config.EnableCalendar) return;

        var servers = GetServers(config).Where(server => server.Enabled).ToList();
        if (servers.Count == 0) return;

        var outputRoot = ResolveOutputRoot(config);
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(Path.Combine(outputRoot, "Shows"));
        Directory.CreateDirectory(Path.Combine(outputRoot, "Anime"));
        var local = config.SkipLocalMatches ? ScanLocalMedia("/media") : LocalInventory.Empty;
        var seenEpisodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var serverConfig in servers)
        {
            try
            {
                var session = GetConfiguredSession(serverConfig);
                var client = CreateClient(serverConfig.ServerUrl, session.AccessToken);
                var managedMedia = await GetRemoteManagedMediaAsync(client, cancellationToken).ConfigureAwait(false);
                var views = await GetAsync<QueryResult>(client, $"Users/{Uri.EscapeDataString(session.UserId)}/Views", cancellationToken).ConfigureAwait(false);
                var publicInfo = await GetAsync<PublicSystemInfo>(client, "System/Info/Public", cancellationToken).ConfigureAwait(false);
                var sourceLabel = string.IsNullOrWhiteSpace(serverConfig.SourceLabel)
                    ? publicInfo.ServerName ?? new Uri(serverConfig.ServerUrl).Host
                    : serverConfig.SourceLabel.Trim();
                var seriesById = new Dictionary<string, RemoteItem>(StringComparer.OrdinalIgnoreCase);
                var seriesByName = new Dictionary<string, RemoteItem>(StringComparer.OrdinalIgnoreCase);
                var managedSeriesIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var managedSeriesNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var view in views.Items.Where(view => IsSeriesView(view, config)))
                {
                    var seriesItems = await GetAllItemsAsync(client, session.UserId, view.Id, "Series", cancellationToken).ConfigureAwait(false);
                    var episodes = await GetAllItemsAsync(client, session.UserId, view.Id, "Episode", cancellationToken).ConfigureAwait(false);
                    var nativeSeriesIds = episodes
                        .Where(episode => !IsManagedRemoteItem(episode, managedMedia) && !string.IsNullOrWhiteSpace(episode.SeriesId))
                        .Select(episode => episode.SeriesId!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var nativeSeriesNames = episodes
                        .Where(episode => !IsManagedRemoteItem(episode, managedMedia) && !string.IsNullOrWhiteSpace(episode.SeriesName))
                        .Select(episode => SeriesLookupKey(episode.SeriesName))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var series in seriesItems)
                    {
                        if (IsManagedRemoteItem(series, managedMedia)
                            && !nativeSeriesIds.Contains(series.Id)
                            && !nativeSeriesNames.Contains(SeriesLookupKey(series.Name)))
                        {
                            managedSeriesIds.Add(series.Id);
                            managedSeriesNames.Add(SeriesLookupKey(series.Name));
                            continue;
                        }

                        managedSeriesIds.Remove(series.Id);
                        managedSeriesNames.Remove(SeriesLookupKey(series.Name));
                        seriesById.TryAdd(series.Id, series);
                        seriesByName.TryAdd(SeriesLookupKey(series.Name), series);
                    }
                }

                var upcoming = await GetEnhancedCalendarEpisodesAsync(client, cancellationToken).ConfigureAwait(false);
                if (upcoming.Count == 0)
                {
                    upcoming = await GetUpcomingEpisodesAsync(client, session.UserId, cancellationToken).ConfigureAwait(false);
                }
                _logger.LogInformation(
                    "Remote library calendar received {Count} upcoming episodes from {Source}",
                    upcoming.Count,
                    sourceLabel);

                foreach (var episode in upcoming)
                {
                    if (episode.ParentIndexNumber is null || episode.IndexNumber is null
                        || string.IsNullOrWhiteSpace(episode.SeriesName)
                        || IsManagedRemoteItem(episode, managedMedia)
                        || (episode.SeriesId is not null && managedSeriesIds.Contains(episode.SeriesId))
                        || managedSeriesNames.Contains(SeriesLookupKey(episode.SeriesName)))
                    {
                        continue;
                    }

                    var series = episode.SeriesId is not null && seriesById.TryGetValue(episode.SeriesId, out var byId)
                        ? byId
                        : seriesByName.GetValueOrDefault(SeriesLookupKey(episode.SeriesName));
                    var seriesName = series?.Name ?? episode.SeriesName;
                    var seriesYear = series?.ProductionYear;
                    var key = EpisodeKey(seriesName, seriesYear, episode.ParentIndexNumber, episode.IndexNumber);
                    if (!seenEpisodes.Add(key)) continue;

                    var localSeriesKey = SeriesKey(seriesName, seriesYear);
                    var localSeriesWithoutYear = SeriesKey(seriesName, null);
                    if (config.SkipLocalMatches
                        && (local.Episodes.Contains(key) || local.Episodes.Contains(EpisodeKey(seriesName, null, episode.ParentIndexNumber, episode.IndexNumber))))
                    {
                        continue;
                    }

                    var localSeriesPath = local.SeriesPaths.GetValueOrDefault(localSeriesKey)
                        ?? local.SeriesPaths.GetValueOrDefault(localSeriesWithoutYear);
                    if (config.SkipLocalMatches && !string.IsNullOrWhiteSpace(localSeriesPath))
                    {
                        WriteEpisodeInSeriesDirectory(localSeriesPath, episode, seriesName, serverConfig.Id, config.StreamSecret, sourceLabel, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        continue;
                    }

                    var folder = IsAnime(series) ? "Anime" : "Shows";
                    var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // Calendar events may be announced by a server that has no
                    // streamable episode yet. The full library sync owns the
                    // series NFO and writes it from a selected episode pointer.
                    WriteEpisode(outputRoot, folder, episode, seriesName, seriesYear, serverConfig.Id, config.StreamSecret, sourceLabel, desired);
                }

                _logger.LogInformation("Remote library calendar sync completed for {Source}", sourceLabel);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning(exception, "Skipping remote calendar sync from {Source}", SourceLabel(serverConfig));
            }
        }

        _libraryManager.QueueLibraryScan();
    }

    public SyncJobStatus StartSync()
    {
        lock (_syncStateLock)
        {
            if (string.Equals(_syncStatus.State, "Running", StringComparison.Ordinal))
            {
                return _syncStatus;
            }

            if (string.Equals(_syncStatus.State, "Failed", StringComparison.Ordinal)
                && _syncStatus.FinishedUtc.HasValue
                && DateTimeOffset.UtcNow - _syncStatus.FinishedUtc.Value < TimeSpan.FromMinutes(5))
            {
                return _syncStatus;
            }

            _syncStatus = new SyncJobStatus("Running", "Connecting to the remote Jellyfin server…", DateTimeOffset.UtcNow, null, null);
            _ = Task.Run(RunBackgroundSyncAsync);
            return _syncStatus;
        }
    }

    public SyncJobStatus GetSyncStatus()
    {
        lock (_syncStateLock)
        {
            return _syncStatus;
        }
    }

    // Kept as an empty compatibility endpoint for clients that cached an older
    // bridge. Reviews are deliberately local-only and are never fetched from a
    // configured remote server.
    public Task<IReadOnlyList<RemoteReview>> GetReviewsAsync(
        string mediaType,
        string tmdbId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RemoteReview>>([]);

    private void UpdateSyncMessage(string message)
    {
        lock (_syncStateLock)
        {
            if (string.Equals(_syncStatus.State, "Running", StringComparison.Ordinal))
            {
                _syncStatus = _syncStatus with { Message = message };
            }
        }
    }

    private string ResolveOutputRoot(PluginConfiguration config)
    {
        var configured = Path.GetFullPath(config.OutputPath);
        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch (UnauthorizedAccessException) when (Path.IsPathRooted(configured))
        {
            var fallback = Path.Combine(_applicationPaths.DataPath, "remote-library");
            Directory.CreateDirectory(fallback);
            _logger.LogWarning(
                "Remote Library cannot write to configured OutputPath {OutputPath}; using writable fallback {FallbackPath}",
                configured,
                fallback);
            config.OutputPath = fallback;
            Plugin.Instance!.SaveConfiguration();
            return fallback;
        }
    }

    private async Task RunBackgroundSyncAsync()
    {
        try
        {
            var result = await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_syncStateLock)
            {
                _syncStatus = new SyncJobStatus(
                    "Completed",
                    $"Synced {result.OnlineServers} online server(s), skipped {result.OfflineServers} offline; added {result.Movies} movies and {result.Episodes} episodes ({result.CalendarEpisodes} upcoming), filling {result.LocalGapsFilled} local-series gaps.",
                    _syncStatus.StartedUtc,
                    DateTimeOffset.UtcNow,
                    result);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Remote library background sync failed");
            lock (_syncStateLock)
            {
                _syncStatus = new SyncJobStatus("Failed", FriendlyError(exception), _syncStatus.StartedUtc, DateTimeOffset.UtcNow, null);
            }
        }
    }

    private static string FriendlyError(Exception exception)
    {
        if (exception is TaskCanceledException)
        {
            return "The remote Jellyfin server did not respond within 30 seconds.";
        }

        if (exception is HttpRequestException httpException && httpException.StatusCode is not null)
        {
            return $"The remote Jellyfin server returned HTTP {(int)httpException.StatusCode} ({httpException.StatusCode}).";
        }

        return exception.Message;
    }

    public bool ValidateStreamSecret(string supplied)
    {
        var expected = GetConfiguration().StreamSecret;
        return !string.IsNullOrWhiteSpace(expected)
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(supplied ?? string.Empty));
    }

    public ManagedMediaManifest GetManagedMediaManifest()
    {
        var config = GetConfiguration();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
        {
            config.OutputPath,
            Path.Combine(_applicationPaths.DataPath, "remote-library")
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var fullRoot = Path.GetFullPath(root);
            roots.Add(fullRoot);
            var manifest = Path.Combine(fullRoot, ".remote-library-local-files");
            if (!File.Exists(manifest)) continue;
            foreach (var path in File.ReadLines(manifest).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }

        return new ManagedMediaManifest(roots.Order().ToList(), paths.Order().ToList());
    }

    public async Task ProxyAsync(string? serverId, string itemId, bool image, HttpRequest incoming, HttpResponse outgoing, CancellationToken cancellationToken)
    {
        var config = GetConfiguration();
        var serverConfig = string.IsNullOrWhiteSpace(serverId) ? GetServers(config).First() : GetServer(serverId);
        var session = GetConfiguredSession(serverConfig);
        var client = CreateClient(serverConfig.ServerUrl, session.AccessToken);
        var path = image
            ? $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary"
            : $"Videos/{Uri.EscapeDataString(itemId)}/stream?static=true";
        using var request = new HttpRequestMessage(HttpMethods.IsHead(incoming.Method) ? HttpMethod.Head : HttpMethod.Get, path);
        if (incoming.Headers.Range.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", incoming.Headers.Range.ToString());
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        outgoing.StatusCode = (int)response.StatusCode;
        foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified" })
        {
            CopyResponseHeader(response, outgoing, name);
        }

        if (!HttpMethods.IsHead(incoming.Method))
        {
            await response.Content.CopyToAsync(outgoing.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void CopyResponseHeader(HttpResponseMessage source, HttpResponse destination, string name)
    {
        if (source.Headers.TryGetValues(name, out var values) || source.Content.Headers.TryGetValues(name, out values))
        {
            destination.Headers[name] = values.ToArray();
        }
    }

    private static RemoteSession GetConfiguredSession(RemoteServerConfiguration config)
    {
        ValidatePairedConfiguration(config);
        return new RemoteSession(config.AccessToken, config.RemoteUserId, config.RemoteUserName, config.ServerUrl);
    }

    private HttpClient CreateClient(string serverUrl, string? token)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var header = "MediaBrowser Client=\"Remote Library\", Device=\"GLaDOSfin\", DeviceId=\"remote-library-plugin\", Version=\"1.0.3\"";
        if (!string.IsNullOrWhiteSpace(token))
        {
            header += $", Token=\"{token}\"";
        }

        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(header);
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The remote Jellyfin returned an empty response for {path}.");
    }

    private static async Task<List<RemoteItem>> GetAllItemsAsync(
        HttpClient client,
        string userId,
        string parentId,
        string itemType,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var result = new List<RemoteItem>();
        for (var start = 0; ; start += pageSize)
        {
            var path = $"Users/{Uri.EscapeDataString(userId)}/Items?ParentId={Uri.EscapeDataString(parentId)}"
                + $"&Recursive=true&IncludeItemTypes={itemType}&Fields=ProviderIds,Overview,PremiereDate,OriginalTitle,SeriesName,Genres,Tags,Path,MediaSources,ImageTags,RunTimeTicks"
                + $"&StartIndex={start}&Limit={pageSize}";
            var page = await GetAsync<QueryResult>(client, path, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items);
            if (page.Items.Count < pageSize || result.Count >= page.TotalRecordCount)
            {
                return result;
            }
        }
    }

    private static async Task<RemoteManagedMedia> GetRemoteManagedMediaAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await GetAsync<ManagedMediaManifest>(
                client,
                "RemoteLibrary/ManagedMedia",
                cancellationToken).ConfigureAwait(false);
            return new RemoteManagedMedia(manifest.Roots, manifest.Paths);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.Unauthorized)
        {
            return RemoteManagedMedia.Empty;
        }
    }

    private static async Task<List<RemoteItem>> GetUpcomingEpisodesAsync(
        HttpClient client,
        string userId,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var result = new List<RemoteItem>();
        for (var start = 0; ; start += pageSize)
        {
            var path = "Shows/Upcoming"
                + $"?UserId={Uri.EscapeDataString(userId)}"
                + "&Fields=ProviderIds,Overview,PremiereDate,OriginalTitle,SeriesName,Genres,Tags,ImageTags,RunTimeTicks"
                + "&EnableImages=false&EnableUserData=false"
                + $"&StartIndex={start}&Limit={pageSize}";
            var page = await GetAsync<QueryResult>(client, path, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items);
            if (page.Items.Count < pageSize || result.Count >= page.TotalRecordCount)
            {
                return result;
            }
        }
    }

    private static async Task<List<RemoteItem>> GetEnhancedCalendarEpisodesAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(365);
        var path = "JellyfinEnhanced/arr/calendar"
            + $"?start={Uri.EscapeDataString(start.ToString("O"))}"
            + $"&end={Uri.EscapeDataString(end.ToString("O"))}";
        EnhancedCalendarResult calendar;
        try
        {
            calendar = await GetAsync<EnhancedCalendarResult>(client, path, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            return [];
        }
        var result = new List<RemoteItem>();
        foreach (var entry in calendar.Events)
        {
            if (!string.Equals(entry.ReleaseType, "Episode", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.Title)
                || entry.SeasonNumber is null
                || entry.EpisodeNumber is null
                || !DateTimeOffset.TryParse(entry.ReleaseDate, out _))
            {
                continue;
            }

            var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddProviderId(providerIds, "Tvdb", entry.TvdbId);
            AddProviderId(providerIds, "Imdb", entry.ImdbId);
            AddProviderId(providerIds, "Tmdb", entry.TmdbId);
            AddProviderId(providerIds, "Tvdb", entry.EpisodeTvdbId);
            AddProviderId(providerIds, "Imdb", entry.EpisodeImdbId);

            result.Add(new RemoteItem
            {
                Id = string.IsNullOrWhiteSpace(entry.ItemId) ? entry.Id ?? string.Empty : entry.ItemId,
                Name = string.IsNullOrWhiteSpace(entry.EpisodeTitle) ? entry.Title : entry.EpisodeTitle,
                SeriesName = entry.Title,
                ParentIndexNumber = entry.SeasonNumber,
                IndexNumber = entry.EpisodeNumber,
                PremiereDate = entry.ReleaseDate,
                RemoteCalendarDate = entry.ReleaseDate,
                Overview = entry.Overview,
                ProviderIds = providerIds
            });
        }

        return result;
    }

    private static void AddProviderId(Dictionary<string, string> providerIds, string name, object? value)
    {
        var text = Convert.ToString(value);
        if (!string.IsNullOrWhiteSpace(text) && !providerIds.ContainsKey(name))
        {
            providerIds[name] = text;
        }
    }

    private static PluginConfiguration GetConfiguration()
        => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Remote Library is not initialized.");

    private static IReadOnlyList<RemoteServerConfiguration> GetServers(PluginConfiguration config)
    {
        if (config.Servers.Count == 0 && !string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            config.Servers.Add(new RemoteServerConfiguration
            {
                Id = Guid.NewGuid().ToString("N"),
                ServerUrl = config.ServerUrl,
                AccessToken = config.AccessToken,
                RemoteUserId = config.RemoteUserId,
                RemoteUserName = config.RemoteUserName,
                SourceLabel = config.SourceLabel,
                Enabled = true
            });
            Plugin.Instance!.SaveConfiguration();
        }

        return config.Servers;
    }

    private static RemoteServerConfiguration GetServer(string serverId)
        => GetServers(GetConfiguration()).FirstOrDefault(server => string.Equals(server.Id, serverId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("That remote server configuration no longer exists.");

    private static string SourceLabel(RemoteServerConfiguration server)
        => !string.IsNullOrWhiteSpace(server.SourceLabel)
            ? server.SourceLabel.Trim()
            : Uri.TryCreate(server.ServerUrl, UriKind.Absolute, out var uri) ? uri.Host : "Remote";

    private static void ValidateServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS Jellyfin server URL.");
        }
    }

    private static void ValidatePairedConfiguration(RemoteServerConfiguration config)
    {
        ValidateServerUrl(config.ServerUrl);
        if (string.IsNullOrWhiteSpace(config.AccessToken) || string.IsNullOrWhiteSpace(config.RemoteUserId))
        {
            throw new InvalidOperationException("Pair with the remote Jellyfin server using Quick Connect first.");
        }
    }

    private static void WriteMovie(string root, RemoteItem item, string serverId, string secret, string sourceLabel, HashSet<string> desired)
    {
        var display = SafeName($"{item.Name} ({item.ProductionYear?.ToString() ?? "Unknown"})");
        WritePointerPair(Path.Combine(root, "Movies", display), display, "movie", item, item.Name, serverId, secret, sourceLabel, desired);
    }

    private static void WriteEpisode(
        string root,
        string libraryFolder,
        RemoteItem item,
        string? seriesName,
        int? seriesYear,
        string serverId,
        string secret,
        string sourceLabel,
        HashSet<string> desired)
    {
        var series = SafeName(seriesName ?? "Unknown Series");
        var seriesFolder = seriesYear is null ? series : SafeName($"{series} ({seriesYear})");
        WriteEpisodeInSeriesDirectory(
            Path.Combine(root, libraryFolder, seriesFolder),
            item,
            seriesName,
            serverId,
            secret,
            sourceLabel,
            desired);
    }

    private static void WriteEpisodeInSeriesDirectory(
        string seriesDirectory,
        RemoteItem item,
        string? seriesName,
        string serverId,
        string secret,
        string sourceLabel,
        HashSet<string> desired)
    {
        var series = SafeName(seriesName ?? "Unknown Series");
        var season = Math.Max(item.ParentIndexNumber ?? 0, 0);
        var episode = Math.Max(item.IndexNumber ?? 0, 0);
        var stem = SafeName($"{series} - S{season:00}E{episode:00} - {EpisodeDisplayName(item)}");
        WritePointerPair(Path.Combine(seriesDirectory, $"Season {season:00}"), stem, "episodedetails", item, seriesName, serverId, secret, sourceLabel, desired);
    }

    private static void WriteSeriesMetadata(
        string root,
        string libraryFolder,
        RemoteItem series,
        string serverId,
        string secret,
        string sourceLabel,
        HashSet<string> desired)
    {
        var seriesName = SafeName(series.Name);
        var seriesFolder = series.ProductionYear is null ? seriesName : SafeName($"{seriesName} ({series.ProductionYear})");
        var directory = Path.Combine(root, libraryFolder, seriesFolder);
        Directory.CreateDirectory(directory);
        var nfo = Path.Combine(directory, "tvshow.nfo");
        var document = CreateNfo("tvshow", series, series.Name, serverId, secret, sourceLabel);
        WriteTextIfChanged(nfo, SerializeNfo(document));
        desired.Add(Path.GetFullPath(nfo));
    }

    private static RemoteItem SeriesMetadataFor(RemoteItem? series, RemoteItem episode, string seriesName, int? seriesYear)
    {
        if (series is not null)
        {
            return series;
        }

        // Some views return episodes without their parent Series item. Generate
        // source-specific show metadata so an NFO from a different server cannot
        // survive indefinitely beside the newly selected episode pointers.
        return new RemoteItem
        {
            Id = episode.SeriesId ?? episode.Id,
            Name = seriesName,
            ProductionYear = seriesYear,
            Overview = episode.Overview,
            ProviderIds = new Dictionary<string, string>(episode.ProviderIds, StringComparer.OrdinalIgnoreCase),
            ImageTags = new Dictionary<string, string>(episode.ImageTags, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void WritePointerPair(
        string directory,
        string stem,
        string nfoRoot,
        RemoteItem item,
        string? seriesName,
        string serverId,
        string secret,
        string sourceLabel,
        HashSet<string> desired)
    {
        Directory.CreateDirectory(directory);
        var strm = Path.Combine(directory, stem + ".strm");
        var nfo = Path.Combine(directory, stem + ".nfo");
        WriteTextIfChanged(nfo, SerializeNfo(CreateNfo(nfoRoot, item, seriesName, serverId, secret, sourceLabel)));
        // Jellyfin's file watcher may index a new stream pointer immediately. Ensure its
        // sidecar exists first so the initial import cannot fall back to a release filename.
        WriteTextIfChanged(strm, $"http://127.0.0.1:8096/RemoteLibrary/Stream/{Uri.EscapeDataString(serverId)}/{Uri.EscapeDataString(item.Id)}?secret={Uri.EscapeDataString(secret)}\n");
        desired.Add(Path.GetFullPath(strm));
        desired.Add(Path.GetFullPath(nfo));
    }

    private static XDocument CreateNfo(string nfoRoot, RemoteItem item, string? seriesName, string serverId, string secret, string sourceLabel)
    {
        var displayName = nfoRoot == "episodedetails" ? EpisodeDisplayName(item) : item.Name;
        var root = new XElement(
            nfoRoot,
            new XElement("title", displayName),
            string.IsNullOrWhiteSpace(item.OriginalTitle) ? null : new XElement("originaltitle", item.OriginalTitle),
            item.ProductionYear is null ? null : new XElement("year", item.ProductionYear),
            string.IsNullOrWhiteSpace(item.Overview) ? null : new XElement("plot", item.Overview),
            string.IsNullOrWhiteSpace(item.PremiereDate) ? null : new XElement("premiered", item.PremiereDate),
            nfoRoot == "episodedetails" && !string.IsNullOrWhiteSpace(item.PremiereDate)
                ? new XElement("aired", item.PremiereDate)
                : null,
            nfoRoot == "episodedetails" ? new XElement("showtitle", seriesName) : null,
            nfoRoot == "episodedetails" ? new XElement("season", item.ParentIndexNumber ?? 0) : null,
            nfoRoot == "episodedetails" ? new XElement("episode", item.IndexNumber ?? 0) : null,
            nfoRoot != "tvshow" && RuntimeMinutes(item.RunTimeTicks) is { } runtime
                ? new XElement("runtime", runtime)
                : null,
            item.ProviderIds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new XElement("uniqueid", new XAttribute("type", pair.Key.ToLowerInvariant()), pair.Value)),
            new XElement("tag", "Remote Library"),
            new XElement("tag", $"Remote Source: {sourceLabel}"),
            string.IsNullOrWhiteSpace(item.RemoteCalendarDate)
                ? null
                : new XElement("tag", $"Remote Calendar Date: {item.RemoteCalendarDate}"),
            item.ImageTags.ContainsKey("Primary")
                ? new XElement("thumb", $"http://127.0.0.1:8096/RemoteLibrary/Image/{Uri.EscapeDataString(serverId)}/{Uri.EscapeDataString(item.Id)}?secret={Uri.EscapeDataString(secret)}")
                : null);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static long? RuntimeMinutes(long? runTimeTicks)
    {
        if (runTimeTicks is not > 0)
        {
            return null;
        }

        // Jellyfin's NFO importer reads runtime in whole minutes. Round up so a
        // partial final minute is preserved for Ends At and other runtime users.
        return (runTimeTicks.Value + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute;
    }

    private static string EpisodeDisplayName(RemoteItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.OriginalTitle)
            && LooksLikeReleaseFilenameRegex().IsMatch(item.Name))
        {
            return item.OriginalTitle.Trim();
        }

        return item.Name;
    }

    private static string SerializeNfo(XDocument document)
        => $"{document.Declaration}{Environment.NewLine}{document.Root}{Environment.NewLine}";

    private static void WriteTextIfChanged(string path, string content)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content);
    }

    private static int RemoveStaleGeneratedFiles(string root, HashSet<string> desired)
    {
        var removed = 0;
        foreach (var extension in new[] { "*.strm", "*.nfo" })
        {
            foreach (var file in Directory.EnumerateFiles(root, extension, SearchOption.AllDirectories))
            {
                if (!desired.Contains(Path.GetFullPath(file)))
                {
                    File.Delete(file);
                    removed++;
                }
            }
        }

        return removed;
    }

    private static int PruneOrphanedDirectories(string root)
    {
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Count(character => character == Path.DirectorySeparatorChar)))
        {
            if (Directory.Exists(directory)
                && !Directory.EnumerateFiles(directory, "*.strm", SearchOption.AllDirectories).Any()
                && !Directory.EnumerateFiles(directory, "*.nfo", SearchOption.AllDirectories).Any())
            {
                removed += Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
                Directory.Delete(directory, recursive: true);
            }
        }

        return removed;
    }

    private static int SyncManagedLocalFiles(string outputRoot, HashSet<string> desired)
    {
        var manifest = Path.Combine(outputRoot, ".remote-library-local-files");
        var previous = File.Exists(manifest)
            ? File.ReadAllLines(manifest).Where(path => !string.IsNullOrWhiteSpace(path)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var path in previous.Except(desired, StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(path);
            var isInsideMedia = fullPath.StartsWith("/media/Shows/", StringComparison.Ordinal)
                || fullPath.StartsWith("/media/Anime/", StringComparison.Ordinal);
            if (!isInsideMedia || !File.Exists(fullPath))
            {
                continue;
            }

            var extension = Path.GetExtension(fullPath);
            var content = File.ReadAllText(fullPath);
            var isManaged = string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase)
                ? content.Contains("/RemoteLibrary/Stream/", StringComparison.Ordinal)
                : string.Equals(extension, ".nfo", StringComparison.OrdinalIgnoreCase)
                    && content.Contains("<tag>Remote Library</tag>", StringComparison.Ordinal);
            if (isManaged)
            {
                File.Delete(fullPath);
                removed++;
            }
        }

        var manifestContent = desired.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, desired.Order(StringComparer.OrdinalIgnoreCase)) + Environment.NewLine;
        WriteTextIfChanged(manifest, manifestContent);
        return removed;
    }

    private static bool IsAnime(RemoteItem? series)
        => series is not null
            && series.Tags.Any(tag => string.Equals(tag, "anime", StringComparison.OrdinalIgnoreCase));

    private static bool IsSeriesView(RemoteItem view, PluginConfiguration config)
        => string.Equals(view.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(view.CollectionType)
                && (string.Equals(view.Name, config.SeriesLibraryName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(view.Name, config.AnimeLibraryName, StringComparison.OrdinalIgnoreCase)));

    private static bool IsManagedRemoteItem(RemoteItem item, RemoteManagedMedia managedMedia)
    {
        var paths = item.MediaSources
            .Select(source => source.Path)
            .Prepend(item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        // Paths are stronger evidence than inherited metadata tags. A real episode
        // can inherit a series-level Remote Source tag after remote gaps were added;
        // its normal media path must keep it eligible for export. Managed pointers
        // remain identifiable by their remote-library root or manifest entry.
        if (paths.Any(path => IsRemoteLibraryPath(path) || managedMedia.Contains(path)))
        {
            return true;
        }

        if (paths.Count > 0)
        {
            return false;
        }

        return item.Tags.Any(tag => string.Equals(tag, "Remote Library", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("Remote Source:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRemoteLibraryPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && (path.Contains("remote-library", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/RemoteLibrary/Stream/", StringComparison.OrdinalIgnoreCase));

    private static string SeriesLookupKey(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : Regex.Replace(name.Trim(), @"[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);

    private static LocalInventory ScanLocalMedia(string root)
    {
        if (!Directory.Exists(root))
        {
            return LocalInventory.Empty;
        }

        var movies = new HashSet<string>(StringComparer.Ordinal);
        var episodes = new HashSet<string>(StringComparer.Ordinal);
        var seriesPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seriesRoot in new[] { Path.Combine(root, "Shows"), Path.Combine(root, "Anime") })
        {
            if (!Directory.Exists(seriesRoot))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(seriesRoot))
            {
                var name = Path.GetFileName(directory);
                var yearMatch = YearRegex().Match(name);
                seriesPaths.TryAdd(SeriesKey(
                    StripYear(name),
                    yearMatch.Success
                        ? int.Parse(yearMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                        : null), directory);
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!MediaExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            var episode = EpisodeFileRegex().Match(Path.GetFileNameWithoutExtension(file));
            if (episode.Success)
            {
                var parent = Directory.GetParent(file);
                var series = parent is not null && SeasonFolderRegex().IsMatch(parent.Name)
                    ? parent.Parent?.Name
                    : parent?.Name;
                var seriesYearMatch = YearRegex().Match(series ?? string.Empty);
                int? seriesYear = seriesYearMatch.Success
                    ? int.Parse(seriesYearMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                episodes.Add(EpisodeKey(
                    StripYear(series),
                    seriesYear,
                    int.Parse(episode.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(episode.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }

            var candidate = Path.GetFileName(Path.GetDirectoryName(file)) ?? Path.GetFileNameWithoutExtension(file);
            var yearMatch = YearRegex().Match(candidate);
            if (yearMatch.Success)
            {
                movies.Add(MovieKey(
                    StripYear(candidate),
                    int.Parse(yearMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return new LocalInventory(movies, episodes, seriesPaths);
    }

    private static string MovieKey(string? name, int? year) => $"{Normalize(name)}:{year ?? 0}";

    private static string EpisodeKey(string? series, int? seriesYear, int? season, int? episode)
        => $"{Normalize(StripYear(series))}:{seriesYear ?? 0}:s{season ?? 0}:e{episode ?? 0}";

    private static string SeriesKey(string? series, int? seriesYear)
        => $"{Normalize(StripYear(series))}:{seriesYear ?? 0}";

    private static string StripYear(string? value) => YearRegex().Replace(value ?? string.Empty, string.Empty).Trim();

    private static string Normalize(string? value)
        => string.Join(' ', NonAlphaNumericRegex()
            .Replace((value ?? string.Empty).ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safe))
        {
            return "Unknown";
        }

        const int maxUtf8Bytes = 180;
        while (System.Text.Encoding.UTF8.GetByteCount(safe) > maxUtf8Bytes)
        {
            safe = safe[..^1].TrimEnd();
        }

        return safe;
    }

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".webm"
    };

    [GeneratedRegex(@"(?i)S(\d{1,2})E(\d{1,3})")]
    private static partial Regex EpisodeFileRegex();

    [GeneratedRegex(@"(?i)^Season\s+\d+$")]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"[\(\[]((?:19|20)\d{2})[\)\]]")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"(?ix)(?:\bS\d{1,2}E\d{1,3}\b|\b(?:480|720|1080|2160)p\b|\b(?:BluRay|WEB[-_. ]?DL|WEBRip|HDTV|Remux|x26[45]|HEVC|AV1)\b)")]
    private static partial Regex LooksLikeReleaseFilenameRegex();

    private sealed record RemoteSession(string AccessToken, string UserId, string UserName, string ServerName);

    private sealed record AuthenticationResult(string AccessToken, RemoteUser User);

    private sealed class QuickConnectResult
    {
        public string Secret { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public bool Authenticated { get; set; }
    }

    private sealed record PublicSystemInfo(string? ServerName);

    private sealed record RemoteUser(string Id, string Name);

    private sealed class QueryResult
    {
        public List<RemoteItem> Items { get; set; } = [];

        public int TotalRecordCount { get; set; }
    }

    private sealed class EnhancedCalendarResult
    {
        public List<EnhancedCalendarEvent> Events { get; set; } = [];
    }

    private sealed class EnhancedCalendarEvent
    {
        public string? Id { get; set; }
        public string? ItemId { get; set; }
        public string? Title { get; set; }
        public string? ReleaseDate { get; set; }
        public string? ReleaseType { get; set; }
        public string? EpisodeTitle { get; set; }
        public string? Overview { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public int? TvdbId { get; set; }
        public int? TmdbId { get; set; }
        public string? ImdbId { get; set; }
        public int? EpisodeTvdbId { get; set; }
        public string? EpisodeImdbId { get; set; }
    }

    private sealed class RemoteItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? OriginalTitle { get; set; }
        public string? SeriesName { get; set; }
        public string? SeriesId { get; set; }
        public string? CollectionType { get; set; }
        public int? ProductionYear { get; set; }
        public int? ParentIndexNumber { get; set; }
        public int? IndexNumber { get; set; }
        public string? PremiereDate { get; set; }
        public long? RunTimeTicks { get; set; }
        public string? RemoteCalendarDate { get; set; }
        public string? Overview { get; set; }
        public string? Path { get; set; }
        public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Genres { get; set; } = [];
        public List<string> Tags { get; set; } = [];
        public List<RemoteMediaSource> MediaSources { get; set; } = [];
        public Dictionary<string, string> ImageTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RemoteMediaSource
    {
        public string? Path { get; set; }
    }

    private sealed record RemoteSeriesMetadata(string LibraryFolder, RemoteItem Series, string ServerId, string SourceLabel);

    private sealed class RemoteManagedMedia
    {
        public static RemoteManagedMedia Empty { get; } = new([], []);

        private readonly IReadOnlyList<string> _roots;
        private readonly HashSet<string> _paths;

        public RemoteManagedMedia(IEnumerable<string> roots, IEnumerable<string> paths)
        {
            _roots = roots.Select(Normalize).Where(path => path.Length > 0).ToList();
            _paths = paths.Select(Normalize).Where(path => path.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public bool Contains(string? path)
        {
            var normalized = Normalize(path);
            if (normalized.Length == 0) return false;
            return _paths.Contains(normalized)
                || _roots.Any(root => string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string? path)
            => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');
    }

    public sealed class RemoteReview
    {
        [System.Text.Json.Serialization.JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("userName")]
        public string? UserName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("tmdbId")]
        public string? TmdbId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mediaType")]
        public string? MediaType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public string? Source { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("avatarUrl")]
        public string? AvatarUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("imported")]
        public bool Imported { get; set; }
    }

    private sealed record LocalInventory(
        HashSet<string> Movies,
        HashSet<string> Episodes,
        Dictionary<string, string> SeriesPaths)
    {
        public static LocalInventory Empty { get; } = new([], [], new(StringComparer.Ordinal));
    }
}

public sealed record RemoteStatus(bool Connected, string? ServerName, string? Username, string? Error);

public sealed record RemoteServerReachability(string ServerId, string SourceLabel, bool Online);

public sealed record RemoteReachabilitySummary(IReadOnlyList<RemoteServerReachability> Servers, DateTimeOffset CheckedUtc);

public sealed record ManagedMediaManifest(IReadOnlyList<string> Roots, IReadOnlyList<string> Paths);

public sealed record QuickConnectStart(string Code, string Instructions);

public sealed record SyncResult(
    int Movies,
    int Episodes,
    int CalendarEpisodes,
    int LocalMatchesSkipped,
    int LocalGapsFilled,
    int InvalidItemsSkipped,
    int StaleFilesRemoved,
    string OutputPath,
    int OnlineServers,
    int OfflineServers);

public sealed record SyncJobStatus(string State, string Message, DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc, SyncResult? Result);
