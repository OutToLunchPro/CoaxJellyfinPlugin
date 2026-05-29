# Jellyfin.Plugin.Coax

A minimal, **stateless** Jellyfin plugin that exposes the person→items inverse that vanilla
Jellyfin can't produce cheaply, so the Coax client can build **Actor / Director channels** for
both movie and TV libraries.

The plugin stores nothing and knows nothing about channels or scheduling. It runs the DB
joins vanilla can't and returns raw associations plus item metadata; all scheduling stays on
the client.

## Endpoints

### `GET /coax/info`

Capability probe (standard Jellyfin auth).

```json
{ "pluginVersion": "1.0.0", "contractVersion": 1,
  "capabilities": ["index.people", "index.studios", "index.items"] }
```

### `POST /coax/index`

The data call (`Authorization: MediaBrowser Token=…`). Request shapes data only — never
scheduling. See the contract for the full request/response shape.

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

The response carries the full filtered library inline (`items`) and the person→item-id inverse
(`people`, the reason the plugin exists). `itemIds` always point at schedulable items (Movie or
Episode ids), never Series ids. Collection memberships are intentionally **not** returned — the
Coax client already prefetches box sets cheaply on its own.

#### TV semantics

Cast attaches at two levels and the actor inverse unions both: **series-level** main/recurring
cast (applied to all returned episodes of that series) ∪ **episode-level** guest stars
(applied to just that episode). `GuestStar` folds into `Actor`. **Directors** are read
per-episode and never inherited from the series.

## Build

Targets **net9.0** against the Jellyfin 10.11 ABI (built with the .NET 9 or 10 SDK).

```sh
./build.sh
```

`build.sh` compiles in Release and refreshes the drop-in bundle at `dist/Coax_1.0.0.0/`
(`Jellyfin.Plugin.Coax.dll` + `meta.json`). Drop that folder into the `plugins/` directory under
your Jellyfin data directory and restart the server.

## Scope (v1)

In: the stateless `/coax/info` + `/coax/index` data endpoints for Movies and Episodes.
Out: schedule generation, lineup storage, WebSocket events, Live TV, transcoding, Emby.
