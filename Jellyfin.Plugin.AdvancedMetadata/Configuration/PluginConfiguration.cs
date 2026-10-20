using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AdvancedMetadata.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<Mapping> Mappings { get; set; } = new();

    public string Delimiters { get; set; } = "/|;:";

    /// <summary>Libraries to scan. Empty means all music libraries.</summary>
    public List<string> Libraries { get; set; } = new();

    public string StorePath { get; set; } = string.Empty;

    public string LastScanSummary { get; set; } = "Never scanned.";
}

public class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceTag { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Hierarchy { get; set; } = "songs_albums";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
