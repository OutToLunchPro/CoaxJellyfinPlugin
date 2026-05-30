# Jellyfin.Plugin.Coax

A minimal, **stateless** Jellyfin plugin that exposes the person→items inverse that vanilla
Jellyfin can't produce cheaply, so the [Coax](https://coaxtheapp.com) client can build **Actor / Director channels** for both movie and TV libraries.

The plugin stores nothing and knows nothing about channels or scheduling. It runs the DB joins vanilla can't and returns raw associations plus item metadata; all scheduling stays on the client.

## Endpoints

### `GET /coax/info`

Capability probe (standard Jellyfin auth).

```json
{ "pluginVersion": "1.0.0", "contractVersion": 1,
  "capabilities": ["index.people", "index.studios", "index.items"] }
```

### `POST /coax/index`

The data call (`Authorization: MediaBrowser Token=…`). Request shapes data only — never scheduling. See the contract for the full request/response shape.

```jsonc
{
  "contractVersion": 1,
  "libraryIds": ["<jf library id>"],
  "itemTypes": ["Movie", "Episode"],
  "include": ["items", "people"],
  "filters": { "maxOfficialRating": "PG-13", "watched": "all", "userId": null },
  "shaping": { "minItemsPerPerson": 5, "maxItems": 20000, "maxEpisodesPerSeries": 50 }
}
```

The response carries the full filtered library inline (`items`) and the person→item-id inverse (`people`, the reason the plugin exists). `itemIds` always point at schedulable items (Movie or Episode ids), never Series ids. Collection memberships are intentionally **not** returned — the Coax client already prefetches box sets cheaply on its own.

#### TV semantics

Cast attaches at two levels and the actor inverse unions both: **series-level** main/recurring cast (applied to all returned episodes of that series) ∪ **episode-level** guest stars (applied to just that episode). `GuestStar` folds into `Actor`. **Directors** are read per-episode and never inherited from the series.

## Build

Targets **net9.0** against the Jellyfin 10.11 ABI (built with the .NET 9 or 10 SDK).

```sh
./build.sh
```

`build.sh` compiles in Release and refreshes the drop-in bundle at `dist/Coax_1.0.0.0/` (`Jellyfin.Plugin.Coax.dll` + `meta.json`). Drop that folder into the `plugins/` directory under your Jellyfin data directory and restart the server.

It also emits `dist/Coax_<version>.zip` and `dist/manifest.json` for installing via a
repository instead of side-loading (see below).

## Install from the GitHub repository (recommended)

The repo publishes a `manifest.json` at its root whose `sourceUrl` points at the plugin zip
attached to the matching GitHub release. To install (and get in-app updates) without building
or hosting anything yourself:

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → +**.
2. Add a repository with any name and this URL:

   ```
   https://raw.githubusercontent.com/OutToLunchPro/CoaxJellyfinPlugin/main/manifest.json
   ```

3. Go to **Dashboard → Plugins → Catalog**, find **Coax** under *General*, and install it.
4. Restart Jellyfin when prompted.

This also clears the *"An error occurred while getting the plugin details from the
repository"* message that side-loaded copies show, since the GUID now resolves to a known repo.

## Install via your own repository (optional)

If you'd rather self-host the manifest and zip, point the build at your base URL:

```sh
COAX_SOURCE_BASE_URL=https://my.host/jellyfin ./build.sh
```

`sourceUrl` in `manifest.json` is built as `$COAX_SOURCE_BASE_URL/Coax_<version>.zip`
(defaults to a `CHANGE_ME` placeholder if unset). Host `dist/Coax_<version>.zip` and
`dist/manifest.json` at that base URL, then add the `manifest.json` URL under
**Dashboard → Plugins → Repositories**. The `checksum` is the zip's MD5 and must match, or
installs fail.

## Scope (v1)

* In: the stateless `/coax/info` + `/coax/index` data endpoints for Movies and Episodes.
* Out: schedule generation, lineup storage, WebSocket events, Live TV, transcoding, Emby.  
