using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.RemoteLibrary.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteLibrary.Services;

public sealed partial class RemoteLibraryService
{
    private const string ImportedReviewPrefix = "[Remote Source: ";
    private readonly SemaphoreSlim _reviewSyncLock = new(1, 1);

    private string EnhancedReviewsPath => Path.Combine(
        _applicationPaths.PluginsPath,
        "configurations",
        "Jellyfin.Plugin.JellyfinEnhanced",
        "reviews.json");

    private string ImportedReviewsRegistryPath => Path.Combine(
        _applicationPaths.DataPath,
        "remote-library-imported-reviews.json");

    private string ReviewDiscoveryStatePath => Path.Combine(
        _applicationPaths.DataPath,
        "remote-library-review-discovery.json");

    public async Task<ReviewSyncResult> SyncReviewsAsync(CancellationToken cancellationToken)
    {
        await _reviewSyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var imported = 0;
            var removed = 0;
            var onlineServers = 0;
            var skippedServers = 0;

            foreach (var server in GetServers(GetConfiguration()).Where(item => item.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var session = GetConfiguredSession(server);
                    var client = CreateClient(server.ServerUrl, session.AccessToken);
                    var reviews = await FetchExportableReviewsAsync(client, server, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await SyncReviewAvatarsAsync(client, server, reviews, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException exception)
                    {
                        _logger.LogDebug(exception, "Could not refresh one or more review avatars from {Source}", SourceLabel(server));
                    }
                    var result = ImportReviews(server, SourceLabel(server), reviews);
                    imported += result.Imported;
                    removed += result.Removed;
                    onlineServers++;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    skippedServers++;
                    _logger.LogWarning(
                        exception,
                        "Skipping review sync from {Source}; the paired account may need administrator access when the remote server does not have the current Remote Library release or newer",
                        SourceLabel(server));
                }
            }

            return new ReviewSyncResult(imported, removed, onlineServers, skippedServers);
        }
        finally
        {
            _reviewSyncLock.Release();
        }
    }

    public IReadOnlyList<RemoteReview> GetExportableReviews()
        => ReadLocalReviews(includeImported: false, mediaType: null, tmdbId: null);

    public IReadOnlyList<RemoteReview> GetLocalReviews(string mediaType, string tmdbId)
        => ReadLocalReviews(includeImported: true, mediaType, tmdbId);

    public bool TryGetReviewAvatar(string serverId, string userId, out string path, out string contentType)
    {
        path = ReviewAvatarPath(serverId, userId);
        contentType = "image/jpeg";
        if (!File.Exists(path)) return false;
        var contentTypePath = path + ".type";
        if (File.Exists(contentTypePath))
        {
            var stored = File.ReadAllText(contentTypePath).Trim();
            if (stored.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) contentType = stored;
        }

        return true;
    }

    private async Task SyncReviewAvatarsAsync(
        HttpClient client,
        RemoteServerConfiguration server,
        IReadOnlyList<RemoteReview> reviews,
        CancellationToken cancellationToken)
    {
        var userIds = reviews.Select(review => review.UserId)
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        await Parallel.ForEachAsync(
            userIds,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            async (userId, token) =>
            {
                var path = ReviewAvatarPath(server.Id, userId);
                var missingPath = path + ".missing";
                var newestCache = new[] { path, missingPath }
                    .Where(File.Exists)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                if (DateTime.UtcNow - newestCache < TimeSpan.FromHours(1)) return;

                using var response = await client.GetAsync(
                    $"Users/{Uri.EscapeDataString(userId)}/Images/Primary?width=128&quality=90",
                    HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound
                    or HttpStatusCode.NoContent
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.Unauthorized)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(missingPath, DateTimeOffset.UtcNow.ToString("O"));
                    return;
                }

                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                File.WriteAllText(path + ".type", response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
                if (File.Exists(missingPath)) File.Delete(missingPath);
            }).ConfigureAwait(false);
    }

    private string ReviewAvatarPath(string serverId, string userId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{serverId}:{userId}"))).ToLowerInvariant();
        return Path.Combine(_applicationPaths.DataPath, "remote-library-review-avatars", key + ".img");
    }

