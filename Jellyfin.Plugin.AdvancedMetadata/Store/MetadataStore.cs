using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.AdvancedMetadata.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AdvancedMetadata.Store;

/// <summary>Own SQLite index. Jellyfin itself is never written to.</summary>
public sealed class MetadataStore
{
    private readonly string _dbPath;

    public MetadataStore(IApplicationPaths paths)
    {
        var folder = Path.Combine(paths.DataPath, "AdvancedMetadata");
        Directory.CreateDirectory(folder);
        _dbPath = Path.Combine(folder, "index.db");
        EnsureSchema();
    }

    public string DatabasePath => _dbPath;

    public long SizeBytes => File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection("Data Source=" + _dbPath);
        c.Open();
        return c;
    }

    private void EnsureSchema()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS items(
  id INTEGER PRIMARY KEY,
  jellyfin_id TEXT NOT NULL UNIQUE,
  kind TEXT NOT NULL,
  path TEXT,
  mtime INTEGER DEFAULT 0,
  size INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS vals(
  item_id INTEGER NOT NULL,
  field TEXT NOT NULL,
  value TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_vals ON vals(field, value);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    public bool IsUnchanged(string jellyfinId, long mtime, long size)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT mtime, size FROM items WHERE jellyfin_id = $id";
        cmd.Parameters.AddWithValue("$id", jellyfinId);
        using var r = cmd.ExecuteReader();
        return r.Read() && r.GetInt64(0) == mtime && r.GetInt64(1) == size;
    }

    public long UpsertItem(string jellyfinId, string kind, string? path, long mtime, long size)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO items(jellyfin_id, kind, path, mtime, size) VALUES($id, $kind, $path, $mtime, $size)
ON CONFLICT(jellyfin_id) DO UPDATE SET kind=$kind, path=$path, mtime=$mtime, size=$size;
SELECT id FROM items WHERE jellyfin_id = $id;";
        cmd.Parameters.AddWithValue("$id", jellyfinId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mtime", mtime);
        cmd.Parameters.AddWithValue("$size", size);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void ReplaceFieldValues(long itemId, string field, IEnumerable<string> values)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM vals WHERE item_id = $id AND field = $f";
            del.Parameters.AddWithValue("$id", itemId);
            del.Parameters.AddWithValue("$f", field);
            del.ExecuteNonQuery();
        }
        foreach (var v in values)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO vals(item_id, field, value) VALUES($id, $f, $v)";
            ins.Parameters.AddWithValue("$id", itemId);
            ins.Parameters.AddWithValue("$f", field);
            ins.Parameters.AddWithValue("$v", v);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ClearAll()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vals; DELETE FROM items;";
        cmd.ExecuteNonQuery();
    }
}
