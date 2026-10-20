using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AdvancedMetadata.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AdvancedMetadata.Scanning;

public class ScanTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly MetadataStore _store;

    public ScanTask(ILibraryManager library, MetadataStore store)
    {
        _library = library;
        _store = store;
    }

    public string Name => "Advanced Metadata Scan";
    public string Key => "AdvancedMetadataScan";
    public string Description => "Reads mapped audio tags into the local index.";
    public string Category => "Advanced Metadata";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Run(progress, ct), ct);

    private void Run(IProgress<double> progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) { return; }

        var cfg = plugin.Configuration;
        cfg.StorePath = _store.DatabasePath;

        if (cfg.Mappings.Count == 0)
        {
            cfg.LastScanSummary = "No mappings defined.";
            plugin.UpdateConfiguration(cfg);
            return;
        }

        var delimiters = (cfg.Delimiters ?? string.Empty).ToCharArray();
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true
        };

        var items = _library.GetItemList(query).OfType<Audio>().ToList();
        var total = items.Count;
        var done = 0;
        var updated = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(total == 0 ? 0 : (double)done / total * 100);

            var path = item.Path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { continue; }

            var fi = new FileInfo(path);
            var id = item.Id.ToString("N");
            if (_store.IsUnchanged(id, fi.LastWriteTimeUtc.Ticks, fi.Length)) { continue; }

            var itemRow = _store.UpsertItem(id, "Audio", path, fi.LastWriteTimeUtc.Ticks, fi.Length);

            foreach (var m in cfg.Mappings)
            {
                var values = TagReader.ReadValues(path, m.SourceTag, delimiters);
                _store.ReplaceFieldValues(itemRow, m.DisplayName, values);

                if (m.Hierarchy.Contains("albums"))
                {
                    var albumId = GetAlbumId(item);
                    if (albumId != Guid.Empty)
                    {
                        var albumRow = _store.UpsertItem(albumId.ToString("N"), "MusicAlbum", null, 0, 0);
                        _store.ReplaceFieldValues(albumRow, m.DisplayName, values);
                    }
                }
            }
            updated++;
        }

        cfg.StorePath = _store.DatabasePath;
        cfg.LastScanSummary = string.Format(
            "{0} of {1} tracks processed, {2} updated. {3:0.0} MB store.",
            done, total, updated, _store.SizeBytes / 1024.0 / 1024.0);
        plugin.UpdateConfiguration(cfg);
    }

    /// <summary>Album id, tolerating either property name across Jellyfin versions.</summary>
    private static Guid GetAlbumId(Audio item)
    {
        var t = item.GetType();
        var prop = t.GetProperty("AlbumEntityId") ?? t.GetProperty("AlbumId");
        if (prop != null && prop.GetValue(item) is Guid g && g != Guid.Empty) { return g; }
        return item.ParentId;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
