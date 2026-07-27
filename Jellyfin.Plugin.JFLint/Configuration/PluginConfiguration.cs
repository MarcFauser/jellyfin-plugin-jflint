using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JFLint.Configuration;

/// <summary>
/// Configuration for the JFLint plugin. Intentionally empty: the plugin exposes
/// read-only query endpoints and has nothing to configure.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
}
