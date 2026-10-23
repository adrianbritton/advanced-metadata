using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AdvancedMetadata.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<Mapping> Mappings { get; set; } = new();

    public string Delimiters { get; set; } = "/|;:";

    public List<string> Libraries { get; set; } = new();

    public string StorePath { get; set; } = string.Empty;

    public bool FullRescanRequested { get; set; }

    public string LastScanStatus { get; set; } = "Never scanned.";
    public string LastScanType { get; set; } = string.Empty;
    public string LastScanSummary { get; set; } = string.Empty;
    public DateTime? LastScanStartedUtc { get; set; }
    public DateTime? LastScanFinishedUtc { get; set; }
    public int LastScanTotal { get; set; }
    public int LastScanItems { get; set; }
    public int LastScanSkipped { get; set; }
    public int LastScanValues { get; set; }
    public int LastScanErrors { get; set; }
    public string LastScanError { get; set; } = string.Empty;
}

public class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceTag { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Hierarchy { get; set; } = "songs_albums";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
