using System;
using Jellyfin.Plugin.JFLint.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JFLint;

/// <summary>
/// Entry point of the JFLint plugin. It contributes nothing but its API controller;
/// the class exists because Jellyfin discovers plugin assemblies through it.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "JFLint";

    /// <inheritdoc />
    public override string Description =>
        "Adds library-lint query endpoints that the Jellyfin API cannot express, " +
        "starting with episodes whose season could not be determined.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7febad9a-ee08-4eda-843a-a5522060a096");
}
