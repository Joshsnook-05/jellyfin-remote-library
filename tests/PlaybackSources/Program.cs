using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using System.IO;
using Jellyfin.Plugin.RemoteLibrary;
using Jellyfin.Plugin.RemoteLibrary.Services;
using Jellyfin.Plugin.RemoteLibrary.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;

var paths = DispatchProxy.Create<IApplicationPaths, Stub>();
var serializer = DispatchProxy.Create<IXmlSerializer, Stub>();
var plugin = new Plugin(paths, serializer);
plugin.Configuration.Servers = [new RemoteServerConfiguration { Id = "peer", SourceLabel = "Friend", ServerUrl = "https://peer.test", AccessToken = "private-token", RemoteUserId = "viewer" }];
var factory = new FakeFactory();
var service = new RemoteLibraryService(factory, NullLogger<RemoteLibraryService>.Instance, paths, null!);
var movie = new Movie { Id = Guid.NewGuid(), Name = "Example", ProviderIds = new Dictionary<string,string> { ["Tmdb"] = "42" } };
var sources = await service.GetRemotePlaybackSources(movie, CancellationToken.None);
Check(sources.Count == 1, "Only matching real media is exposed");
Check(sources[0].Name.StartsWith("Friend"), "Remote server name");
Check(sources[0].Path.Contains("mediaSourceId=version1"), "Exact media version proxy");
Check(!sources[0].Path.Contains("private-token"), "Remote token is not exposed");
Check(!sources[0].SupportsDirectPlay && sources[0].SupportsDirectStream, "Local proxy is required");
Check(sources[0].RequiredHttpHeaders.Count == 0, "Remote headers are removed");
Check(sources[0].MediaStreams.Count == 2, "External subtitle paths are removed");
Check(sources[0].MediaStreams[1].Language == "eng", "Audio metadata survives");
sources[0].Name = "mutated";
Check((await service.GetRemotePlaybackSources(movie, CancellationToken.None))[0].Name.StartsWith("Friend"), "Cached results are cloned");
var noIds = new Movie { Id = Guid.NewGuid(), Name = "Example" };
Check((await service.GetRemotePlaybackSources(noIds, CancellationToken.None)).Count == 0, "No unsafe title matching");
var series = new Series { Id = Guid.NewGuid(), Name = "Example series", ProviderIds = new Dictionary<string,string> { ["Tmdb"] = "42" } };
Stub.Series = series;
typeof(BaseItem).GetProperty("LibraryManager")!.SetValue(null, DispatchProxy.Create<ILibraryManager, Stub>());
var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 2, IndexNumber = 3 };
var episodeSources = await service.GetRemotePlaybackSources(episode, CancellationToken.None);
Check(episodeSources.Count == 1 && episodeSources[0].Id.Contains("episode"), "Episodes match series identity and exact season/episode numbers");
var context = new DefaultHttpContext();
context.Request.Method = "GET";
context.Request.QueryString = new QueryString("?mediaSourceId=version1");
context.Request.Headers.Range = "bytes=10-20";
context.Response.Body = new MemoryStream();
await service.ProxyAsync("peer", "movie", false, context.Request, context.Response, CancellationToken.None);
Check(factory.LastUri!.Query.Contains("mediaSourceId=version1"), "Proxy forwards chosen version");
Check(factory.LastRange == "bytes=10-20", "Proxy preserves seeking range");
Check(context.Response.StatusCode == 206 && context.Response.Body.Length == 5, "Proxy preserves partial-content response");
factory.Offline = true;
movie.Id = Guid.NewGuid();
Check((await service.GetRemotePlaybackSources(movie, CancellationToken.None)).Count == 0, "Offline remote does not break local playback");
Console.WriteLine("Playback discovery, proxy metadata, cache isolation, loop rejection and offline checks passed.");
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

public class Stub : DispatchProxy
{
    public static Series? Series;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetItemById") return Series;
        if (method.ReturnType == typeof(string)) return "/tmp/remote-library-tests";
        if (method.ReturnType == typeof(object)) return new PluginConfiguration();
        if (method.ReturnType.IsValueType) return Activator.CreateInstance(method.ReturnType);
        return null;
    }
}
public class FakeFactory : IHttpClientFactory
{
    public bool Offline;
    public Uri? LastUri;
    public string? LastRange;
    public HttpClient CreateClient(string name) => new(new Handler(this));
    private class Handler(FakeFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (owner.Offline) throw new HttpRequestException("Offline");
            owner.LastUri = request.RequestUri;
            owner.LastRange = request.Headers.Range?.ToString();
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("Videos/movie/stream")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("video") });
            string json;
            if (path.EndsWith("ManagedMedia")) json = "{\"Roots\":[\"/remote-library\"],\"Paths\":[]}";
            else if (request.RequestUri.Query.Contains("IncludeItemTypes=Series")) json = "{\"Items\":[{\"Id\":\"series\",\"ProviderIds\":{\"Tmdb\":\"42\"}}]}";
            else if (request.RequestUri.Query.Contains("IncludeItemTypes=Episode")) json = """
                {"Items":[{"Id":"episode","ParentIndexNumber":2,"IndexNumber":3,"Path":"/media/episode.mkv"},{"Id":"wrongEpisode","ParentIndexNumber":2,"IndexNumber":4,"Path":"/media/wrong.mkv"}]}
                """;
            else if (path.EndsWith("Items")) json = """
                {"Items":[
                  {"Id":"movie","ProviderIds":{"Tmdb":"42"},"Path":"/media/movie.mkv"},
                  {"Id":"wrong","ProviderIds":{"Tmdb":"99"},"Path":"/media/wrong.mkv"},
                  {"Id":"loop","ProviderIds":{"Tmdb":"42"},"Path":"/remote-library/loop.strm"},
                  {"Id":"virtual","ProviderIds":{"Tmdb":"42"},"IsVirtualItem":true}
                ]}
                """;
            else if ((path.EndsWith("Items/movie") || path.EndsWith("Items/episode"))) json = """
                {"MediaSources":[{"Id":"version1","Name":"1080p","Path":"/media/movie.mkv","Container":"mkv","RequiredHttpHeaders":{"Authorization":"secret"},"MediaStreams":[{"Type":"Video","Width":1920,"Height":1080},{"Type":"Audio","Language":"eng"},{"Type":"Subtitle","IsExternal":true,"Path":"/private/sub.srt"}]}]}
                """;
            else throw new Exception("Unexpected remote request " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
