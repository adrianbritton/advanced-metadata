using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.AdvancedMetadata.Configuration;
using Jellyfin.Plugin.AdvancedMetadata.Store;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AdvancedMetadata.Controllers;

[ApiController]
[Authorize]
[Route("AdvancedMetadataData")]
public class DataController : ControllerBase
{
    private const string ScanKey = "AdvancedMetadataScan";
    private const long UploadLimit = 536870912;

    private readonly MetadataStore _store;
    private readonly ITaskManager _tasks;

    public DataController(MetadataStore store, ITaskManager tasks)
    {
        _store = store;
        _tasks = tasks;
    }

    private static bool ScanRunning()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) { return false; }
        if (!string.Equals(cfg.LastScanStatus, "Running", StringComparison.OrdinalIgnoreCase)) { return false; }

        // A scan killed by a restart leaves the status stuck on Running; ignore anything ancient.
        var started = cfg.LastScanStartedUtc;
        return started is null || (DateTime.UtcNow - started.Value) < TimeSpan.FromHours(12);
    }

    private static readonly byte[] SqliteMagic =
    {
        0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00
    };

    private static bool IsSqlite(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 16) { return false; }
        var buf = new byte[16];
        if (fs.Read(buf, 0, 16) < 16) { return false; }
        for (var i = 0; i < 16; i++) { if (buf[i] != SqliteMagic[i]) { return false; } }
        return true;
    }

    private static void ClearPools()
    {
        var names = new[]
        {
            "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite",
            "System.Data.SQLite.SQLiteConnection, System.Data.SQLite"
        };

        foreach (var n in names)
        {
            try
            {
                var t = Type.GetType(n, false);
                t?.GetMethod("ClearAllPools", Type.EmptyTypes)?.Invoke(null, null);
            }
            catch
            {
                // provider absent or refusing; the rename still usually works on Linux
            }
        }
    }

    private static void PruneSafety(string folder)
    {
        try
        {
            foreach (var p in Directory.GetFiles(folder, "pre-restore-*.db")
                         .OrderByDescending(p => p, StringComparer.Ordinal).Skip(3))
            {
                System.IO.File.Delete(p);
            }
        }
        catch
        {
            // pruning is cosmetic
        }
    }

    [HttpGet("Info")]
    public ActionResult Info()
    {
        var counts = _store.Counts();
        var folder = Path.GetDirectoryName(_store.DbPath) ?? string.Empty;
        return Ok(new
        {
            StoreFolder = folder,
            StorePath = _store.DbPath,
            Schema = "0.7",
            SizeBytes = _store.SizeBytes,
            Items = counts.Items,
            Values = counts.Values,
            Mappings = Plugin.Instance?.Configuration.Mappings.Count ?? 0,
            LastRestore = Plugin.Instance?.Configuration.LastRestoreUtc,
            LastRestoreItems = Plugin.Instance?.Configuration.LastRestoreItems ?? 0
        });
    }

    [HttpGet("Fields")]
    public ActionResult Fields()
    {
        var rows = new List<object>();
        foreach (var f in _store.FieldSummary())
        {
            rows.Add(new { f.Name, f.Display, f.Hierarchy, f.Items, f.Values });
        }

        return Ok(rows);
    }

    [HttpGet("Values")]
    public ActionResult Values([FromQuery] string? field, [FromQuery] string? q, [FromQuery] string? kind,
                               [FromQuery] bool? onlyOverrides, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        var lim = Math.Clamp(limit ?? 300, 1, 2000);
        var off = Math.Max(0, offset ?? 0);
        var result = _store.BrowseField(field ?? string.Empty, q, kind, onlyOverrides == true, lim, off);
        return Ok(new
        {
            Total = result.Total,
            Rows = result.Rows.Select(v => new
            {
                v.Id, v.Kind, v.Item, v.Artist, v.Album, v.Detail, v.Field, v.Value,
                v.IsOverride, v.IsDeleted, v.OverrideOld
            })
        });
    }

    [HttpGet("Item")]
    public ActionResult Item([FromQuery] string? name, [FromQuery] string? kind,
                             [FromQuery] bool? onlyOverrides, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        var lim = Math.Clamp(limit ?? 300, 1, 2000);
        var off = Math.Max(0, offset ?? 0);
        var result = _store.BrowseItem(name ?? string.Empty, kind, onlyOverrides == true, lim, off);
        return Ok(new
        {
            Total = result.Total,
            Rows = result.Rows.Select(v => new
            {
                v.Id, v.Kind, v.Item, v.Artist, v.Album, v.Detail, v.Field, v.Value,
                v.IsOverride, v.IsDeleted, v.OverrideOld
            })
        });
    }

    [HttpGet("Overrides")]
    public ActionResult Overrides()
        => Ok(_store.ListOverrides());

    [HttpPost("SetOverride")]
    public ActionResult SetOverride([FromQuery] string jellyfinId, [FromQuery] string field,
                                    [FromQuery] string? oldValue, [FromQuery] string? newValue)
    {
        if (string.IsNullOrWhiteSpace(jellyfinId) || string.IsNullOrWhiteSpace(field))
        {
            return BadRequest("jellyfinId and field are required");
        }

        _store.SetOverride(jellyfinId, field, oldValue ?? string.Empty, newValue ?? string.Empty);
        return Ok(new { jellyfinId, field });
    }

    [HttpPost("ClearOverride")]
    public ActionResult ClearOverride([FromQuery] string jellyfinId, [FromQuery] string field,
                                      [FromQuery] string? oldValue)
    {
        if (string.IsNullOrWhiteSpace(jellyfinId) || string.IsNullOrWhiteSpace(field))
        {
            return BadRequest("jellyfinId and field are required");
        }

        var removed = _store.ClearOverride(jellyfinId, field, oldValue ?? string.Empty);
        return Ok(new { Removed = removed });
    }

    [HttpPost("ClearAllOverrides")]
    public ActionResult ClearAllOverrides()
    {
        var removed = _store.ClearAllOverrides();
        return Ok(new { Removed = removed });
    }

    [HttpPost("SyncFields")]
    public ActionResult SyncFields()
    {
        var mappings = Plugin.Instance?.Configuration.Mappings;
        if (mappings is null) { return Ok(new { Synced = 0 }); }
        _store.SyncMappings(mappings);
        return Ok(new { Synced = mappings.Count });
    }

    [HttpPost("DeleteFieldValues")]
    public ActionResult DeleteFieldValues([FromQuery] string field)
    {
        if (string.IsNullOrWhiteSpace(field)) { return BadRequest("field required"); }
        var removed = _store.DeleteField(field);
        return Ok(new { Field = field, Removed = removed });
    }

    [HttpPost("ClearAll")]
    public ActionResult ClearAll()
    {
        var before = _store.Counts();
        _store.ClearAll();
        return Ok(new { Removed = before.Values });
    }

    [HttpGet("Backup")]
    public ActionResult Backup()
    {
        if (ScanRunning())
        {
            return Ok(new { Message = "A scan is running. Backups are paused so the copy cannot catch the index mid-write." });
        }

        var db = _store.DbPath;
        var folder = Path.GetDirectoryName(db) ?? string.Empty;
        if (!System.IO.File.Exists(db))
        {
            return NotFound(new { Message = "There is no index file to back up yet." });
        }

        _store.Checkpoint();

        var work = Path.Combine(Path.GetTempPath(), "am-bk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "backup.zip");

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
               AddFile(zip, db, "index.db");

                var walPath = db + "-wal";
                if (System.IO.File.Exists(walPath) && new FileInfo(walPath).Length > 0)
                {
                    AddFile(zip, walPath, "index.db-wal");
                }

                var counts = _store.Counts();
                AddText(zip, "manifest.txt",
                    "Advanced Metadata backup\n"
                    + "created: " + DateTime.UtcNow.ToString("o") + "\n"
                    + "plugin: " + (Plugin.Instance?.Version?.ToString() ?? "unknown") + "\n"
                    + "items: " + counts.Items + "\n"
                    + "values: " + counts.Values + "\n");

                AddText(zip, "mappings.json",
                    JsonSerializer.Serialize(Plugin.Instance?.Configuration.Mappings ?? new List<Mapping>()));
            }

            var bytes = System.IO.File.ReadAllBytes(zipPath);
            var name = "advancedmetadata-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip";
            return File(bytes, "application/zip", name);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }

    private static void AddFile(ZipArchive zip, string source, string entryName)
    {
        if (!System.IO.File.Exists(source)) { return; }
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var es = entry.Open();
        using var fs = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.CopyTo(es);
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var es = entry.Open();
        using var w = new StreamWriter(es);
        w.Write(text);
    }

    [HttpPost("Restore")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    public ActionResult Restore()
    {
       if (ScanRunning())
        {
            return Ok(new { Ok = false, Message = "A scan is running. Restores are paused so the index cannot be swapped mid-write." });
        }

        var upload = Request.Form.Files.FirstOrDefault();
        if (upload is null || upload.Length == 0)
        {
            return Ok(new { Ok = false, Message = "No backup file was received." });
        }

        var db = _store.DbPath;
        var folder = Path.GetDirectoryName(db) ?? string.Empty;
        Directory.CreateDirectory(folder);

        var work = Path.Combine(Path.GetTempPath(), "am-rs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var zipPath = Path.Combine(work, "backup.zip");
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            {
                upload.CopyTo(fs);
            }

            var staged = Path.Combine(work, "index.db");
            string? mappingsJson = null;

            if (IsSqlite(zipPath))
            {
                System.IO.File.Copy(zipPath, staged, true);
            }
            else
            {
                ZipArchive zip;
                try { zip = ZipFile.OpenRead(zipPath); }
                catch
                {
                    return Ok(new { Ok = false, Message = "That file is neither a plugin backup zip nor a SQLite database." });
                }
    
                using (zip)
                {
                    var dbEntry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "index.db", StringComparison.OrdinalIgnoreCase));
                    if (dbEntry is null)
                    {
                        return Ok(new { Ok = false, Message = "The archive does not contain index.db." });
                    }
    
                   dbEntry.ExtractToFile(staged, true);
                    if (!IsSqlite(staged))
                    {
                        return Ok(new { Ok = false, Message = "index.db inside the archive is not a SQLite database." });
                    }

                    var walEntry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "index.db-wal", StringComparison.OrdinalIgnoreCase));
                    if (walEntry is not null)
                    {
                        walEntry.ExtractToFile(Path.Combine(work, "index.db-wal"), true);
                    }
    
                    var jsonEntry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "mappings.json", StringComparison.OrdinalIgnoreCase));
                    if (jsonEntry is not null)
                    {
                        using var sr = new StreamReader(jsonEntry.Open());
                        mappingsJson = sr.ReadToEnd();
                    }
                }
            }

                var safety = Path.Combine(folder, "pre-restore-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".db");
                var hadDb = System.IO.File.Exists(db);
                if (hadDb)
                {
                    System.IO.File.Copy(db, safety, true);
                }

                ClearPools();
                Thread.Sleep(80);

                try
                {
                   var wal = db + "-wal";
                    _store.CloseForMaintenance();
                    System.IO.File.Copy(staged, db, true);

                    var stagedWal = Path.Combine(work, "index.db-wal");
                    if (System.IO.File.Exists(stagedWal)) { System.IO.File.Copy(stagedWal, wal, true); }

                    _store.Reopen();
                }
                catch (IOException)
                {
                    if (hadDb) { try { System.IO.File.Copy(safety, db, true); } catch { /* rollback best effort */ } }
                    return Ok(new { Ok = false, Message = "The index file is locked. Stop any running task and try again." });
                }

                ClearPools();
                Thread.Sleep(150);

                int items;
                int values;
                try
                {
                    var counts = _store.Counts();
                    items = counts.Items;
                    values = counts.Values;
                }
                catch (Exception ex)
                {
                    return Ok(new
                    {
                        Ok = true,
                        Items = 0,
                        Values = 0,
                        Safety = System.IO.File.Exists(safety) ? Path.GetFileName(safety) : string.Empty,
                        Message = "The index file was replaced but cannot be read until the database is "
                            + "reopened (" + ex.Message + "). Restart Jellyfin to finish the restore."
                    });
                }

                if (mappingsJson is not null && Plugin.Instance is not null)
                {
                    try
                    {
                        var imported = JsonSerializer.Deserialize<List<Mapping>>(mappingsJson);
                        if (imported is not null)
                        {
                            var cfg = Plugin.Instance.Configuration;
                            cfg.Mappings = imported;
                            cfg.KnownMappingIds = new List<Guid>();
                            Plugin.Instance.UpdateConfiguration(cfg);
                        }
                    }
                    catch
                    {
                        // mappings stay as they are
                    }
                }

                PruneSafety(folder);

                if (Plugin.Instance is not null)
                {
                    var after = Plugin.Instance.Configuration;
                    after.LastRestoreUtc = DateTime.UtcNow;
                    after.LastRestoreItems = items;
                    Plugin.Instance.UpdateConfiguration(after);
                }

                return Ok(new
                {
                    Ok = true,
                    Items = items,
                    Values = values,
                   Safety = System.IO.File.Exists(safety) ? Path.GetFileName(safety) : string.Empty
                });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }
}
