using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AdvancedMetadata.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AdvancedMetadata.Controllers;

[ApiController]
[Authorize]
[Route("AdvancedMetadataData")]
public class DataController : ControllerBase
{
    private readonly MetadataStore _store;

    public DataController(MetadataStore store)
    {
        _store = store;
    }

    [HttpGet("Info")]
    public ActionResult Info()
    {
        var counts = _store.Counts();
        return Ok(new
        {
            StorePath = _store.DbPath,
            Schema = "0.7",
            SizeBytes = _store.SizeBytes,
            Items = counts.Items,
            Values = counts.Values,
            Mappings = Plugin.Instance?.Configuration.Mappings.Count ?? 0
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
}
