using System.Globalization;
using Jellyfin.Plugin.ImdbSmartLookup.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ImdbSmartLookup;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "IMDb Smart Lookup";

    public override Guid Id => Guid.Parse("a8f4c2e1-7b3d-4e9a-9c1f-6d2e8a4b5c70");

    public override string Description =>
        "Automatically resolves IMDb IDs during library scans with configurable year tolerance and passes them to downstream metadata providers like TheMovieDb.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}