    private string ReviewAvatarUrl(string serverId, string userId)
    {
        var secret = GetConfiguration().StreamSecret;
        return $"/RemoteLibrary/ReviewAvatar/{Uri.EscapeDataString(serverId)}/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(secret)}";
    }

    private async Task<IReadOnlyList<RemoteReview>> FetchExportableReviewsAsync(
        HttpClient client,
        RemoteServerConfiguration server,
        CancellationToken cancellationToken)
    {
        var pluginResult = await FetchPagedReviewsAsync(
            client,
            "RemoteLibrary/Reviews/Export",
            allowMissing: true,
            cancellationToken).ConfigureAwait(false);
        if (pluginResult is not null)
        {
            return pluginResult;
        }

        // Compatibility for servers which have Jellyfin Enhanced but have not
        // yet updated Remote Library. Enhanced limits this endpoint to admins.
        var enhancedAdminResult = await FetchPagedReviewsAsync(
            client,
            "JellyfinEnhanced/reviews/admin/all",
            allowMissing: true,
            cancellationToken).ConfigureAwait(false);
        if (enhancedAdminResult is not null)
        {
            return enhancedAdminResult;
        }

        // A non-admin paired account cannot use Enhanced's bulk endpoint. For
        // older Remote Library versions, refresh known/relevant review keys on
        // every five-minute run and do the expensive full discovery hourly.
        var discoveryState = ReadReviewDiscoveryState();
        var discoveryDue = !discoveryState.LastFullDiscoveryUtc.TryGetValue(server.Id, out var lastDiscovery)
            || DateTimeOffset.UtcNow - lastDiscovery >= TimeSpan.FromHours(1);
        IReadOnlyCollection<string> keys;
        if (discoveryDue)
        {
            keys = await DiscoverRemoteReviewKeysAsync(client, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            keys = GetRelevantReviewKeys(server.Id);
        }

        var reviews = await FetchVisibleReviewsForKeysAsync(client, keys, cancellationToken).ConfigureAwait(false);
        if (discoveryDue)
        {
            discoveryState.LastFullDiscoveryUtc[server.Id] = DateTimeOffset.UtcNow;
            WriteReviewDiscoveryState(discoveryState);
        }

        return reviews;
    }

    private static async Task<IReadOnlyList<RemoteReview>?> FetchPagedReviewsAsync(
        HttpClient client,
        string endpoint,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        const int pageSize = 1000;
        var reviews = new List<RemoteReview>();
        for (var offset = 0; ; offset += pageSize)
        {
            var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            using var response = await client.GetAsync(
                $"{endpoint}{separator}limit={pageSize}&offset={offset}",
                cancellationToken).ConfigureAwait(false);
            if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<ReviewPage>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The remote Jellyfin returned an empty response for {endpoint}.");
            reviews.AddRange(page.Reviews);
            if (reviews.Count >= page.Total || page.Reviews.Count < pageSize)
            {
                return reviews;
            }
        }
    }

    private static async Task<IReadOnlyCollection<string>> DiscoverRemoteReviewKeysAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var currentUser = await GetAsync<RemoteUser>(client, "Users/Me", cancellationToken).ConfigureAwait(false);
        var views = await GetAsync<QueryResult>(
            client,
            $"Users/{Uri.EscapeDataString(currentUser.Id)}/Views",
            cancellationToken).ConfigureAwait(false);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var view in views.Items)
        {
            if (string.Equals(view.CollectionType, "movies", StringComparison.OrdinalIgnoreCase))
            {
                var movies = await GetAllItemsAsync(client, currentUser.Id, view.Id, "Movie", cancellationToken).ConfigureAwait(false);
                foreach (var movie in movies)
                {
                    if (movie.ProviderIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrWhiteSpace(tmdbId))
                    {
                        keys.Add($"movie|{tmdbId}");
                    }
                }
            }

            if (!string.Equals(view.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var series = await GetAllItemsAsync(client, currentUser.Id, view.Id, "Series", cancellationToken).ConfigureAwait(false);
            var seriesTmdbIds = series
                .Where(item => item.ProviderIds.TryGetValue("Tmdb", out var id) && !string.IsNullOrWhiteSpace(id))
                .ToDictionary(item => item.Id, item => item.ProviderIds["Tmdb"], StringComparer.OrdinalIgnoreCase);
            foreach (var tmdbId in seriesTmdbIds.Values)
            {
                keys.Add($"tv|{tmdbId}");
            }

            var episodes = await GetAllItemsAsync(client, currentUser.Id, view.Id, "Episode", cancellationToken).ConfigureAwait(false);
            foreach (var episode in episodes)
            {
                if (episode.SeriesId is null
                    || !seriesTmdbIds.TryGetValue(episode.SeriesId, out var seriesTmdbId)
                    || episode.ParentIndexNumber is null)
                {
                    continue;
                }

                keys.Add($"tv|{seriesTmdbId}:s{episode.ParentIndexNumber}");
                if (episode.IndexNumber is not null)
                {
                    keys.Add($"tv|{seriesTmdbId}:s{episode.ParentIndexNumber}:e{episode.IndexNumber}");
                }
            }
        }

        return keys;
    }

    private static async Task<IReadOnlyList<RemoteReview>> FetchVisibleReviewsForKeysAsync(
        HttpClient client,
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        var reviews = new ConcurrentBag<RemoteReview>();
        await Parallel.ForEachAsync(
            keys,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 },
            async (candidate, token) =>
            {
                var split = candidate.IndexOf('|');
                var mediaType = candidate[..split];
                var tmdbId = candidate[(split + 1)..];
                try
                {
                    var page = await GetAsync<ReviewPage>(
                        client,
                        $"JellyfinEnhanced/reviews/{mediaType}/{Uri.EscapeDataString(tmdbId)}",
                        token).ConfigureAwait(false);
                    foreach (var review in page.Reviews)
                    {
                        reviews.Add(review);
                    }
                }
                catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    // Enhanced reviews are not enabled or visible for this item.
                }
            }).ConfigureAwait(false);

