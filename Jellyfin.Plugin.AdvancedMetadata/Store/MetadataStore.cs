using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.AdvancedMetadata.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AdvancedMetadata.Store;

public record FieldRow(string Name, string Display, string Hierarchy, int Items, int Values);

public record BrowseRow(string Kind, string Item, string Field, string Value);

public class MetadataStore
{
    private readonly string _dbPath;

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

    private SqliteConnection Open()
    {
        var c = new SqliteConnection("Data Source=" + _dbPath);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS items(id INTEGER PRIMARY KEY, jellyfin_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, name TEXT, path TEXT, mtime INTEGER, size INTEGER);
CREATE TABLE IF NOT EXISTS fields(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, display TEXT NOT NULL, hierarchy TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS vals(item_id INTEGER NOT NULL, field_id INTEGER NOT NULL, value TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_vals ON vals(field_id, value);
CREATE INDEX IF NOT EXISTS ix_vals_item ON vals(item_id);
CREATE INDEX IF NOT EXISTS ix_items_name ON items(name);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();

        // upgrade path for databases created before the name column existed
        try
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE items ADD COLUMN name TEXT;";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // column already present
        }

        SetMeta("schema", "0.3");
    }

    public void SetMeta(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public bool IsUnchanged(string jellyfinId, long mtime, long size)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT mtime, size FROM items WHERE jellyfin_id=$j;";
        cmd.Parameters.AddWithValue("$j", jellyfinId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { return false; }
        return r.GetInt64(0) == mtime && r.GetInt64(1) == size;
    }

    public void SyncMappings(IEnumerable<Mapping> mappings)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var m in mappings)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO fields(name, display, hierarchy) VALUES ($n,$d,$h)
                                ON CONFLICT(name) DO UPDATE SET display=$d, hierarchy=$h;";
            cmd.Parameters.AddWithValue("$n", m.SourceTag.ToUpperInvariant());
            cmd.Parameters.AddWithValue("$d", m.DisplayName);
            cmd.Parameters.AddWithValue("$h", m.Hierarchy);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public long UpsertItem(string jellyfinId, string kind, string? name, string? path, long mtime, long size)
    {
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO items(jellyfin_id, kind, name, path, mtime, size) VALUES ($j,$k,$n,$p,$m,$s)
                                ON CONFLICT(jellyfin_id) DO UPDATE SET kind=$k, name=$n, path=$p, mtime=$m, size=$s;";
            cmd.Parameters.AddWithValue("$j", jellyfinId);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
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

    private static long FieldId(SqliteConnection c, string field)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM fields WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", field.ToUpperInvariant());
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? 0 : (long)r;
    }

    public void ReplaceFieldValues(long itemId, string field, IEnumerable<string> values)
    {
        using var c = Open();
        var fid = FieldId(c, field);
        if (fid == 0) { return; }

        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM vals WHERE item_id=$i AND field_id=$f;";
            del.Parameters.AddWithValue("$i", itemId);
            del.Parameters.AddWithValue("$f", fid);
            del.ExecuteNonQuery();
        }

        AddValuesInternal(c, itemId, fid, values);
    }

    public void AddValues(long itemId, string field, IEnumerable<string> values)
    {
        using var c = Open();
        var fid = FieldId(c, field);
        if (fid == 0) { return; }
        AddValuesInternal(c, itemId, fid, values);
    }

    private static void AddValuesInternal(SqliteConnection c, long itemId, long fieldId, IEnumerable<string> values)
    {
        using var tx = c.BeginTransaction();
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) { continue; }
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO vals(item_id, field_id, value)
                                SELECT $i,$f,$v
                                WHERE NOT EXISTS (SELECT 1 FROM vals WHERE item_id=$i AND field_id=$f AND value=$v);";
            cmd.Parameters.AddWithValue("$i", itemId);
            cmd.Parameters.AddWithValue("$f", fieldId);
            cmd.Parameters.AddWithValue("$v", v.Trim());
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Removes all indexed values for one field. Returns how many rows went.</summary>
    public int ClearField(string field)
    {
        using var c = Open();
        var fid = FieldId(c, field);
        if (fid == 0) { return 0; }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vals WHERE field_id=$f;";
        cmd.Parameters.AddWithValue("$f", fid);
        return cmd.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vals; DELETE FROM items;";
        cmd.ExecuteNonQuery();
    }

    public (int Items, int Values) Counts()
    {
        using var c = Open();
        using var i = c.CreateCommand();
        i.CommandText = "SELECT COUNT(*) FROM items;";
        using var v = c.CreateCommand();
        v.CommandText = "SELECT COUNT(*) FROM vals;";
        return (Convert.ToInt32(i.ExecuteScalar(), CultureInfo.InvariantCulture),
                Convert.ToInt32(v.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    /// <summary>Per-mapping counts, for the Browse Index tab.</summary>
    public List<FieldRow> FieldSummary()
    {
        var rows = new List<FieldRow>();
        using var c = Open();
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
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                Convert.ToInt32(r.GetInt64(3), CultureInfo.InvariantCulture),
                Convert.ToInt32(r.GetInt64(4), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>Browse one mapping's indexed values, optionally filtered by value text.</summary>
    public List<BrowseRow> BrowseField(string field, string? query, int limit)
    {
        var rows = new List<BrowseRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT i.kind, COALESCE(i.name, i.path, i.jellyfin_id), f.name, v.value
FROM vals v
JOIN items i ON i.id = v.item_id
JOIN fields f ON f.id = v.field_id
WHERE f.name = $f AND ($q = '' OR v.value LIKE $like)
ORDER BY i.name, v.value
LIMIT $lim;";
        cmd.Parameters.AddWithValue("$f", field.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$q", query ?? string.Empty);
        cmd.Parameters.AddWithValue("$like", "%" + (query ?? string.Empty) + "%");
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new BrowseRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        return rows;
    }

    /// <summary>All indexed values for items whose name matches — the "check one track" view.</summary>
    public List<BrowseRow> BrowseItem(string name, int limit)
    {
        var rows = new List<BrowseRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT i.kind, COALESCE(i.name, i.path, i.jellyfin_id), f.name, v.value
FROM vals v
JOIN items i ON i.id = v.item_id
JOIN fields f ON f.id = v.field_id
WHERE i.name LIKE $like
ORDER BY i.name, f.name, v.value
LIMIT $lim;";
        cmd.Parameters.AddWithValue("$like", "%" + name + "%");
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new BrowseRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        return rows;
    }
}
