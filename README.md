# IMDb Smart Lookup for Jellyfin

A lightweight Jellyfin metadata provider plugin that automatically resolves **IMDb IDs** (`tt…`) during library scans—even when the year in the filename is off by 1–2 years (e.g. festival premiere vs. theatrical release) or localized titles are used.

## Why?

The built-in **TheMovieDb (TMDb)** and **OMDb** providers strictly filter by the exact year parsed from the filename. If a file is named `Hokum (2025).mkv` while TMDb/IMDb lists it as `2026`, Jellyfin returns zero matches until you manually look up and enter the IMDb ID in the *Identify* dialog.

**IMDb Smart Lookup** solves this automatically during the scan:
1. Skips any item that already has an IMDb ID (e.g. from `.nfo` or `[imdbid-tt…]`).
2. Queries the public IMDb suggestion endpoint (no API key required, supports localized titles / AKAs) with built-in rate limiting and short-lived caching.
3. Matches by media type (Movies / TV Series), title, and release year with configurable **year tolerance** (default: **±2 years**).
4. Sets **only the `Imdb` provider ID** on the item—leaving title, overview, cast, and artwork untouched so **TheMovieDb** immediately fetches full localized metadata via the resolved IMDb ID.
5. Also provides direct IMDb search results in Jellyfin's **Identify** dialog.

## Installation (Jellyfin Plugin Repository)

1. Open your Jellyfin **Dashboard** → **Plugins** → **Repositories**.
2. Click **+** (Add) and enter:
   - **Repository Name:** `IMDb Smart Lookup`
   - **Repository URL:**
     ```text
     https://raw.githubusercontent.com/manuelniehues/imdb-smart-lookup/main/manifest.json
     ```
3. Go to **Catalog**, search for **IMDb Smart Lookup**, install it, and restart Jellyfin.

## Recommended Library Configuration

For each **Movies** and **Shows** library (**Dashboard → Libraries → `…` → Manage Library → Metadata Downloaders**):

1. **IMDb Smart Lookup** *(first)*
2. **TheMovieDb** *(second — fetches full localized metadata & artwork using the resolved IMDb ID)*
3. **The Open Movie Database** *(optional, third)*

## Plugin Settings

Under **Dashboard → Plugins → IMDb Smart Lookup**:
- **Year Tolerance (± years):** Allowed difference between filename year and IMDb year (0–5, default: `2`).
- **Enable for Movies / TV Shows:** Toggle per media type.
- **Skip items that already have an IMDb ID:** Enabled by default so existing matches are never touched.

## Compatibility

- Targets **Jellyfin 12.x** (`targetAbi: 12.0.0.0`, `.NET 10.0`).

## Building from Source

```bash
./build.sh
```

Produces `dist/imdb-smart-lookup_1.0.0.0.zip` and prints its MD5 checksum for `manifest.json`.

## Disclaimer

This is an unofficial community plugin and is not affiliated with, endorsed by, or connected to IMDb or Amazon.
