using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AdvancedMetadata.Configuration;

/// <summary>Persisted plugin configuration. Jellyfin stores this as XML for us.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>User-defined mappings.</summary>
    public List<Mapping> Mappings { get; set; } = new();

    /// <summary>Characters that split a multi-value tag.</summary>
    public string Delimiters { get; set; } = "/|;:";
}

/// <summary>One source-tag to display-name mapping.</summary>
public class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tag exactly as written in the file, e.g. LABEL.</summary>
    public string SourceTag { get; set; } = string.Empty;

    /// <summary>Friendly name shown to users, e.g. Record Label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>songs | songs_albums | albums | songs_albums_artists</summary>
    public string Hierarchy { get; set; } = "songs_albums";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
