using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.AdvancedMetadata.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AdvancedMetadata.Store;

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

    /// <summary>Full path to the index database.</summary>
    public string DbPath => _dbPath;

    /// <summary>Alias kept for readability in log output.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>Size of the index database on disk, in bytes. Returns -1 if absent.</summary>
    public long SizeBytes
    {
        get
        {
            try
            {
                var fi = new FileInfo(_dbPath);
                return fi.Exists ? fi.Length : -1;
            }
            catch
            {
                return -1;
            }
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
CREATE TABLE IF NOT EXISTS items(id INTEGER PRIMARY KEY, jellyfin_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, path TEXT, mtime INTEGER, size INTEGER);
CREATE TABLE IF NOT EXISTS fields(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, display TEXT NOT NULL, hierarchy TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS vals(item_id INTEGER NOT NULL, field_id INTEGER NOT NULL, value TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_vals ON vals(field_id, value);
CREATE INDEX IF NOT EXISTS ix_vals_item ON vals(item_id);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        SetMeta("schema", "0.2");
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

    /// <summary>True when the file has not changed since it was last indexed.</summary>
    public bool IsUnchanged(string jellyfinId, long mtime, long size)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT mtime, size FROM items WHERE jellyfin_id=$j;";
        cmd.Parameters.AddWithValue("$j", jellyfinId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
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

    public long UpsertItem(string jellyfinId, string kind, string? path, long mtime, long size)
    {
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO items(jellyfin_id, kind, path, mtime, size) VALUES ($j,$k,$p,$m,$s)
                                ON CONFLICT(jellyfin_id) DO UPDATE SET kind=$k, path=$p, mtime=$m, size=$s;";
            cmd.Parameters.AddWithValue("$j", jellyfinId);
            cmd.Parameters.AddWithValue("$k", kind);
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

    /// <summary>Clear then set — used for the song that owns the tag.</summary>
    public void ReplaceFieldValues(long itemId, string field, IEnumerable<string> values)
    {
        using var c = Open();
        var fid = FieldId(c, field);
        if (fid == 0) return;

        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM vals WHERE item_id=$i AND field_id=$f;";
            del.Parameters.AddWithValue("$i", itemId);
            del.Parameters.AddWithValue("$f", fid);
            del.ExecuteNonQuery();
        }

        AddValuesInternal(c, itemId, fid, values);
    }

    /// <summary>Accumulate — used for album/artist roll-up targets.</summary>
    public void AddValues(long itemId, string field, IEnumerable<string> values)
    {
        using var c = Open();
        var fid = FieldId(c, field);
        if (fid == 0) return;
        AddValuesInternal(c, itemId, fid, values);
    }

    private static void AddValuesInternal(SqliteConnection c, long itemId, long fieldId, IEnumerable<string> values)
    {
        using var tx = c.BeginTransaction();
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
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

    public void ClearField(string field)
    {
        using var c = Open();
        var fid = FieldId(c, field);
        if (fid == 0) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vals WHERE field_id=$f;";
        cmd.Parameters.AddWithValue("$f", fid);
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

    public void ClearAll()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vals; DELETE FROM items;";
        cmd.ExecuteNonQuery();
    }
}
