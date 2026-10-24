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
        cfg.LastScanStartedUtc = DateTime.UtcNow;
        cfg.LastScanFinishedUtc = null;
        cfg.LastScanError = string.Empty;
        cfg.LastScanItems = 0;
        cfg.LastScanSkipped = 0;
        cfg.LastScanValues = 0;
        cfg.LastScanErrors = 0;
        cfg.LastScanTotal = 0;
        cfg.LastScanType = full ? "Full Scan" : "Incremental Scan";
        cfg.LastScanStatus = "Running";
        cfg.LastScanSummary = string.Empty;
        plugin.UpdateConfiguration(cfg);

        try
        {
            var mappings = cfg.Mappings.Where(m => !string.IsNullOrWhiteSpace(m.SourceTag)).ToList();
            var delims = cfg.Delimiters.ToCharArray();

            _store.SyncMappings(mappings);

            if (full)
            {
                foreach (var m in mappings) { _store.ClearField(m.SourceTag); }
            }

            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true
            }).OfType<Audio>().ToList();

            cfg.LastScanTotal = items.Count;
            plugin.UpdateConfiguration(cfg);

            _logger.LogInformation("Advanced Metadata: {Type} over {Count} tracks into {Path}",
                cfg.LastScanType, items.Count, _store.DatabasePath);

            var done = 0;
            var skipped = 0;
            var values = 0;
            var errors = 0;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                done++;

                if (items.Count > 0) { progress.Report(done * 100.0 / items.Count); }

                if (done % 250 == 0)
                {
                    cfg.LastScanItems = done;
                    cfg.LastScanSkipped = skipped;
                    plugin.UpdateConfiguration(cfg);
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

                if (!full && _store.IsUnchanged(key, mtime, size))
                {
                    skipped++;
                    continue;
                }

                var artist = GetStringList(item, "Artists");
                var albumArtist = GetStringList(item, "AlbumArtists");
                var album = GetStringProperty(item, "Album");

                long itemId;
                try
                {
                    itemId = _store.UpsertItem(key, "Audio", item.Name, artist, album, path, mtime, size);
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogWarning(ex, "Advanced Metadata: could not index {Path}", path);
                    continue;
                }

                foreach (var m in mappings)
                {
                    var vals = ReadTag(path, m.SourceTag, delims);
                    if (vals.Count == 0) { continue; }

                    _store.ReplaceFieldValues(itemId, m.SourceTag, vals);
                    values += vals.Count;

                    if (m.Hierarchy.Contains("albums", StringComparison.OrdinalIgnoreCase))
                    {
                        var albumId = GetGuidProperty(item, "AlbumEntityId") ?? GetGuidProperty(item, "AlbumId");
                        if (albumId.HasValue)
                        {
                            var tid = _store.UpsertItem(albumId.Value.ToString("N"), "MusicAlbum",
                                album, albumArtist, album, null, 0, 0);
                            _store.AddValues(tid, m.SourceTag, vals);
                        }
                    }

                    if (m.Hierarchy.Contains("artists", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var a in GetArtists(item))
                        {
                            var tid = _store.UpsertItem(a.Id.ToString("N"), "MusicArtist",
                                a.Name, a.Name, null, null, 0, 0);
                            _store.AddValues(tid, m.SourceTag, vals);
                        }
                    }
                }
            }

            progress.Report(100);
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
            cfg.LastScanStatus = "Failed To Execute";
            cfg.LastScanError = ex.Message;
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            plugin.UpdateConfiguration(cfg);
            _logger.LogError(ex, "Advanced Metadata scan failed");
            throw;
        }
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
