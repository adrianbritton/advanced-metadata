# Advanced Metadata

A Jellyfin plugin that builds a searchable index of custom audio metadata
(ID3 / Vorbis tags) for the companion Advanced Search plugin.

**Jellyfin is never modified.** The plugin reads your files and Jellyfin's
metadata, and writes only to its own store.

## Status

Milestone A-M1 — plugin shell, Create and Manage mappings.

## Install (manual, for testing)

1. Download `advanced-metadata.zip` from the latest build.
2. Unzip into your Jellyfin `plugins/AdvancedMetadata/` folder.
3. Restart Jellyfin.
4. Dashboard → Plugins → Advanced Metadata.

## Licence

MIT.
