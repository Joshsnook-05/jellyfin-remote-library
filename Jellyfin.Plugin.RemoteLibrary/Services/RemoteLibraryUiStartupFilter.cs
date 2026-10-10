using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteLibrary.Services;

/// <summary>Injects the bundled Enhanced compatibility bridge into Jellyfin Web.</summary>
public sealed class RemoteLibraryUiStartupFilter : IStartupFilter
{
    private readonly ILogger<RemoteLibraryUiStartupFilter> _logger;
    private int _loggedIndexInjection;
    private int _loggedEnhancedBundle;

    public RemoteLibraryUiStartupFilter(ILogger<RemoteLibraryUiStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var isIndex = IsIndexRequest(context.Request.Path.Value);
        var isEnhancedBundle = IsEnhancedBundleRequest(context.Request.Path.Value);
        if (!HttpMethods.IsGet(context.Request.Method) || (!isIndex && !isEnhancedBundle))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var isExpectedResponse = context.Response.StatusCode == StatusCodes.Status200OK
            && (isIndex
                ? context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false
                : context.Response.ContentType?.Contains("javascript", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isExpectedResponse)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string content;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            content = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (isIndex)
        {
            var close = content.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (close >= 0)
            {
                var scripts = string.Empty;
                if (content.IndexOf("/RemoteLibrary/Integration.js", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    scripts += "<script plugin=\"Remote Library\" defer src=\"../RemoteLibrary/Integration.js?v=1.0.12\"></script>\n";
                }

                if (content.IndexOf("/RemoteLibrary/Recommendations.js", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    scripts += "<script plugin=\"Remote Library\" defer src=\"../RemoteLibrary/Recommendations.js?v=1.0.12\"></script>\n";
                }

                if (scripts.Length > 0)
                {
                    content = content[..close] + scripts + content[close..];
                    if (Interlocked.Exchange(ref _loggedIndexInjection, 1) == 0)
                    {
                        _logger.LogInformation("Remote Library: injected its bundled browser integrations.");
                    }
                }
            }
        }
        else if (content.IndexOf("__remoteLibraryBridgeInstalled", StringComparison.Ordinal) < 0)
        {
            using var stream = typeof(RemoteLibraryUiStartupFilter).Assembly
                .GetManifestResourceStream("Jellyfin.Plugin.RemoteLibrary.Web.integration.js");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                content += "\n" + await reader.ReadToEndAsync().ConfigureAwait(false) + "\n";
                if (Interlocked.Exchange(ref _loggedEnhancedBundle, 1) == 0)
                {
                    _logger.LogInformation("Remote Library: appended the compatibility bridge to the Jellyfin Enhanced client bundle.");
                }
            }
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        context.Response.ContentType = isIndex ? "text/html; charset=utf-8" : "text/javascript; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(string? path)
        => !string.IsNullOrEmpty(path)
            && (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase));

    private static bool IsEnhancedBundleRequest(string? path)
        => !string.IsNullOrEmpty(path)
            && path.EndsWith("/JellyfinEnhanced/script", StringComparison.OrdinalIgnoreCase);
}
