using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AdvancedMetadata.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AdvancedMetadata.Store;

public record FieldRow(string Name, string Display, string Hierarchy, int Items, int Values);

public record OverrideRow(string JellyfinId, string Field, string OldValue, string NewValue, string CreatedUtc);

public record BrowseRow(string Id, string Kind, string Item, string Artist, string Album, string Detail, string Field, string Value, bool IsOverride, bool IsDeleted, string OverrideOld);

public class MetadataStore
{
    private static readonly object InitGate = new();
    private static bool _initialised;

    private static readonly object SharedGate = new();
    private static SqliteConnection? _shared;
    private static SqliteTransaction? _sharedTx;
    private static readonly Dictionary<string, long> _fieldIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _dbPath;

    public MetadataStore(IApplicationPaths paths)
    {
        var dir = Path.Combine(paths.DataPath, "advancedmetadata");
        Directory.CreateDirectory(dir);
       _dbPath = Path.Combine(dir, "index.db");

        try
        {
            lock (InitGate)
            {
                if (!_initialised)
                {
                    Init();
                    _initialised = true;
                }
            }
        }
        catch (Exception ex) { Log("Store unavailable: " + ex.Message); }
    }

    public string DbPath => _dbPath;

    public string DatabasePath => _dbPath;

    /// <summary>Appends one line to debug.log next to index.db, and beside the plugin DLLs.</summary>
    private void Log(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine;
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) { AppendLog(Path.Combine(dir, "debug.log"), stamp); }
        }
        catch { }
        try { AppendLog(Path.Combine(AppContext.BaseDirectory, "debug.log"), stamp); } catch { }
    }

    private static void AppendLog(string path, string text)
    {
        var fi = new FileInfo(path);
        if (fi.Exists && fi.Length > 400_000) { File.WriteAllText(path, string.Empty); }
        File.AppendAllText(path, text);
    }

    /// <summary>Flushes the write-ahead log into the main file so a copy is self-contained.</summary>
    public void Checkpoint()
    {
        try
        {
            using var c = NewConn();
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch { }
    }

   /// <summary>Folds the write-ahead log back into the main file without blocking writers.</summary>
    public void CheckpointPassive()
    {
        try
        {
            using var c = NewConn();
            Exec(c, "PRAGMA wal_checkpoint(PASSIVE);");
        }
        catch (Exception ex)
        {
            Log("Passive checkpoint failed: " + ex.Message);
        }
    }

    /// <summary>Closes everything so the database file can be replaced underneath.</summary>
    public void CloseForMaintenance()
    {
        EndBatch();
        DropSideFiles();
    }

    /// <summary>Reopens after the file has been replaced. Safe to call when nothing was replaced.</summary>
    public void Reopen()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        Init();
    }

    public long SizeBytes
    {
        get
        {
            try { var fi = new FileInfo(_dbPath); return fi.Exists ? fi.Length : -1; }
            catch { return -1; }
        }
    }

    /* ---------------- connection handling ---------------- */

    private SqliteConnection NewConn()
    {
        var c = new SqliteConnection("Data Source=" + _dbPath + ";Default Timeout=60");
        c.Open();
        Exec(c, "PRAGMA busy_timeout=60000;");
        return c;
    }

   private T Read<T>(Func<SqliteConnection, T> fn)
    {
        // WAL readers never block on the batch writer, so give them their own connection.
        using var c = NewConn();
        return fn(c);
    }

    private T Write<T>(Func<SqliteConnection, T> fn)
    {
        lock (SharedGate)
        {
            if (_shared is not null) { return fn(_shared); }
            using var c = NewConn();
            using var tx = c.BeginTransaction();
            var result = fn(c);
            tx.Commit();
            return result;
        }
    }

    private void Write(Action<SqliteConnection> fn)
        => Write<object?>(c => { fn(c); return null; });

    /// <summary>Opens one connection + transaction for a whole scan.</summary>
    public void BeginBatch()
    {
        lock (SharedGate)
        {
            if (_shared is not null) { return; }
            _shared = NewConn();
            _sharedTx = _shared.BeginTransaction();
            _fieldIds.Clear();
        }
    }

    /// <summary>Commits current work and starts a fresh transaction, keeping the connection.</summary>
    public void CommitBatch()
    {
        lock (SharedGate)
        {
            if (_shared is null || _sharedTx is null) { return; }
            _sharedTx.Commit();
            _sharedTx.Dispose();
            _sharedTx = _shared.BeginTransaction();
        }
    }

    public void EndBatch()
    {
        lock (SharedGate)
        {
            try { _sharedTx?.Commit(); } catch { /* ignore */ }
            _sharedTx?.Dispose();
            _sharedTx = null;
            _shared?.Dispose();
            _shared = null;
            _fieldIds.Clear();
        }
    }

    /* ---------------- schema ---------------- */

    private void Init()
    {
        try
        {
            InitInner();
        }
        catch (SqliteException ex)
        {
            Log("Init failed: " + ex.Message + " — clearing WAL side files and retrying");
            LogEnvironment();
            DropSideFiles();
            try
            {
                InitInner();
            }
            catch (SqliteException ex2)
            {
                Log("Init failed again: " + ex2.Message + " — moving the index aside and starting fresh");
                try
                {
                    if (File.Exists(_dbPath))
                    {
                        File.Move(_dbPath, _dbPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                    }
                }
                catch { }

                DropSideFiles();
                InitInner();
            }
        }
    }

    /// <summary>Releases every handle and removes the WAL side files.</summary>
    private void DropSideFiles()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            try
            {
                if (File.Exists(p)) { File.Delete(p); }
            }
            catch (Exception ex)
            {
                Log("Could not delete " + p + " — " + ex.Message);
            }
        }
    }

    /// <summary>Logs whether the folder can be written to and how much space is left.</summary>
    private void LogEnvironment()
    {
        var probe = _dbPath + ".probe";
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            Log("Environment: store folder is writable");
        }
        catch (Exception ex)
        {
            Log("Environment: store folder is NOT writable — " + ex.Message);
        }

        try
        {
            var root = Path.GetPathRoot(_dbPath);
            var drive = new DriveInfo(string.IsNullOrEmpty(root) ? "/" : root);
            Log("Environment: free bytes on volume = " + drive.AvailableFreeSpace);
        }
        catch (Exception ex)
        {
            Log("Environment: free space unknown — " + ex.Message);
        }
    }

    private void InitInner()
    {
        using var c = NewConn();
        try { Exec(c, "PRAGMA journal_mode=WAL;"); } catch { }

        Exec(c, @"
CREATE TABLE IF NOT EXISTS items(id INTEGER PRIMARY KEY, jellyfin_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, name TEXT, artist TEXT, album TEXT, path TEXT, mtime INTEGER, size INTEGER);
CREATE TABLE IF NOT EXISTS fields(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, display TEXT NOT NULL, hierarchy TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS vals(item_id INTEGER NOT NULL, field_id INTEGER NOT NULL, value TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_vals ON vals(field_id, value);
CREATE INDEX IF NOT EXISTS ix_vals_item ON vals(item_id);
CREATE INDEX IF NOT EXISTS ix_items_name ON items(name);
CREATE TABLE IF NOT EXISTS overrides(id INTEGER PRIMARY KEY, jellyfin_id TEXT NOT NULL, field TEXT NOT NULL, old_value TEXT NOT NULL DEFAULT '', new_value TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS ix_ovr ON overrides(jellyfin_id, field, old_value);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");

        AddColumn(c, "items", "name", "TEXT");
        AddColumn(c, "items", "artist", "TEXT");
        AddColumn(c, "items", "album", "TEXT");
        AddColumn(c, "items", "parent_id", "TEXT");

        try
        {
            Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS ix_vals_uniq ON vals(item_id, field_id, value);");
        }
        catch
        {
            Exec(c, "DELETE FROM vals WHERE rowid NOT IN (SELECT MIN(rowid) FROM vals GROUP BY item_id, field_id, value);");
            Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS ix_vals_uniq ON vals(item_id, field_id, value);");
        }

        RepairOverrides(c);

        SetMeta("schema", "0.7");
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddColumn(SqliteConnection c, string table, string column, string type)
    {
        try { Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {type};"); }
        catch { /* already present */ }
    }

    public void SetMeta(string key, string value)
        => Write(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        });

    /// <summary>True when an item is missing artist or album metadata and should be repaired.</summary>
    public bool ItemMissingMeta(string jellyfinId)
        => Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT artist, album FROM items WHERE jellyfin_id=$j;";
            cmd.Parameters.AddWithValue("$j", jellyfinId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) { return true; }
            var a = r.IsDBNull(0) ? null : r.GetString(0);
            var b = r.IsDBNull(1) ? null : r.GetString(1);
            return string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b);
        });

    /* ---------------- items ---------------- */

    public bool IsUnchanged(string jellyfinId, long mtime, long size)
        => Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT mtime, size, name FROM items WHERE jellyfin_id=$j;";
            cmd.Parameters.AddWithValue("$j", jellyfinId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) { return false; }
            if (r.IsDBNull(2)) { return false; }
            return r.GetInt64(0) == mtime && r.GetInt64(1) == size;
        });

    public long UpsertItem(string jellyfinId, string kind, string? name, string? artist, string? album,
                           string? path, long mtime, long size)
    {
        lock (SharedGate)
        {
            if (_shared is not null)
            {
                return UpsertItemInner(_shared, jellyfinId, kind, name, artist, album, path, mtime, size);
            }

            using var c = NewConn();
            using var tx = c.BeginTransaction();
            var id = UpsertItemInner(c, jellyfinId, kind, name, artist, album, path, mtime, size);
            tx.Commit();
            return id;
        }
    }

    private static long UpsertItemInner(SqliteConnection c, string jellyfinId, string kind, string? name,
                                        string? artist, string? album, string? path, long mtime, long size)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO items(jellyfin_id, kind, name, artist, album, path, mtime, size)
                                VALUES ($j,$k,$n,$ar,$al,$p,$m,$s)
                                ON CONFLICT(jellyfin_id) DO UPDATE SET
                                  kind=$k, name=$n, artist=$ar, album=$al, path=$p, mtime=$m, size=$s;";
            cmd.Parameters.AddWithValue("$j", jellyfinId);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ar", (object?)artist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$al", (object?)album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object?)path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", mtime);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.ExecuteNonQuery();
        }

        using var idc = c.CreateCommand();
        idc.CommandText = "SELECT id FROM items WHERE jellyfin_id=$j;";
        idc.Parameters.AddWithValue("$j", jellyfinId);
        return (long)idc.ExecuteScalar()!;
    }

    /* ---------------- mappings / values ---------------- */

    public void SyncMappings(IEnumerable<Mapping> mappings)
    {
        var list = mappings.ToList();
        Write(c =>
        {
            foreach (var m in list)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"INSERT INTO fields(name, display, hierarchy) VALUES ($n,$d,$h)
                                    ON CONFLICT(name) DO UPDATE SET display=$d, hierarchy=$h;";
                cmd.Parameters.AddWithValue("$n", m.SourceTag.ToUpperInvariant());
                cmd.Parameters.AddWithValue("$d", m.DisplayName);
                cmd.Parameters.AddWithValue("$h", m.Hierarchy);
                cmd.ExecuteNonQuery();
            }

            var keep = list.Select(m => m.SourceTag.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var drop = new List<long>();
            using (var read = c.CreateCommand())
            {
                read.CommandText = "SELECT id, name FROM fields;";
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    if (!keep.Contains(r.GetString(1))) { drop.Add(r.GetInt64(0)); }
                }
            }

            foreach (var id in drop)
            {
                using (var d1 = c.CreateCommand())
                {
                    d1.CommandText = "DELETE FROM vals WHERE field_id=$f;";
                    d1.Parameters.AddWithValue("$f", id);
                    d1.ExecuteNonQuery();
                }

                using (var d2 = c.CreateCommand())
                {
                    d2.CommandText = "DELETE FROM fields WHERE id=$f;";
                    d2.Parameters.AddWithValue("$f", id);
                    d2.ExecuteNonQuery();
                }
            }
        });

        _fieldIds.Clear();
    }

    private static long FieldIdFor(SqliteConnection c, string field)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM fields WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", field.ToUpperInvariant());
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? 0L : (long)r;
    }

    private long FieldId(SqliteConnection c, string field)
    {
        var key = field.ToUpperInvariant();
        if (_fieldIds.TryGetValue(key, out var cached)) { return cached; }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM fields WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", key);
        var r = cmd.ExecuteScalar();
        var id = r is null or DBNull ? 0L : (long)r;
        if (id != 0) { _fieldIds[key] = id; }
        return id;
    }

    public void ReplaceFieldValues(long itemId, string field, IEnumerable<string> values)
        => Write(c =>
        {
            var fid = FieldId(c, field);
            if (fid == 0) { return; }

            using (var del = c.CreateCommand())
            {
                del.CommandText = "DELETE FROM vals WHERE item_id=$i AND field_id=$f;";
                del.Parameters.AddWithValue("$i", itemId);
                del.Parameters.AddWithValue("$f", fid);
                del.ExecuteNonQuery();
            }

            AddValuesInner(c, itemId, fid, values);
        });

    public void AddValues(long itemId, string field, IEnumerable<string> values)
        => Write(c =>
        {
            var fid = FieldId(c, field);
            if (fid == 0) { return; }
            AddValuesInner(c, itemId, fid, values);
        });

    private static void AddValuesInner(SqliteConnection c, long itemId, long fieldId, IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) { continue; }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO vals(item_id, field_id, value) VALUES ($i,$f,$v);";
            cmd.Parameters.AddWithValue("$i", itemId);
            cmd.Parameters.AddWithValue("$f", fieldId);
            cmd.Parameters.AddWithValue("$v", v.Trim());
            cmd.ExecuteNonQuery();
        }
    }

    public int ClearField(string field)
        => Write(c =>
        {
            var fid = FieldId(c, field);
            if (fid == 0) { return 0; }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM vals WHERE field_id=$f;";
            cmd.Parameters.AddWithValue("$f", fid);
            return cmd.ExecuteNonQuery();
        });

    public int DeleteField(string field)
    {
        var removed = Write(c =>
        {
            var fid = FieldId(c, field);
            if (fid == 0) { return 0; }
            int n;
            using (var del = c.CreateCommand())
            {
                del.CommandText = "DELETE FROM vals WHERE field_id=$f;";
                del.Parameters.AddWithValue("$f", fid);
                n = del.ExecuteNonQuery();
            }

            using (var drop = c.CreateCommand())
            {
                drop.CommandText = "DELETE FROM fields WHERE id=$f;";
                drop.Parameters.AddWithValue("$f", fid);
                drop.ExecuteNonQuery();
            }

            return n;
        });

        _fieldIds.Clear();
        return removed;
    }

    /* ---------------- overrides ---------------- */

    /// <summary>
    /// Records an override. The base value is resolved from the index rather than trusted
    /// from the caller, and an existing override is retargeted rather than duplicated.
    /// </summary>
    public void SetOverride(string jellyfinId, string field, string oldValue, string newValue)
        => Write(c =>
        {
            var f = field.ToUpperInvariant();
            var newV = newValue ?? string.Empty;
            var asked = oldValue ?? string.Empty;

            long itemId;
            using (var idc = c.CreateCommand())
            {
                idc.CommandText = "SELECT id FROM items WHERE jellyfin_id=$j;";
                idc.Parameters.AddWithValue("$j", jellyfinId);
                var o = idc.ExecuteScalar();
                if (o is null or DBNull) { return; }
                itemId = (long)o;
            }

            var fid = FieldIdFor(c, f);

            var ovr = new List<(long Id, string Old, string New)>();
            using (var sel = c.CreateCommand())
            {
                sel.CommandText = "SELECT id, old_value, new_value FROM overrides WHERE jellyfin_id=$j AND field=$f ORDER BY id;";
                sel.Parameters.AddWithValue("$j", jellyfinId);
                sel.Parameters.AddWithValue("$f", f);
                using var r = sel.ExecuteReader();
                while (r.Read()) { ovr.Add((r.GetInt64(0), r.GetString(1), r.GetString(2))); }
            }

            var present = new List<string>();
            if (fid != 0)
            {
                using var pv = c.CreateCommand();
                pv.CommandText = "SELECT value FROM vals WHERE item_id=$i AND field_id=$f;";
                pv.Parameters.AddWithValue("$i", itemId);
                pv.Parameters.AddWithValue("$f", fid);
                using var r2 = pv.ExecuteReader();
                while (r2.Read()) { present.Add(r2.GetString(0)); }
            }

            Log($"SetOverride j={jellyfinId} f={f} old='{asked}' new='{newV}' vals={present.Count} ovr={ovr.Count}");
            long? targetId = null;
            var stale = string.Empty;
            var baseValue = asked;

            // (1) caller handed us the value an existing override already produces -> retarget it
            foreach (var (id, ov, nv) in ovr)
            {
                if (nv == asked) { targetId = id; stale = nv; baseValue = ov; break; }
            }

            // (2) caller handed us that override's own original value
            if (targetId is null)
            {
                foreach (var (id, ov, nv) in ovr)
                {
                    if (ov == asked) { targetId = id; stale = nv; baseValue = ov; break; }
                }
            }

            // (3) no override yet -> the base is whatever the index actually holds
            if (targetId is null)
            {
                if ((!present.Contains(asked) || string.IsNullOrEmpty(asked)) && present.Count == 1) { baseValue = present[0]; }
            }

            if (targetId is long tid)
            {
                using var upd = c.CreateCommand();
                upd.CommandText = "UPDATE overrides SET new_value=$n, created_utc=$d WHERE id=$id;";
                upd.Parameters.AddWithValue("$n", newV);
                upd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("o"));
                upd.Parameters.AddWithValue("$id", tid);
                upd.ExecuteNonQuery();
            }
            else
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"INSERT INTO overrides(jellyfin_id, field, old_value, new_value, created_utc)
                                    VALUES ($j,$f,$o,$n,$d)
                                    ON CONFLICT(jellyfin_id, field, old_value) DO UPDATE SET new_value=$n, created_utc=$d;";
                cmd.Parameters.AddWithValue("$j", jellyfinId);
                cmd.Parameters.AddWithValue("$f", f);
                cmd.Parameters.AddWithValue("$o", baseValue);
                cmd.Parameters.AddWithValue("$n", newV);
                cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            // the retargeted override no longer produces its previous value
            if (!string.IsNullOrEmpty(stale) && stale != newV && fid != 0)
            {
                using var del = c.CreateCommand();
                del.CommandText = "DELETE FROM vals WHERE item_id=$i AND field_id=$f AND value=$v;";
                del.Parameters.AddWithValue("$i", itemId);
                del.Parameters.AddWithValue("$f", fid);
                del.Parameters.AddWithValue("$v", stale);
                del.ExecuteNonQuery();
            }

            Log($"  -> base='{baseValue}' target={(targetId?.ToString() ?? "none")}");
            ApplyInner(c, jellyfinId, f);
        });

    public void ApplyOverrides(string jellyfinId, string field)
        => Write(c => ApplyInner(c, jellyfinId, field));

    private static void ApplyInner(SqliteConnection c, string jellyfinId, string field)
    {
        long itemId;
        using (var idc = c.CreateCommand())
        {
            idc.CommandText = "SELECT id FROM items WHERE jellyfin_id=$j;";
            idc.Parameters.AddWithValue("$j", jellyfinId);
            var r = idc.ExecuteScalar();
            if (r is null or DBNull) { return; }
            itemId = (long)r;
        }

        var fid = FieldIdFor(c, field);
        if (fid == 0) { return; }

        var pairs = new List<(string Old, string New)>();
        using (var sel = c.CreateCommand())
        {
            sel.CommandText = "SELECT old_value, new_value FROM overrides WHERE jellyfin_id=$j AND field=$f;";
            sel.Parameters.AddWithValue("$j", jellyfinId);
            sel.Parameters.AddWithValue("$f", field.ToUpperInvariant());
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                pairs.Add((r.GetString(0), r.GetString(1)));
            }
        }

        foreach (var (oldV, newV) in pairs)
        {
            if (!string.IsNullOrEmpty(oldV))
            {
                using var del = c.CreateCommand();
                del.CommandText = "DELETE FROM vals WHERE item_id=$i AND field_id=$f AND value=$v;";
                del.Parameters.AddWithValue("$i", itemId);
                del.Parameters.AddWithValue("$f", fid);
                del.Parameters.AddWithValue("$v", oldV);
                del.ExecuteNonQuery();
            }

            if (!string.IsNullOrEmpty(newV))
            {
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO vals(item_id, field_id, value) VALUES ($i,$f,$v);";
                ins.Parameters.AddWithValue("$i", itemId);
                ins.Parameters.AddWithValue("$f", fid);
                ins.Parameters.AddWithValue("$v", newV);
                ins.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Records which album a track belongs to, so album-level reverts can find its tracks.</summary>
    public void SetItemParent(string jellyfinId, string parentId)
        => Write(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE items SET parent_id=$p WHERE jellyfin_id=$j;";
            cmd.Parameters.AddWithValue("$p", parentId);
            cmd.Parameters.AddWithValue("$j", jellyfinId);
            cmd.ExecuteNonQuery();
        });

    /// <summary>Marks an item so the next scan re-reads it, whatever else has changed.</summary>
    public void InvalidateItem(string jellyfinId)
        => Write(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE items SET mtime = -1 WHERE jellyfin_id=$j;";
            cmd.Parameters.AddWithValue("$j", jellyfinId);
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Removes one override, clears the affected values so the file can restore them,
    /// and flags the item — plus any tracks belonging to it — for re-reading.
    /// </summary>
        public int ClearOverride(string jellyfinId, string field, string oldValue)
        => Write(c =>
        {
            var f = field.ToUpperInvariant();
            var ov = oldValue ?? string.Empty;
            int n = 0;

            var hit = new List<(long Id, string Old, string New)>();
            var passes = new[] { "old_value", "new_value", "" };

            foreach (var col in passes)
            {
                if (hit.Count > 0) { break; }
                using var sel = c.CreateCommand();
                sel.CommandText = col.Length == 0
                    ? "SELECT id, old_value, new_value FROM overrides WHERE jellyfin_id=$j AND field=$f;"
                    : $"SELECT id, old_value, new_value FROM overrides WHERE jellyfin_id=$j AND field=$f AND {col}=$o;";
                sel.Parameters.AddWithValue("$j", jellyfinId);
                sel.Parameters.AddWithValue("$f", f);
                if (col.Length > 0) { sel.Parameters.AddWithValue("$o", ov); }
                using var r = sel.ExecuteReader();
                while (r.Read()) { hit.Add((r.GetInt64(0), r.GetString(1), r.GetString(2))); }
            }

            long itemId = 0;
            using (var idc = c.CreateCommand())
            {
                idc.CommandText = "SELECT id FROM items WHERE jellyfin_id=$j;";
                idc.Parameters.AddWithValue("$j", jellyfinId);
                var o = idc.ExecuteScalar();
                if (o is not null and not DBNull) { itemId = (long)o; }
            }

            var fid = FieldIdFor(c, f);

            foreach (var (id, oldV, newV) in hit)
            {
                using (var del = c.CreateCommand())
                {
                    del.CommandText = "DELETE FROM overrides WHERE id=$id;";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                    n++;
                }

                if (fid == 0 || itemId == 0) { continue; }

                // drop what the override produced, put back what it replaced
                if (!string.IsNullOrEmpty(newV))
                {
                    using var d1 = c.CreateCommand();
                    d1.CommandText = @"DELETE FROM vals WHERE field_id=$f AND value=$v AND item_id IN
                                       (SELECT id FROM items WHERE jellyfin_id=$j OR parent_id=$j);";
                    d1.Parameters.AddWithValue("$f", fid);
                    d1.Parameters.AddWithValue("$v", newV);
                    d1.Parameters.AddWithValue("$j", jellyfinId);
                    d1.ExecuteNonQuery();
                }

                if (!string.IsNullOrEmpty(oldV))
                {
                    using var i1 = c.CreateCommand();
                    i1.CommandText = "INSERT OR IGNORE INTO vals(item_id, field_id, value) VALUES ($i,$f,$v);";
                    i1.Parameters.AddWithValue("$i", itemId);
                    i1.Parameters.AddWithValue("$f", fid);
                    i1.Parameters.AddWithValue("$v", oldV);
                    i1.ExecuteNonQuery();
                }
            }

            using (var inv = c.CreateCommand())
            {
                inv.CommandText = "UPDATE items SET mtime = -1 WHERE jellyfin_id=$j OR parent_id=$j;";
                inv.Parameters.AddWithValue("$j", jellyfinId);
                inv.ExecuteNonQuery();
            }

            Log($"ClearOverride j={jellyfinId} f={f} old='{ov}' removed={n}");
            return n;
        });

    /// <summary>Wipes every override and forces a full re-read on the next scan.</summary>
    public int ClearAllOverrides()
        => Write(c =>
        {
            int n;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM overrides; DELETE FROM vals;";
                n = cmd.ExecuteNonQuery();
            }

            using (var inv = c.CreateCommand())
            {
                inv.CommandText = "UPDATE items SET mtime = -1;";
                inv.ExecuteNonQuery();
            }

            return n;
        });

    public List<OverrideRow> ListOverrides()
        => Read(c =>
        {
            var rows = new List<OverrideRow>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT jellyfin_id, field, old_value, new_value, created_utc FROM overrides ORDER BY field;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new OverrideRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            }

            return rows;
        });

    public void ClearAll()
    {
        Write(c => { Exec(c, "DELETE FROM vals; DELETE FROM items;"); });
        _fieldIds.Clear();
    }

    /* ---------------- one-time repair ---------------- */

    /// <summary>
    /// Fixes overrides written before the base value was resolved properly: any override whose
    /// old_value is not actually indexed gets the one unclaimed indexed value, and any chained
    /// duplicates are collapsed. Runs once.
    /// </summary>
    private static void RepairOverrides(SqliteConnection c)
    {
        using (var f = c.CreateCommand())
        {
            f.CommandText = "SELECT value FROM meta WHERE key='ovrfix';";
            var got = f.ExecuteScalar();
            if (got is string s && s == "2") { return; }
        }

        // 1. bad base values
        var work = new List<(long Id, string J, string F, string Nv, string Ov)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, jellyfin_id, field, new_value, old_value FROM overrides;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { work.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))); }
        }

        foreach (var w in work)
        {
            var fid = FieldIdFor(c, w.F);
            if (fid == 0) { continue; }

            long itemId;
            using (var idc = c.CreateCommand())
            {
                idc.CommandText = "SELECT id FROM items WHERE jellyfin_id=$j;";
                idc.Parameters.AddWithValue("$j", w.J);
                var o = idc.ExecuteScalar();
                if (o is null or DBNull) { continue; }
                itemId = (long)o;
            }

            var present = new List<string>();
            using (var pv = c.CreateCommand())
            {
                pv.CommandText = "SELECT value FROM vals WHERE item_id=$i AND field_id=$f;";
                pv.Parameters.AddWithValue("$i", itemId);
                pv.Parameters.AddWithValue("$f", fid);
                using var r2 = pv.ExecuteReader();
                while (r2.Read()) { present.Add(r2.GetString(0)); }
            }

            if (present.Contains(w.Ov)) { continue; }

            var cands = new List<string>();
            foreach (var v in present)
            {
                if (v == w.Nv) { continue; }
                using var u = c.CreateCommand();
                u.CommandText = "SELECT COUNT(*) FROM overrides WHERE jellyfin_id=$j AND field=$f AND new_value=$v;";
                u.Parameters.AddWithValue("$j", w.J);
                u.Parameters.AddWithValue("$f", w.F);
                u.Parameters.AddWithValue("$v", v);
                if (Convert.ToInt64(u.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) { cands.Add(v); }
            }

            if (cands.Count != 1) { continue; }

            try
            {
                using var fix = c.CreateCommand();
                fix.CommandText = "UPDATE overrides SET old_value=$o WHERE id=$id;";
                fix.Parameters.AddWithValue("$o", cands[0]);
                fix.Parameters.AddWithValue("$id", w.Id);
                fix.ExecuteNonQuery();
            }
            catch { /* unique clash - leave for the chain pass */ }
        }

        // 2. collapse chains: an old_value that is another override's new_value
        Exec(c, @"
UPDATE overrides
SET new_value = (SELECT o2.new_value FROM overrides o2
                 WHERE o2.jellyfin_id = overrides.jellyfin_id
                   AND o2.field = overrides.field
                   AND o2.old_value = overrides.new_value
                 LIMIT 1)
WHERE id = (SELECT MIN(id) FROM overrides o3
            WHERE o3.jellyfin_id = overrides.jellyfin_id AND o3.field = overrides.field)
  AND EXISTS (SELECT 1 FROM overrides o2
              WHERE o2.jellyfin_id = overrides.jellyfin_id
                AND o2.field = overrides.field
                AND o2.old_value = overrides.new_value);");

        Exec(c, @"
DELETE FROM overrides
WHERE id > (SELECT MIN(id) FROM overrides o3
            WHERE o3.jellyfin_id = overrides.jellyfin_id AND o3.field = overrides.field)
  AND EXISTS (SELECT 1 FROM overrides o2
              WHERE o2.jellyfin_id = overrides.jellyfin_id
                AND o2.field = overrides.field
                AND o2.old_value = overrides.new_value);");

        // 3. re-derive the affected items
        var pairs = new List<(string J, string F)>();
        using (var sel = c.CreateCommand())
        {
            sel.CommandText = "SELECT DISTINCT jellyfin_id, field FROM overrides;";
            using var r = sel.ExecuteReader();
            while (r.Read()) { pairs.Add((r.GetString(0), r.GetString(1))); }
        }

        foreach (var (j, f) in pairs)
        {
            var fid = FieldIdFor(c, f);
            if (fid == 0) { continue; }

            using (var d = c.CreateCommand())
            {
                d.CommandText = "DELETE FROM vals WHERE field_id=$f AND item_id=(SELECT id FROM items WHERE jellyfin_id=$j);";
                d.Parameters.AddWithValue("$f", fid);
                d.Parameters.AddWithValue("$j", j);
                d.ExecuteNonQuery();
            }

            ApplyInner(c, j, f);

            using var inv = c.CreateCommand();
            inv.CommandText = "UPDATE items SET mtime=-1 WHERE jellyfin_id=$j;";
            inv.Parameters.AddWithValue("$j", j);
            inv.ExecuteNonQuery();
        }

        Exec(c, "INSERT INTO meta(key,value) VALUES('ovrfix','2') ON CONFLICT(key) DO UPDATE SET value='2';");
    }
/* ---------------- read-only browse ---------------- */

    public record BrowseResult(List<BrowseRow> Rows, int Total);

    public (int Items, int Values) Counts()
        => Read(c =>
        {
            using var i = c.CreateCommand();
            i.CommandText = "SELECT COUNT(*) FROM items;";
            using var v = c.CreateCommand();
            v.CommandText = "SELECT COUNT(*) FROM vals;";
            return (
                Convert.ToInt32(i.ExecuteScalar(), CultureInfo.InvariantCulture),
                Convert.ToInt32(v.ExecuteScalar(), CultureInfo.InvariantCulture));
        });

    /// <summary>Per-mapping counts, for the Manage Mappings tab.</summary>
    public List<FieldRow> FieldSummary()
        => Read(c =>
        {
            var rows = new List<FieldRow>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
SELECT f.name, f.display, f.hierarchy,
       (SELECT COUNT(DISTINCT item_id) FROM vals WHERE field_id = f.id),
       (SELECT COUNT(*) FROM vals WHERE field_id = f.id)
FROM fields f
ORDER BY f.name;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new FieldRow(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    Convert.ToInt32(r.GetInt64(3), CultureInfo.InvariantCulture),
                    Convert.ToInt32(r.GetInt64(4), CultureInfo.InvariantCulture)));
            }

            return rows;
        });

    private const string RowCols = @"
