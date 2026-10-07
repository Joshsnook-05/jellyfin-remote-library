using Jellyfin.Plugin.RemoteLibrary.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RemoteLibrary;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Remote Library";

    public override Guid Id => Guid.Parse("5bfb2af8-3575-4d2f-9041-621588e13bfa");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "RemoteLibrary",
            EmbeddedResourcePath = GetType().Namespace + ".Web.config.html"
        };
    }
}
