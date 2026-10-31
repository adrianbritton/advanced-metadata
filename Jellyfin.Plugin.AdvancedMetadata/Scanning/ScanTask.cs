using System;
using System.Collections.Generic;
using System.Globalization;
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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdvancedMetadata.Scanning;

public class ScanTask : IScheduledTask
{
    private const int CommitEvery = 500;

    private readonly ILibraryManager _library;
    private readonly MetadataStore _store;
    private readonly ILogger<ScanTask> _logger;

    public ScanTask(ILibraryManager library, MetadataStore store, ILogger<ScanTask> logger)
    {
        _library = library;
        _store = store;
        _logger = logger;
    }

    public string Name => "Advanced Metadata Scan";

    public string Key => "AdvancedMetadataScan";

    public string Description => "Reads the mapped tags from your audio files into the local Advanced Metadata index.";

    public string Category => "Advanced Metadata";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Run(progress, ct), ct);

    private void Run(IProgress<double> progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance!;
        var cfg = plugin.Configuration;

        var full = cfg.FullRescanRequested;
        cfg.FullRescanRequested = false;

        // Optional single-library scope
        var scopeId = Guid.Empty;
        var scopeName = string.Empty;
        // Only a manually started scan carries a scope. The scheduled run is always global.
        var requestedScope = cfg.PendingScanLibraryId;
        cfg.PendingScanLibraryId = string.Empty;

        if (Guid.TryParse(requestedScope, out var wantedScope) && wantedScope != Guid.Empty)
        {
            var scopeItem = _library.GetItemById(wantedScope);
            if (scopeItem is null)
            {
                _logger.LogWarning(
                    "Advanced Metadata: scan library {Id} no longer exists, scanning every library instead",
                    wantedScope);
            }
            else
            {
                scopeId = wantedScope;
                scopeName = scopeItem.Name;
            }
        }

        var scoped = scopeId != Guid.Empty;

        cfg.LastScanStartedUtc = DateTime.UtcNow;
        cfg.LastScanFinishedUtc = null;
        cfg.LastScanError = string.Empty;
        cfg.LastScanItems = 0;
        cfg.LastScanSkipped = 0;
        cfg.LastScanValues = 0;
        cfg.LastScanErrors = 0;
        cfg.LastScanBackfill = 0;
        cfg.LastScanTotal = 0;
        cfg.LastScanType = (full ? "Full Scan" : "Incremental Scan")
            + (scoped ? " — " + scopeName : string.Empty);
        cfg.LastScanStatus = "Running";
        cfg.LastScanStage = "Starting";
        cfg.LastScanSummary = string.Empty;
        plugin.UpdateConfiguration(cfg);

        var mappings = cfg.Mappings.Where(m => !string.IsNullOrWhiteSpace(m.SourceTag)).ToList();
        var knownIds = cfg.KnownMappingIds.ToHashSet();

        var backfill = mappings
            .Where(m => !knownIds.Contains(m.Id))
            .Select(m => m.SourceTag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (full) { backfill.Clear(); }
        cfg.LastScanBackfill = backfill.Count;
        plugin.UpdateConfiguration(cfg);

        void Stage(string s)
        {
            cfg.LastScanStage = s;
            plugin.UpdateConfiguration(cfg);
            _logger.LogInformation("Advanced Metadata: stage -> {Stage}", s);
        }

        try
        {
            var delims = string.IsNullOrWhiteSpace(cfg.Delimiters)
                ? new[] { '/', '|', ';', ':' }
                : cfg.Delimiters.ToCharArray();

            Stage("Pruning and syncing mappings");
            _store.SyncMappings(mappings);

            // A scoped full rescan clears per item in the loop, so other libraries keep their values.
            if (full && !scoped)
            {
                foreach (var m in mappings) { _store.ClearField(m.SourceTag); }
            }

            Stage("Reading library item list");
            List<BaseItem> items;

            if (scoped)
            {
                var folder = _library.GetItemById(scopeId) as Folder;
                items = (folder?.GetRecursiveChildren() ?? Enumerable.Empty<BaseItem>())
                    .Where(i => !i.IsFolder && !i.IsVirtualItem && !string.IsNullOrEmpty(i.Path))
                    .ToList();
            }
            else
            {
                items = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    Recursive = true,
                    IsVirtualItem = false,
                    EnableTotalRecordCount = false
                }).ToList();
            }

            if (items.Count == 0)
            {
                cfg.LastScanTotal = 0;
                cfg.LastScanStage = "Nothing to scan";
                cfg.LastScanStatus = "Complete";
                cfg.LastScanFinishedUtc = DateTime.UtcNow;
                cfg.LastScanSummary = scoped
                    ? "No items found in " + scopeName + "."
                    : "No audio items found.";
                plugin.UpdateConfiguration(cfg);
                _logger.LogWarning("Advanced Metadata: nothing to scan ({Summary})", cfg.LastScanSummary);
                return;
            }

            cfg.LastScanTotal = items.Count;
            plugin.UpdateConfiguration(cfg);
            Stage("Scanning " + items.Count + " tracks");

            _logger.LogInformation(
                "Advanced Metadata: {Type} over {Count} tracks (backfill: {Backfill} mapping(s))",
                cfg.LastScanType, items.Count, backfill.Count);

            _store.BeginBatch();

            var done = 0;
            var skipped = 0;
            var values = 0;
            var errors = 0;

            try
            {
                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;

                    if (items.Count > 0) { progress.Report(done * 100.0 / items.Count); }

                    if (done % CommitEvery == 0)
                    {
                        _store.CommitBatch();
                        cfg.LastScanItems = done;
                        cfg.LastScanSkipped = skipped;
                        plugin.UpdateConfiguration(cfg);
                    }

                    if (done % 1000 == 0)
                    {
                        _logger.LogInformation(
                            "Advanced Metadata: heartbeat {Done}/{Total} (skipped {Skipped}, indexed {Values})",
                            done, items.Count, skipped, values);
                    }

                    var path = item.Path;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) { continue; }

                    var key = item.Id.ToString("N");
                    long mtime;
                    long size;
                    try
                    {
                        var fi = new FileInfo(path);
                        mtime = fi.LastWriteTimeUtc.Ticks;
                        size = fi.Length;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        _logger.LogWarning(ex, "Advanced Metadata: could not stat {Path}", path);
                        continue;
                    }

                    var unchanged = !full && _store.IsUnchanged(key, mtime, size);
                    if (unchanged && backfill.Count == 0 && !_store.ItemMissingMeta(key))
                    {
                        skipped++;
                        continue;
                    }

                    var albumId = ResolveAlbumId(item);
                    var albumItem = albumId.HasValue ? _library.GetItemById(albumId.Value) : null;

                    var artist = ArtistNames(item)
                        ?? GetStringList(item, "AlbumArtists")
                        ?? ArtistNames(albumItem);
                    var albumArtist = GetStringList(item, "AlbumArtists") ?? artist;
                    var album = GetStringProperty(item, "Album") ?? albumItem?.Name;

                    if (string.IsNullOrWhiteSpace(artist))
                    {
                        artist = ReadTag(path, "ARTIST", delims).FirstOrDefault();
                    }

                    if (string.IsNullOrWhiteSpace(albumArtist))
                    {
                        albumArtist = ReadTag(path, "ALBUMARTIST", delims).FirstOrDefault() ?? artist;
                    }

                    if (string.IsNullOrWhiteSpace(album))
                    {
                        album = ReadTag(path, "ALBUM", delims).FirstOrDefault();
                    }

                    long itemId;
                    try
                    {
                        itemId = _store.UpsertItem(key, item.GetType().Name,
                            item.Name ?? Path.GetFileNameWithoutExtension(path), artist, album, path, mtime, size);
                        if (albumId.HasValue) { _store.SetItemParent(key, albumId.Value.ToString("N")); }
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        _logger.LogWarning(ex, "Advanced Metadata: could not index {Path}", path);
                        continue;
                    }

                    foreach (var m in mappings)
                    {
                        if (unchanged && !backfill.Contains(m.SourceTag)) { continue; }

                        var extra = (m.Delimiters ?? string.Empty).Trim();
                        var mode = (m.DelimiterMode ?? string.Empty).Trim().ToLowerInvariant();
                        if (mode.Length == 0) { mode = "remove"; }
                        char[] mDelims;
                        if (extra.Length == 0) { mDelims = delims; }
                        else if (mode == "only") { mDelims = extra.ToCharArray(); }
                        else if (mode == "remove") { mDelims = delims.Where(c => extra.IndexOf(c) < 0).ToArray(); }
                        else { mDelims = delims.Concat(extra.ToCharArray()).Distinct().ToArray(); }
                        if (mDelims.Length == 0) { mDelims = new[] { '/', '|', ';', ':' }; }
                        var vals = ReadTag(path, m.SourceTag, mDelims);

                        // On a full scan an empty result still has to overwrite, or removed tags linger.
                        if (vals.Count == 0 && !full) { continue; }

                        try
                        {
                            _store.ReplaceFieldValues(itemId, m.SourceTag, vals);
                            _store.ApplyOverrides(key, m.SourceTag);
                            values += vals.Count;

                            if (vals.Count > 0 && albumId.HasValue
                                && m.Hierarchy.Contains("albums", StringComparison.OrdinalIgnoreCase))
                            {
                                var aid = albumId.Value.ToString("N");
                                var albumName = album ?? item.Name ?? "Unknown album";
                                var albumBy = albumArtist ?? artist ?? albumName;
                                var tid = _store.UpsertItem(aid, "MusicAlbum", albumName, albumBy, albumName, null, 0, 0);
                                _store.AddValues(tid, m.SourceTag, vals);
                                _store.ApplyOverrides(aid, m.SourceTag);
                            }

                            if (vals.Count > 0 && m.Hierarchy.Contains("artists", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var a in GetArtists(item))
                                {
                                    var aid = a.Id.ToString("N");
                                    var an = string.IsNullOrWhiteSpace(a.Name) ? (artist ?? "Unknown artist") : a.Name;
                                    var tid = _store.UpsertItem(aid, "MusicArtist", an, an, null, null, 0, 0);
                                    _store.AddValues(tid, m.SourceTag, vals);
                                    _store.ApplyOverrides(aid, m.SourceTag);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            _logger.LogWarning(ex, "Advanced Metadata: mapping {Tag} failed on {Path}", m.SourceTag, path);
                        }
                    }
                }
            }
           finally
            {
                cfg.LastScanValues = values;
                cfg.LastScanSkipped = skipped;
                cfg.LastScanErrors = errors;
                cfg.LastScanItems = done;
                plugin.UpdateConfiguration(cfg);
                _store.EndBatch();
            }

            progress.Report(100);
            cfg.LastScanStage = "Complete";
            cfg.KnownMappingIds = mappings.Select(m => m.Id).ToList();
            cfg.LastScanItems = done;
            cfg.LastScanSkipped = skipped;
            cfg.LastScanValues = values;
            cfg.LastScanErrors = errors;
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            cfg.LastScanStatus = "Complete";
            cfg.LastScanSummary = $"{done} read, {skipped} unchanged, {values} indexed, {errors} errors.";
            plugin.UpdateConfiguration(cfg);

            _logger.LogInformation("Advanced Metadata: scan complete. {Summary}", cfg.LastScanSummary);
        }
        catch (OperationCanceledException)
        {
            cfg.LastScanStatus = "Cancelled";
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            plugin.UpdateConfiguration(cfg);
            throw;
        }
        catch (Exception ex)
        {
            var detail = ex.ToString();
            cfg.LastScanStatus = "Failed To Execute";
            cfg.LastScanError = detail.Length > 700 ? detail.Substring(0, 700) : detail;
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            plugin.UpdateConfiguration(cfg);
            _logger.LogError(ex, "Advanced Metadata scan failed");
            throw;
        }
    }

    private static Guid? ResolveAlbumId(BaseItem item)
    {
        var byProperty = GetGuidProperty(item, "AlbumEntityId") ?? GetGuidProperty(item, "AlbumId");
        if (byProperty.HasValue) { return byProperty; }
        if (item.ParentId != Guid.Empty) { return item.ParentId; }
        return null;
    }

    private static Guid? GetGuidProperty(BaseItem item, string name)
    {
        var p = item.GetType().GetProperty(name);
        if (p?.GetValue(item) is Guid g && g != Guid.Empty) { return g; }
        return null;
    }

    private static string? GetStringProperty(BaseItem item, string name)
    {
        var p = item.GetType().GetProperty(name);
        return p?.GetValue(item) as string;
    }

    private static string? GetStringList(BaseItem item, string name)
    {
        var p = item.GetType().GetProperty(name);
        if (p?.GetValue(item) is string[] arr && arr.Length > 0)
        {
            return string.Join(" / ", arr.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return null;
    }

    private static string? ArtistNames(BaseItem? item)
    {
        if (item is null) { return null; }

        var direct = GetStringList(item, "Artists");
        if (!string.IsNullOrWhiteSpace(direct)) { return direct; }

        var p = item.GetType().GetProperty("ArtistItems");
        if (p?.GetValue(item) is System.Collections.IEnumerable list)
        {
            var names = new List<string>();
            foreach (var e in list)
            {
                var n = e?.GetType().GetProperty("Name")?.GetValue(e) as string;
                if (!string.IsNullOrWhiteSpace(n)) { names.Add(n); }
            }

            if (names.Count > 0) { return string.Join(" / ", names); }
        }

        return null;
    }

    private static IEnumerable<(Guid Id, string? Name)> GetArtists(BaseItem item)
    {
        var p = item.GetType().GetProperty("ArtistItems");
        if (p?.GetValue(item) is System.Collections.IEnumerable list)
        {
            foreach (var entry in list)
            {
                if (entry is null) { continue; }
                var idProp = entry.GetType().GetProperty("Id");
                var nameProp = entry.GetType().GetProperty("Name");
                if (idProp?.GetValue(entry) is Guid g && g != Guid.Empty)
                {
                    yield return (g, nameProp?.GetValue(entry) as string);
                }
            }
        }
    }

    private static List<string> ReadTag(string path, string tag, char[] delimiters)
    {
        var result = new List<string>();
        var wanted = tag.Trim().ToUpperInvariant();

        try
        {
            using var f = TagLib.File.Create(path);
            var t = f.Tag;

            switch (wanted)
            {
                case "ARTIST": result.AddRange(t.Performers); break;
                case "ALBUMARTIST": result.AddRange(t.AlbumArtists); break;
                case "ALBUM": if (!string.IsNullOrWhiteSpace(t.Album)) { result.Add(t.Album); } break;
                case "TITLE": if (!string.IsNullOrWhiteSpace(t.Title)) { result.Add(t.Title); } break;
                case "GENRE": result.AddRange(t.Genres); break;
                case "COMPOSER": result.AddRange(t.Composers); break;
                case "YEAR": if (t.Year > 0) { result.Add(t.Year.ToString(CultureInfo.InvariantCulture)); } break;
                case "TRACKNUMBER": if (t.Track > 0) { result.Add(t.Track.ToString(CultureInfo.InvariantCulture)); } break;
                case "DISCNUMBER": if (t.Disc > 0) { result.Add(t.Disc.ToString(CultureInfo.InvariantCulture)); } break;
            }

            if (f.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    if (string.Equals(frame.Description, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        result.AddRange(frame.Text);
                    }
                }
            }

            if (f.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var field = xiph.GetField(wanted);
                if (field is not null) { result.AddRange(field); }
            }
        }
        catch
        {
            // unreadable file
        }

        return result
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v.Split(delimiters, StringSplitOptions.RemoveEmptyEntries))
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