i.jellyfin_id AS id, i.kind AS kind,
COALESCE(i.name,'') AS item, COALESCE(i.artist,'') AS artist,
COALESCE(i.album,'') AS album, COALESCE(i.path,'') AS detail,
f.name AS field, v.value AS value,
CASE WHEN EXISTS (SELECT 1 FROM overrides o
                  WHERE o.jellyfin_id = i.jellyfin_id AND o.field = f.name AND o.new_value = v.value)
     THEN 1 ELSE 0 END AS isoverride,
0 AS isdeleted,
COALESCE((SELECT o.old_value FROM overrides o
          WHERE o.jellyfin_id = i.jellyfin_id AND o.field = f.name AND o.new_value = v.value
          LIMIT 1), '') AS override_old";

    private const string DeletedCols = @"
i.jellyfin_id AS id, i.kind AS kind,
COALESCE(i.name,'') AS item, COALESCE(i.artist,'') AS artist,
COALESCE(i.album,'') AS album, COALESCE(i.path,'') AS detail,
f.name AS field, o.old_value AS value,
1 AS isoverride, 1 AS isdeleted, o.old_value AS override_old";

    private const string UnionSql =
        "SELECT " + RowCols + @"
        FROM vals v
        JOIN items i ON i.id = v.item_id
        JOIN fields f ON f.id = v.field_id
        UNION ALL
        SELECT " + DeletedCols + @"
        FROM overrides o
        JOIN items i ON i.jellyfin_id = o.jellyfin_id
        JOIN fields f ON f.name = o.field
        WHERE o.new_value = ''";

    private static BrowseResult Run(SqliteConnection c, string where,
                                    List<(string K, object V)> args, int limit, int offset)
    {
        long total;
        using (var cc = c.CreateCommand())
        {
            cc.CommandText = "SELECT COUNT(*) FROM (" + UnionSql + ") x WHERE " + where + ";";
            foreach (var (k, val) in args) { cc.Parameters.AddWithValue(k, val); }
            total = Convert.ToInt64(cc.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var rows = new List<BrowseRow>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM (" + UnionSql + ") x WHERE " + where +
                              " ORDER BY artist, album, item, field, value LIMIT $lim OFFSET $off;";
            foreach (var (k, val) in args) { cmd.Parameters.AddWithValue(k, val); }
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.Parameters.AddWithValue("$off", offset);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new BrowseRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetString(7),
                    r.GetInt64(8) != 0, r.GetInt64(9) != 0, r.GetString(10)));
            }
        }

        return new BrowseResult(rows, (int)total);
    }

    /// <summary>Browse indexed values for a mapping, optionally filtered by value text and item type.</summary>
    public BrowseResult BrowseField(string field = "", string? query = null, string? kind = null,
                                    bool onlyOverrides = false, int limit = 300, int offset = 0)
        => Read(c =>
        {
            var where = new List<string>();
            var args = new List<(string K, object V)>();

            if (!string.IsNullOrWhiteSpace(field))
            {
                where.Add("field = $f");
                args.Add(("$f", field.ToUpperInvariant()));
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                where.Add("value LIKE $like");
                args.Add(("$like", "%" + query + "%"));
            }

            if (!string.IsNullOrWhiteSpace(kind))
            {
                where.Add("LOWER(kind) = LOWER($k)");
                args.Add(("$k", kind));
            }

            if (onlyOverrides) { where.Add("(isoverride = 1 OR isdeleted = 1)"); }

            return Run(c, where.Count == 0 ? "1=1" : string.Join(" AND ", where), args,
                       Math.Max(1, limit), Math.Max(0, offset));
        });

    /// <summary>All indexed values for items matching a title, artist, album or filename.</summary>
    public BrowseResult BrowseItem(string name = "", string? kind = null, bool onlyOverrides = false,
                                   int limit = 300, int offset = 0)
        => Read(c =>
        {
            var where = new List<string>();
            var args = new List<(string K, object V)>();

            var terms = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6).ToArray();
            if (terms.Length == 0)
            {
                where.Add("1=1");
            }
            else
            {
                var parts = new List<string>();
                for (var i = 0; i < terms.Length; i++)
                {
                    parts.Add($"(item LIKE $t{i} OR artist LIKE $t{i} OR album LIKE $t{i} OR detail LIKE $t{i})");
                    args.Add(("$t" + i, "%" + terms[i] + "%"));
                }

                where.Add("(" + string.Join(" AND ", parts) + ")");
            }

            if (!string.IsNullOrWhiteSpace(kind))
            {
                where.Add("LOWER(kind) = LOWER($k)");
                args.Add(("$k", kind));
            }

            if (onlyOverrides) { where.Add("(isoverride = 1 OR isdeleted = 1)"); }

            return Run(c, string.Join(" AND ", where), args, Math.Max(1, limit), Math.Max(0, offset));
        });
}
