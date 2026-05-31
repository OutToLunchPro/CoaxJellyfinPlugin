# Releasing

Releases are tag-driven. The git tag is the single source of truth for the version —
CI syncs `meta.json` and the csproj to match, so you never hand-edit version numbers.

## Cut a release

```bash
git checkout main && git pull
git tag -a v1.0.1.0 -m "Short changelog line shown in the dashboard"
git push origin v1.0.1.0
```

That's it. The `release` workflow (`.github/workflows/release.yml`) then:

1. Syncs `Jellyfin.Plugin.Coax/meta.json` + csproj to the tag version.
2. Runs the tests.
3. Builds and packages `dist/Coax_<version>.zip`.
4. Creates the GitHub release `v<version>` and uploads the zip as an asset.
5. Regenerates the root `manifest.json` (new version prepended, history kept) and
   commits it back to `main`.

The annotated tag's message (`-m "..."`) becomes the `changelog` field in `manifest.json`.

## Versioning

Use the four-part `MAJOR.MINOR.PATCH.BUILD` form Jellyfin expects (e.g. `1.0.1.0`).
Jellyfin offers an in-app update only when the manifest advertises a **higher** version
than the one installed, so always bump.

## Manual run

You can also trigger from the Actions tab → **release** → **Run workflow**, entering the
version by hand. This creates the tag at the current tip of `main`.

## Notes

- Tag the **tip of `main`** — CI builds and commits the manifest against `main`.
- Clients subscribe to the raw `manifest.json` URL on `main` under
  Dashboard → Plugins → Repositories, so the commit-back in step 5 is what makes the
  update visible.
- Local `./build.sh` still works for ad-hoc bundles; it writes a scratch
  `dist/manifest.json` and never touches the committed one.
