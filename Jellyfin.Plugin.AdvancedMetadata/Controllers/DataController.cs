using System;
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
            Schema = "0.5",
            SizeBytes = _store.SizeBytes,
            Items = counts.Items,
            Values = counts.Values,
            Mappings = Plugin.Instance?.Configuration.Mappings.Count ?? 0
        });
    }

    [HttpGet("Fields")]
    public ActionResult Fields()
        => Ok(_store.FieldSummary().Select(f => new { f.Name, f.Display, f.Hierarchy, f.Items, f.Values }));

    [HttpGet("Values")]
    public ActionResult Values([FromQuery] string? field, [FromQuery] string? q, [FromQuery] string? kind,
                               [FromQuery] int limit = 300, [FromQuery] int offset = 0)
        => Ok(_store.BrowseField(field ?? string.Empty, q, kind, Math.Clamp(limit, 1, 2000), Math.Max(0, offset))
            .Select(v => new { v.Id, v.Kind, v.Item, v.Artist, v.Album, v.Detail, v.Field, v.Value }));

    [HttpGet("Item")]
    public ActionResult Item([FromQuery] string name, [FromQuery] string? kind,
                             [FromQuery] int limit = 300, [FromQuery] int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) { return BadRequest("name required"); }
        return Ok(_store.BrowseItem(name, kind, Math.Clamp(limit, 1, 2000), Math.Max(0, offset))
            .Select(v => new { v.Id, v.Kind, v.Item, v.Artist, v.Album, v.Detail, v.Field, v.Value }));
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
