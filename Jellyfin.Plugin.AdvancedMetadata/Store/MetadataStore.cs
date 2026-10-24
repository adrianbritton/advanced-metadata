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

public record BrowseRow(string Id, string Kind, string Item, string Artist, string Album, string Detail, string Field, string Value);

public class MetadataStore
{
    private readonly string _dbPath;
    private SqliteConnection? _shared;
    private SqliteTransaction? _sharedTx;
    private readonly Dictionary<string, long> _fieldIds = new(StringComparer.OrdinalIgnoreCase);

    public MetadataStore(IApplicationPaths paths)
    {
        var dir = Path.Combine(paths.DataPath, "advancedmetadata");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "index.db");
        Init();
    }

    public string DbPath => _dbPath;

    public string DatabasePath => _dbPath;

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
        var c = new SqliteConnection("Data Source=" + _dbPath);
        c.Open();
        return c;
    }

    private T Read<T>(Func<SqliteConnection, T> fn)
    {
        if (_shared is not null) { return fn(_shared); }
        using var c = NewConn();
        return fn(c);
    }

    private void Write(Action<SqliteConnection> fn)
    {
        if (_shared is not null) { fn(_shared); return; }
        using var c = NewConn();
        using var tx = c.BeginTransaction();
        fn(c);
        tx.Commit();
    }

    /// <summary>Opens one connection + transaction for a whole scan.</summary>
    public void BeginBatch()
    {
        if (_shared is not null) { return; }
        _shared = NewConn();
        _sharedTx = _shared.BeginTransaction();
        _fieldIds.Clear();
    }

    /// <summary>Commits current work and starts a fresh transaction, keeping the connection.</summary>
    public void CommitBatch()
    {
        if (_shared is null || _sharedTx is null) { return; }
        _sharedTx.Commit();
        _sharedTx.Dispose();
        _sharedTx = _shared.BeginTransaction();
    }

    public void EndBatch()
    {
        try { _sharedTx?.Commit(); } catch { /* ignore */ }
        _sharedTx?.Dispose();
        _sharedTx = null;
        _shared?.Dispose();
        _shared = null;
        _fieldIds.Clear();
    }

    /* ---------------- schema ---------------- */

    private void Init()
    {
        using var c = NewConn();

        Exec(c, @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS items(id INTEGER PRIMARY KEY, jellyfin_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, name TEXT, artist TEXT, album TEXT, path TEXT, mtime INTEGER, size INTEGER);
CREATE TABLE IF NOT EXISTS fields(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, display TEXT NOT NULL, hierarchy TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS vals(item_id INTEGER NOT NULL, field_id INTEGER NOT NULL, value TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_vals ON vals(field_id, value);
CREATE INDEX IF NOT EXISTS ix_vals_item ON vals(item_id);
CREATE INDEX IF NOT EXISTS ix_items_name ON items(name);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");

        AddColumn(c, "items", "name", "TEXT");
        AddColumn(c, "items", "artist", "TEXT");
        AddColumn(c, "items", "album", "TEXT");

        try
        {
            Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS ix_vals_uniq ON vals(item_id, field_id, value);");
        }
        catch
        {
            Exec(c, "DELETE FROM vals WHERE rowid NOT IN (SELECT MIN(rowid) FROM vals GROUP BY item_id, field_id, value);");
            Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS ix_vals_uniq ON vals(item_id, field_id, value);");
        }

        SetMeta("schema", "0.6");
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
        if (_shared is not null) { return UpsertItemInner(_shared, jellyfinId, kind, name, artist, album, path, mtime, size); }
        using var c = NewConn();
        using var tx = c.BeginTransaction();
        var id = UpsertItemInner(c, jellyfinId, kind, name, artist, album, path, mtime, size);
        tx.Commit();
        return id;
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

    public void ClearAll()
    {
        Write(c => Exec(c, "DELETE FROM vals; DELETE FROM items;"));
        _fieldIds.Clear();
    }

    /* ---------------- reads ---------------- */

    public (int Items, int Values) Counts()
        => Read(c =>
        {
            using var i = c.CreateCommand();
            i.CommandText = "SELECT COUNT(*) FROM items;";
            using var v = c.CreateCommand();
            v.CommandText = "SELECT COUNT(*) FROM vals;";
            return (Convert.ToInt32(i.ExecuteScalar(), CultureInfo.InvariantCulture),
                    Convert.ToInt32(v.ExecuteScalar(), CultureInfo.InvariantCulture));
        });

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

    private List<BrowseRow> Run(string where, List<(string K, object V)> args, int limit, int offset)
        => Read(c =>
        {
            var rows = new List<BrowseRow>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $@"
SELECT i.jellyfin_id, i.kind,
       COALESCE(i.name, i.path, i.jellyfin_id),
       COALESCE(i.artist, ''), COALESCE(i.album, ''), COALESCE(i.path, ''),
       f.name, v.value
FROM vals v
JOIN items i ON i.id = v.item_id
JOIN fields f ON f.id = v.field_id
WHERE {where}
ORDER BY COALESCE(i.artist,''), COALESCE(i.name, i.path), f.name, v.value
LIMIT $lim OFFSET $off;";
            foreach (var a in args) { cmd.Parameters.AddWithValue(a.K, a.V); }
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.Parameters.AddWithValue("$off", offset);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new BrowseRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
            }

            return rows;
        });

    private static (string Where, List<(string K, object V)> Args) TermWhere(string query)
    {
        var terms = (query ?? string.Empty)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Take(6)
            .ToArray();

        if (terms.Length == 0) { return ("1=1", new List<(string, object)>()); }

        var sb = new StringBuilder("(");
        var args = new List<(string K, object V)>();
        for (var i = 0; i < terms.Length; i++)
        {
            if (i > 0) { sb.Append(" AND "); }
            sb.Append("(COALESCE(i.name,'')||' '||COALESCE(i.path,'')||' '||COALESCE(i.artist,'')||' '||COALESCE(i.album,'')) LIKE $t").Append(i);
            args.Add(("$t" + i, "%" + terms[i] + "%"));
        }

        sb.Append(")");
        return (sb.ToString(), args);
    }

    public List<BrowseRow> BrowseField(string field, string? query, string? kind, int limit, int offset)
    {
        var (where, args) = TermWhere(string.Empty);
        var parts = new List<string> { where };
        if (!string.IsNullOrWhiteSpace(field))
        {
            parts.Add("f.name = $f");
            args.Add(("$f", field.ToUpperInvariant()));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add("v.value LIKE $like");
            args.Add(("$like", "%" + query + "%"));
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            parts.Add("i.kind = $k");
            args.Add(("$k", kind));
        }

        return Run(string.Join(" AND ", parts), args, limit, offset);
    }

    public List<BrowseRow> BrowseItem(string query, string? kind, int limit, int offset)
    {
        var (where, args) = TermWhere(query);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            where = where + " AND i.kind = $k";
            args.Add(("$k", kind));
        }

        return Run(where, args, limit, offset);
    }
}
