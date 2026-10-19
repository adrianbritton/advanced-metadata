using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AdvancedMetadata.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AdvancedMetadata;

/// <summary>
/// Advanced Metadata — builds a searchable index of custom audio metadata.
/// Jellyfin itself is treated as read-only.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Fixed, unique plugin id. Never change this.</summary>
    public static readonly Guid PluginId = Guid.Parse("3f8b2c41-9a7d-4e5b-8c1a-6d2e4f7a9b03");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Advanced Metadata";

    public override Guid Id => PluginId;

    public override string Description =>
        "Builds a searchable index of custom audio metadata for Advanced Search. Jellyfin is read-only.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Web.config.html"
            }
        };
    }
}
