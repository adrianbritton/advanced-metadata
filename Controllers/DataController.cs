using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AdvancedMetadata.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AdvancedMetadata.Controllers;

/// <summary>Read-only window onto the index, plus maintenance actions.</summary>
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
            Schema = "0.3",
            SizeBytes = _store.SizeBytes,
            Items = counts.Items,
            Values = counts.Values,
            Mappings = Plugin.Instance?.Configuration.Mappings.Count ?? 0
        });
    }

    [HttpGet("Fields")]
    public ActionResult Fields()
        => Ok(_store.FieldSummary().Select(f => new
        {
            f.Name,
            f.Display,
            f.Hierarchy,
            f.Items,
            f.Values
        }));

    [HttpGet("Values")]
    public ActionResult Values([FromQuery] string field, [FromQuery] string? q, [FromQuery] int limit = 300)
        => Ok(_store.BrowseField(field, q, Math.Clamp(limit, 1, 2000))
            .Select(v => new { v.Kind, v.Item, v.Field, v.Value }));

    [HttpGet("Item")]
    public ActionResult Item([FromQuery] string name, [FromQuery] int limit = 300)
    {
        if (string.IsNullOrWhiteSpace(name)) { return BadRequest("name required"); }
        return Ok(_store.BrowseItem(name, Math.Clamp(limit, 1, 2000))
            .Select(v => new { v.Kind, v.Item, v.Field, v.Value }));
    }

    /// <summary>Deletes the indexed values for one mapping. Returns how many rows were removed.</summary>
    [HttpPost("DeleteFieldValues")]
    public ActionResult DeleteFieldValues([FromQuery] string field)
    {
        if (string.IsNullOrWhiteSpace(field)) { return BadRequest("field required"); }
        var removed = _store.ClearField(field);
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