        return reviews.ToList();
    }

    private IReadOnlyCollection<string> GetRelevantReviewKeys(string serverId)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registry = ReadImportedRegistry();
        foreach (var pair in registry.Entries)
        {
            if (!string.Equals(pair.Value.SourceServerId, serverId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddCandidateFromStoreKey(keys, pair.Key);
        }

        // Any title reviewed locally is likely to be the title for which a
        // remote review is wanted. Refresh those keys every five minutes too.
        var root = ReadReviewsDocument(EnhancedReviewsPath);
        if (root["Reviews"] is JsonObject store)
        {
            foreach (var pair in store)
            {
                AddCandidateFromStoreKey(keys, pair.Key);
            }
        }

        return keys;
    }

    private static void AddCandidateFromStoreKey(HashSet<string> keys, string storeKey)
    {
        var first = storeKey.IndexOf(':');
        if (first < 0 || first == storeKey.Length - 1)
        {
            return;
        }

        var remainder = storeKey[(first + 1)..];
        var second = remainder.IndexOf(':');
        if (second <= 0 || second == remainder.Length - 1)
        {
            return;
        }

        var mediaType = remainder[..second];
        var tmdbId = remainder[(second + 1)..];
        if (mediaType is "movie" or "tv" && Regex.IsMatch(tmdbId, @"^\d+(:s\d+(:e\d+)?)?$", RegexOptions.CultureInvariant))
        {
            keys.Add($"{mediaType}|{tmdbId}");
        }
    }

    private ReviewSyncResult ImportReviews(
        RemoteServerConfiguration server,
        string sourceLabel,
        IReadOnlyList<RemoteReview> remoteReviews)
    {
        var reviewsPath = EnhancedReviewsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(reviewsPath)!);

        var root = ReadReviewsDocument(reviewsPath);
        var store = root["Reviews"] as JsonObject;
        if (store is null)
        {
            store = new JsonObject();
            root["Reviews"] = store;
        }
        var registry = ReadImportedRegistry();
        var desiredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = 0;

        foreach (var review in remoteReviews)
        {
            if (!IsValidReview(review))
            {
                continue;
            }

            var originalUserId = string.IsNullOrWhiteSpace(review.UserId) ? review.UserName ?? "unknown" : review.UserId;
            var syntheticUserId = SyntheticReviewUserId(server.Id, originalUserId);
            var key = $"{syntheticUserId}:{review.MediaType!.ToLowerInvariant()}:{review.TmdbId}";
            desiredKeys.Add(key);

            var originalContent = review.Content ?? string.Empty;
            var author = string.IsNullOrWhiteSpace(review.UserName) ? originalUserId : review.UserName.Trim();
            var tag = $"{ImportedReviewPrefix}{sourceLabel}; User: {author}]";
            var taggedContent = string.IsNullOrWhiteSpace(originalContent) ? tag : $"{tag}\n{originalContent}";
            var now = DateTimeOffset.UtcNow.ToString("O");

            store[key] = new JsonObject
            {
                ["UserId"] = syntheticUserId,
                ["TmdbId"] = review.TmdbId,
                ["MediaType"] = review.MediaType.ToLowerInvariant(),
                ["Content"] = taggedContent,
                ["Rating"] = review.Rating,
                ["CreatedAt"] = string.IsNullOrWhiteSpace(review.CreatedAt) ? now : review.CreatedAt,
                ["UpdatedAt"] = string.IsNullOrWhiteSpace(review.UpdatedAt) ? now : review.UpdatedAt,
                ["RemoteLibraryImported"] = true,
                ["RemoteLibrarySource"] = sourceLabel,
                ["RemoteLibrarySourceServerId"] = server.Id,
                ["RemoteLibraryOriginalUserId"] = originalUserId,
                ["RemoteLibraryOriginalUserName"] = author,
                ["RemoteLibraryOriginalContent"] = originalContent
            };
            registry.Entries[key] = new ImportedReviewMetadata(
                server.Id,
                sourceLabel,
                originalUserId,
                author,
                originalContent);
            imported++;
        }

        var staleKeys = registry.Entries
            .Where(pair => string.Equals(pair.Value.SourceServerId, server.Id, StringComparison.OrdinalIgnoreCase)
                && !desiredKeys.Contains(pair.Key))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in staleKeys)
        {
            store.Remove(key);
            registry.Entries.Remove(key);
        }

        var backupPath = reviewsPath + ".remote-library-backup";
        if (File.Exists(reviewsPath) && !File.Exists(backupPath))
        {
            File.Copy(reviewsPath, backupPath);
        }

        WriteJsonAtomic(reviewsPath, root);
        WriteImportedRegistry(registry);
        return new ReviewSyncResult(imported, staleKeys.Count, 1, 0);
    }

    private IReadOnlyList<RemoteReview> ReadLocalReviews(
        bool includeImported,
        string? mediaType,
        string? tmdbId)
    {
        var root = ReadReviewsDocument(EnhancedReviewsPath);
        var store = root["Reviews"] as JsonObject;
        if (store is null)
        {
            return [];
        }

        var registry = ReadImportedRegistry();
        var reviews = new List<RemoteReview>();
        foreach (var pair in store)
        {
            if (pair.Value is not JsonObject value)
            {
                continue;
            }

            var storedType = JsonString(value, "MediaType");
            var storedId = JsonString(value, "TmdbId");
            if (mediaType is not null && !string.Equals(storedType, mediaType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tmdbId is not null && !string.Equals(storedId, tmdbId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            registry.Entries.TryGetValue(pair.Key, out var metadata);
            var content = JsonString(value, "Content") ?? string.Empty;
            var imported = metadata is not null
                || value["RemoteLibraryImported"]?.GetValue<bool?>() == true
                || content.StartsWith(ImportedReviewPrefix, StringComparison.Ordinal);
            if (imported && !includeImported)
            {
                continue;
            }

            var source = metadata?.SourceLabel ?? JsonString(value, "RemoteLibrarySource");
            var originalContent = metadata?.OriginalContent ?? JsonString(value, "RemoteLibraryOriginalContent");
            var originalUserName = metadata?.OriginalUserName ?? JsonString(value, "RemoteLibraryOriginalUserName");
            var avatarUrl = metadata is null ? null : ReviewAvatarUrl(metadata.SourceServerId, metadata.OriginalUserId);
            reviews.Add(new RemoteReview
            {
                UserId = JsonString(value, "UserId"),
                UserName = originalUserName ?? JsonString(value, "UserId"),
                TmdbId = storedId,
                MediaType = storedType,
                Content = imported && originalContent is not null ? originalContent : content,
                Rating = JsonDouble(value, "Rating"),
                CreatedAt = JsonString(value, "CreatedAt"),
                UpdatedAt = JsonString(value, "UpdatedAt"),
                Source = source,
                AvatarUrl = avatarUrl,
                Imported = imported
            });
        }

        return reviews;
    }

    private ImportedReviewsRegistry ReadImportedRegistry()
    {
        try
        {
            if (!File.Exists(ImportedReviewsRegistryPath))
            {
                return new ImportedReviewsRegistry();
            }

            return JsonSerializer.Deserialize<ImportedReviewsRegistry>(
                File.ReadAllText(ImportedReviewsRegistryPath),
                JsonOptions) ?? new ImportedReviewsRegistry();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "Remote review import registry could not be read; embedded import tags will still prevent re-export");
            return new ImportedReviewsRegistry();
        }
    }

    private void WriteImportedRegistry(ImportedReviewsRegistry registry)
        => WriteJsonAtomic(
            ImportedReviewsRegistryPath,
            JsonSerializer.SerializeToNode(registry, JsonOptions) ?? new JsonObject());

    private ReviewDiscoveryState ReadReviewDiscoveryState()
    {
        try
        {
            if (File.Exists(ReviewDiscoveryStatePath))
            {
                return JsonSerializer.Deserialize<ReviewDiscoveryState>(
                    File.ReadAllText(ReviewDiscoveryStatePath),
                    JsonOptions) ?? new ReviewDiscoveryState();
            }

            var state = new ReviewDiscoveryState();
            if (File.Exists(ImportedReviewsRegistryPath))
            {
                var lastImport = File.GetLastWriteTimeUtc(ImportedReviewsRegistryPath);
                foreach (var sourceId in ReadImportedRegistry().Entries.Values.Select(item => item.SourceServerId).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    state.LastFullDiscoveryUtc[sourceId] = lastImport;
                }
            }

            return state;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "Remote review discovery state could not be read");
            return new ReviewDiscoveryState();
        }
    }

    private void WriteReviewDiscoveryState(ReviewDiscoveryState state)
        => WriteJsonAtomic(
            ReviewDiscoveryStatePath,
            JsonSerializer.SerializeToNode(state, JsonOptions) ?? new JsonObject());

    private static JsonObject ReadReviewsDocument(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject { ["Reviews"] = new JsonObject() };
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"Refusing to overwrite empty review store {path}.");
        }

        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException($"Refusing to overwrite invalid review store {path}.");
    }

    private static void WriteJsonAtomic(string path, JsonNode document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".remote-library.tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsValidReview(RemoteReview review)
        => review.MediaType is "movie" or "tv"
            && !string.IsNullOrWhiteSpace(review.TmdbId)
            && Regex.IsMatch(review.TmdbId, @"^\d+(:s\d+(:e\d+)?)?$", RegexOptions.CultureInvariant)
            && (review.Rating is null || review.Rating is >= 1 and <= 5);

    private static string SyntheticReviewUserId(string serverId, string originalUserId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"remote-library-review:{serverId}:{originalUserId}")))[..32].ToLowerInvariant();

    private static string? JsonString(JsonObject value, string property)
        => value[property] is JsonValue node && node.TryGetValue<string>(out var result) ? result : null;

    private static double? JsonDouble(JsonObject value, string property)
        => value[property] is JsonValue node && node.TryGetValue<double>(out var result) ? result : null;

    private sealed class ReviewPage
    {
        public List<RemoteReview> Reviews { get; set; } = [];

        public int Total { get; set; }
    }

    private sealed class ImportedReviewsRegistry
    {
        public Dictionary<string, ImportedReviewMetadata> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ReviewDiscoveryState
    {
        public Dictionary<string, DateTimeOffset> LastFullDiscoveryUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ImportedReviewMetadata(
        string SourceServerId,
        string SourceLabel,
        string OriginalUserId,
        string OriginalUserName,
        string OriginalContent);
}

public sealed record ReviewSyncResult(int Imported, int Removed, int OnlineServers, int SkippedServers);
