# CLAUDE.md — vt2-mod-updater

Single-purpose Windows tool that downloads the latest VT2 mod bundles from the `vermintide-2-tweaker`
GitHub release and deploys them into the user's Steam Workshop content folder.

## What this is for

The user (Ensrick) ships ~17 VT2 mods. Most are friends-only on Workshop. Some friends never get
auto-updates because Steam's friends-only Workshop sync is unreliable, and new friends often
don't have visibility into the friends-only items at all. The tool short-circuits Workshop entirely:
read GitHub releases, copy bundles into the Workshop folder, done.

This is the **consumer** side. The **producer** side — the script that builds + publishes the
release assets — lives in the `vermintide-2-tweaker` repo at `tools/publish-release/publish-release.ps1`.

## Architecture

- `src/VT2ModUpdater/VT2ModUpdater.csproj` — WPF, `net9.0-windows`, single-file self-contained
  Release build (~70 MB exe).
- `Models/` — Plain DTOs: `ReleaseManifest` (top-level), `ManifestEntry` (one row), `ModRow`
  (UI binding model that pairs a manifest entry with its installed version + update state).
- `Services/`
  - `GitHubReleaseClient` — `GET /repos/{owner}/{repo}/releases/latest`, downloads the
    `manifest.json` asset, downloads per-mod zip assets.
  - `SteamPaths` — reads registry for Steam install, parses `libraryfolders.vdf` to find the
    library that owns App 552500, returns the workshop content path.
  - `Deployer` — extracts a zip into `<workshop>/552500/<workshop_id>/` (overwrite), writes
    `vt2updater_version.txt`.
- `ViewModels/MainViewModel.cs` — orchestration. Async fetch → populate rows → enable buttons.

## Release manifest schema

The `manifest.json` asset on the `vermintide-2-tweaker` release looks like:

```json
{
  "release_tag": "mods-2026-05-21",
  "published_at": "2026-05-21T18:00:00Z",
  "mods": [
    {
      "mod_id": "ct",
      "friendly_name": "Chaos Wastes Tweaker",
      "workshop_id": "3712929235",
      "version": "0.7.80-alpha",
      "asset_filename": "ct.zip",
      "visibility": "public"
    }
  ]
}
```

Each `asset_filename` corresponds to a sibling release asset whose contents are the mod's
`bundleV2/` directory plus a `vt2updater_version.txt` containing the version string.

## Installed version detection

After deploy, the tool drops `vt2updater_version.txt` into the workshop folder alongside
`mod.bin` / `mod.cer`. On subsequent runs, the file's contents are compared against the
manifest version to determine "up to date" / "out of date" / "not installed".

Steam's own workshop syncs may overwrite `mod.bin`/`mod.cer`, but won't touch the sidecar
since it's not part of any Workshop publish. The sidecar therefore reports what the
**updater** last deployed, which is the right thing for "did I run this tool against the
current release."

## What this tool deliberately doesn't do

- **Doesn't subscribe to Workshop items.** Steam wipes folders for non-subscribed items
  eventually. The tool surfaces "Not installed — subscribe on Workshop first" as a row state.
- **Doesn't build mods.** That's `VMBLauncher.exe` in the tweaker repo.
- **Doesn't talk to ugc_tool.** Upload pipeline lives entirely in the tweaker repo.
- **Doesn't auto-launch the game.** Per user feedback, never start VT2 without explicit
  permission.

## Building

```powershell
cd src/VT2ModUpdater
dotnet build                # debug build
.\publish.ps1               # single-file Release exe in bin/Release/.../publish/
```

## When you're updating this tool

- Keep the manifest schema in `Models/ReleaseManifest.cs` synchronized with the publish
  script's emit format in `vermintide-2-tweaker/tools/publish-release/publish-release.ps1`.
  If you add a field on either side, add it on both.
- The Steam library resolver in `Services/SteamPaths.cs` parses the VDF format with a
  regex. Real-world libraryfolders.vdf has nested brace blocks and quoted paths — if you
  generalize the parser, keep the existing tests green.
- WPF MVVM: hand-rolled `ObservableObject` + `RelayCommand`, no `CommunityToolkit.Mvvm`
  dependency. Don't pull one in for a 200-line view model.
