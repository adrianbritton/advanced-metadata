using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.AdvancedMetadata.Scanning;

public static class TagReader
{
    public static List<string> ReadValues(string path, string tagName, char[] delimiters)
    {
        var raw = new List<string>();
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            switch (tagName.Trim().ToUpperInvariant())
            {
                case "TITLE": Add(raw, tag.Title); break;
                case "ALBUM": Add(raw, tag.Album); break;
                case "ARTIST": raw.AddRange(tag.Performers); break;
                case "ALBUMARTIST": raw.AddRange(tag.AlbumArtists); break;
                case "GENRE": raw.AddRange(tag.Genres); break;
                case "COMPOSER": raw.AddRange(tag.Composers); break;
                case "COMMENT": Add(raw, tag.Comment); break;
                case "YEAR": if (tag.Year > 0) { Add(raw, tag.Year.ToString(CultureInfo.InvariantCulture)); } break;
                default: ReadCustom(file, tagName, raw); break;
            }
        }
        catch
        {
            // unreadable file: treat as no values
        }

        var split = new List<string>();
        foreach (var v in raw.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (delimiters.Length == 0) { split.Add(v.Trim()); continue; }
            split.AddRange(v.Split(delimiters, StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim()).Where(p => p.Length > 0));
        }
        return split.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Add(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) { list.Add(value); }
    }

    private static void ReadCustom(TagLib.File file, string tagName, List<string> raw)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames())
            {
                if (frame is TagLib.Id3v2.UserTextInformationFrame u
                    && string.Equals(u.Description, tagName, StringComparison.OrdinalIgnoreCase))
                {
                    raw.AddRange(u.Text);
                }
            }
        }
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            var values = xiph.GetField(tagName);
            if (values != null) { raw.AddRange(values); }
        }
    }
}
