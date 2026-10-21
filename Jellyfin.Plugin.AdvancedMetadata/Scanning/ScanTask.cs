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

        cfg.LastScanStartedUtc = DateTime.UtcNow;
        cfg.LastScanFinishedUtc = null;
        cfg.LastScanError = string.Empty;
        cfg.LastScanItems = 0;
        cfg.LastScanValues = 0;
        cfg.LastScanErrors = 0;
        cfg.LastScanTotal = 0;
        cfg.LastScanSummary = "Scan running...";
        var full = cfg.FullRescanRequested;
        cfg.FullRescanRequested = false;
        plugin.UpdateConfiguration(cfg);

        try
        {
            var mappings = cfg.Mappings.Where(m => !string.IsNullOrWhiteSpace(m.SourceTag)).ToList();
            var delims = cfg.Delimiters.ToCharArray();

            _store.SyncMappings(mappings);

            if (full)
            {
                foreach (var m in mappings)
                {
                    _store.ClearField(m.SourceTag);
                }
            }

            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true
            }).OfType<Audio>().ToList();

            cfg.LastScanTotal = items.Count;
            plugin.UpdateConfiguration(cfg);

            _logger.LogInformation(
                "Advanced Metadata: scanning {Count} tracks (full rescan: {Full}) into {Path}",
                items.Count, full, _store.DatabasePath);

            var done = 0;
            var values = 0;
            var errors = 0;
            var skipped = 0;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                done++;

                if (items.Count > 0)
                {
                    progress.Report(done * 100.0 / items.Count);
                }

                if (done % 250 == 0)
                {
                    cfg.LastScanItems = done;
                    plugin.UpdateConfiguration(cfg);
                }

                var path = item.Path;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

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

                long itemId;
                try
                {
                    itemId = _store.UpsertItem(key, "Audio", path, mtime, size);
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
                    if (vals.Count == 0) continue;

                    _store.ReplaceFieldValues(itemId, m.SourceTag, vals);
                    values += vals.Count;

                    if (m.Hierarchy.Contains("albums", StringComparison.OrdinalIgnoreCase))
                    {
                        var albumId = GetGuidProperty(item, "AlbumEntityId") ?? GetGuidProperty(item, "AlbumId");
                        if (albumId.HasValue)
                        {
                            var tid = _store.UpsertItem(albumId.Value.ToString("N"), "MusicAlbum", null, 0, 0);
                            _store.AddValues(tid, m.SourceTag, vals);
                        }
                    }

                    if (m.Hierarchy.Contains("artists", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var artistId in GetArtistIds(item))
                        {
                            var tid = _store.UpsertItem(artistId.ToString("N"), "MusicArtist", null, 0, 0);
                            _store.AddValues(tid, m.SourceTag, vals);
                        }
                    }
                }
            }

            progress.Report(100);
            cfg.LastScanItems = done;
            cfg.LastScanValues = values;
            cfg.LastScanErrors = errors;
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            cfg.LastScanSummary = $"Scan complete: {done} tracks read ({skipped} unchanged, skipped), {values} values indexed, {errors} errors.";
            plugin.UpdateConfiguration(cfg);
            _logger.LogInformation(
                "Advanced Metadata: {Summary} Index size: {Bytes} bytes",
                cfg.LastScanSummary, _store.SizeBytes);
        }
        catch (Exception ex)
        {
            cfg.LastScanError = ex.Message;
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            cfg.LastScanSummary = "Scan FAILED: " + ex.Message;
            plugin.UpdateConfiguration(cfg);
            _logger.LogError(ex, "Advanced Metadata scan failed");
            throw;
        }
    }

    private static Guid? GetGuidProperty(BaseItem item, string name)
    {
        var p = item.GetType().GetProperty(name);
        if (p?.GetValue(item) is Guid g && g != Guid.Empty) return g;
        return null;
    }

    private static IEnumerable<Guid> GetArtistIds(BaseItem item)
    {
        var p = item.GetType().GetProperty("ArtistItems");
        if (p?.GetValue(item) is System.Collections.IEnumerable list)
        {
            foreach (var entry in list)
            {
                var idProp = entry?.GetType().GetProperty("Id");
                if (idProp?.GetValue(entry) is Guid g && g != Guid.Empty) yield return g;
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
                case "ALBUM": if (!string.IsNullOrWhiteSpace(t.Album)) result.Add(t.Album); break;
                case "TITLE": if (!string.IsNullOrWhiteSpace(t.Title)) result.Add(t.Title); break;
                case "GENRE": result.AddRange(t.Genres); break;
                case "COMPOSER": result.AddRange(t.Composers); break;
                case "YEAR": if (t.Year > 0) result.Add(t.Year.ToString(CultureInfo.InvariantCulture)); break;
                case "TRACKNUMBER": if (t.Track > 0) result.Add(t.Track.ToString(CultureInfo.InvariantCulture)); break;
                case "DISCNUMBER": if (t.Disc > 0) result.Add(t.Disc.ToString(CultureInfo.InvariantCulture)); break;
            }

            if (result.Count == 0)
            {
                // custom ID3v2 TXXX frames, e.g. LABEL, OWNER, CATALOGNUMBER
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

                // FLAC / Ogg Vorbis comments
                if (result.Count == 0 && f.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                {
                    var field = xiph.GetField(wanted);
                    if (field is not null) result.AddRange(field);
                }
            }
        }
        catch
        {
            // unreadable file — treated as "no value"
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
