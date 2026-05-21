# VT2 Mod Updater

One-click updater for Ensrick's Vermintide 2 mods. Pulls the latest pre-built bundles from
[`vermintide-2-tweaker` GitHub releases](https://github.com/Ensrick/vermintide-2-tweaker/releases)
and drops them straight into your Steam Workshop folder.

Built for the case where the Steam Workshop publish is lagging, the mod is friends-only and a
new friend doesn't have access, or you just want every mod synced to the absolute latest dev
build without waiting on Workshop propagation.

## Install

Grab `VT2ModUpdater.exe` from the [latest release](https://github.com/Ensrick/vt2-mod-updater/releases/latest)
and run it. No installer, no dependencies — single ~70 MB self-contained exe.

## How it works

1. On launch, the tool fetches the latest release manifest from
   `github.com/Ensrick/vermintide-2-tweaker/releases/latest`.
2. It compares the listed versions against what's already installed in your VT2 Workshop folder.
3. It shows you a table — Installed vs. Latest, with an Update button for each out-of-date mod
   and an "Update All" button.
4. Click Update — it downloads the per-mod zip, extracts over your Workshop folder, and writes a
   sidecar `vt2updater_version.txt` so the next run can detect what's installed.

The Steam Workshop folder is found automatically by reading
`steamapps/libraryfolders.vdf` and locating the library that owns Vermintide 2 (App ID 552500).

## What it doesn't do

- It does **not** subscribe you to mods on Steam. You still need to subscribe (or know someone who
  can share the Workshop link, for friends-only mods) before the tool can drop files into the
  mod's folder — Steam only creates the folder once you're subscribed.
- It does **not** upload anything. It's a download-only client.
- It does **not** modify your VT2 install or VMF launcher config.

## For Claude

See [CLAUDE.md](CLAUDE.md). The release pipeline that publishes the assets this tool consumes
lives in the [`vermintide-2-tweaker` repo](https://github.com/Ensrick/vermintide-2-tweaker) at
`tools/publish-release/publish-release.ps1`.

## Building

```powershell
cd src/VT2ModUpdater
dotnet build
# or for a distributable single-file exe:
.\publish.ps1
```

Targets `net9.0-windows`. WPF. .NET 9 SDK required.
