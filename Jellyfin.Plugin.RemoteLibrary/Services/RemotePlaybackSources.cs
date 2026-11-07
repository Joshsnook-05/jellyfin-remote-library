using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.RemoteLibrary.Services;

public sealed class RemoteMediaSourceProvider(RemoteLibraryService service) : IMediaSourceProvider
{
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        => await service.GetRemotePlaybackSources(item, cancellationToken).ConfigureAwait(false);

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

public sealed partial class RemoteLibraryService
{
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Expires, List<MediaSourceInfo> Sources)> _playbackSources = new();

    public async Task<List<MediaSourceInfo>> GetRemotePlaybackSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Movie && item is not Episode) return [];
        if (_playbackSources.TryGetValue(item.Id, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Sources.Select(CloneSource).ToList();
        var results = await Task.WhenAll(GetServers(GetConfiguration())
            .Where(server => server.Enabled && !string.IsNullOrWhiteSpace(server.AccessToken)
                && !string.IsNullOrWhiteSpace(server.RemoteUserId)
                && Uri.TryCreate(server.ServerUrl, UriKind.Absolute, out var url)
                && url.Scheme is "http" or "https")
            .Select(async server =>
            {
                var sources = new List<MediaSourceInfo>();
                try
                {
                    var client = CreateClient(server.ServerUrl, server.AccessToken);
                    var anchor = item is Episode episode ? episode.Series : item;
                    if (anchor is null || item is Episode missingNumbers && (!missingNumbers.ParentIndexNumber.HasValue || !missingNumbers.IndexNumber.HasValue)) return sources;
                    var ids = anchor.ProviderIds.Where(p => p.Key is "Tmdb" or "Imdb" or "Tvdb")
                        .Select(p => p.Key + "." + p.Value).ToArray();
                    // No title-only matches: a remake or similarly named show must never become an alternate source.
                    if (ids.Length == 0) return sources;
                    var managed = await GetRemoteManagedMediaAsync(client, cancellationToken).ConfigureAwait(false);
                    var prefix = $"Users/{Uri.EscapeDataString(server.RemoteUserId)}/Items";
                    var matches = await GetAsync<QueryResult>(client, prefix + "?Recursive=true&IncludeItemTypes="
                        + (item is Episode ? "Series" : "Movie") + "&AnyProviderIdEquals="
                        + Uri.EscapeDataString(string.Join(',', ids)) + "&Fields=MediaSources,Path,ProviderIds,Tags&Limit=100", cancellationToken).ConfigureAwait(false);
                    var candidates = matches.Items.Where(match => ids.Any(id => match.ProviderIds.Any(p =>
                        string.Equals(p.Key + "." + p.Value, id, StringComparison.OrdinalIgnoreCase)))).ToList();
                    if (item is Episode ep)
                    {
                        var episodes = new List<RemoteItem>();
                        foreach (var series in candidates)
                        {
                            var found = await GetAsync<QueryResult>(client, prefix + "?ParentId=" + Uri.EscapeDataString(series.Id)
                                + "&Recursive=true&IncludeItemTypes=Episode&Fields=MediaSources,Path,ProviderIds,Tags"
                                + "&ParentIndexNumber=" + ep.ParentIndexNumber + "&IndexNumber=" + ep.IndexNumber, cancellationToken).ConfigureAwait(false);
                            episodes.AddRange(found.Items.Where(e => e.ParentIndexNumber == ep.ParentIndexNumber && e.IndexNumber == ep.IndexNumber));
                        }
                        candidates = episodes;
                    }
                    foreach (var candidate in candidates.Where(c => !c.IsVirtualItem && !IsManagedRemoteItem(c, managed)))
                    {
                        var details = await GetAsync<PlaybackItem>(client, $"Users/{Uri.EscapeDataString(server.RemoteUserId)}/Items/{Uri.EscapeDataString(candidate.Id)}", cancellationToken).ConfigureAwait(false);
                        foreach (var original in details.MediaSources.Where(s => !string.IsNullOrWhiteSpace(s.Id) && !s.IsRemote
                            && !string.IsNullOrWhiteSpace(s.Path) && !s.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)))
                        {
                            var source = CloneSource(original);
                            source.Id = "remote_" + server.Id + "_" + candidate.Id + "_" + original.Id;
                            source.Name = SourceLabel(server) + " · " + (original.Name ?? original.Container ?? "Video");
                            source.Path = $"http://127.0.0.1:8096/RemoteLibrary/Stream/{Uri.EscapeDataString(server.Id)}/{Uri.EscapeDataString(candidate.Id)}?secret={Uri.EscapeDataString(GetConfiguration().StreamSecret)}&mediaSourceId={Uri.EscapeDataString(original.Id)}";
                            source.EncoderPath = null;
                            source.EncoderProtocol = null;
                            source.MediaAttachments = [];
                            source.Protocol = MediaProtocol.Http;
                            source.IsRemote = true;
                            source.RequiredHttpHeaders = new Dictionary<string, string>();
                            source.SupportsDirectPlay = false;
                            source.SupportsDirectStream = true;
                            source.SupportsTranscoding = true;
                            source.RequiresOpening = false;
                            source.RequiresClosing = false;
                            source.OpenToken = null;
                            source.LiveStreamId = null;
                            source.TranscodingUrl = null;
                            // External subtitle paths belong to the peer, not this server.
                            source.MediaStreams = source.MediaStreams.Where(s => !s.IsExternal).ToArray();
                            if (!source.MediaStreams.Any(stream => stream.Index == source.DefaultSubtitleStreamIndex))
                                source.DefaultSubtitleStreamIndex = null;
                            sources.Add(source);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    _logger.LogDebug(exception, "Could not discover playback alternatives on {Server}", SourceLabel(server));
                }
                return sources;
            })).ConfigureAwait(false);
        var flattened = results.SelectMany(s => s).ToList();
        if (_playbackSources.Count > 1000) _playbackSources.Clear();
        _playbackSources[item.Id] = (DateTimeOffset.UtcNow.AddSeconds(30), flattened);
        return flattened.Select(CloneSource).ToList();
    }

    private static MediaSourceInfo CloneSource(MediaSourceInfo source)
        => System.Text.Json.JsonSerializer.Deserialize<MediaSourceInfo>(System.Text.Json.JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;

    private sealed class PlaybackItem
    {
        public List<MediaSourceInfo> MediaSources { get; set; } = [];
    }
}
