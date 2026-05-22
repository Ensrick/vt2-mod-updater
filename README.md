# VT2 Mod Updater

One-click updater for Ensrick's Vermintide 2 mods. Pulls the latest pre-built bundles from
[`vermintide-2-tweaker` GitHub releases](https://github.com/Ensrick/vermintide-2-tweaker/releases)
and drops them into a local-only mods folder that Steam can't touch.

Built for the case where the Steam Workshop publish is lagging, the mod is friends-only and a
new friend doesn't have access, or you want every mod synced to the absolute latest dev build
without waiting on Workshop propagation.

## Install

Grab `VT2ModUpdater.exe` from the [latest release](https://github.com/Ensrick/vt2-mod-updater/releases/latest)
and run it. No installer, no dependencies — single ~70 MB self-contained exe.

## How it works

1. On launch, the tool fetches the latest release manifest from
   `github.com/Ensrick/vermintide-2-tweaker/releases/latest`.
2. It compares the listed versions against what's already installed locally and shows
   Installed vs Latest per mod.
3. Click Update — it downloads the per-mod zip and extracts it to
   `steamapps/workshop/content/552500/10<workshop_id>/`.
   - The leading `10` is intentional. It's a synthetic ID — outside the Steam-managed
     Workshop range — so Steam can't revert or wipe the folder the way it would for a real
     subscribed Workshop item. VMF still scans every numeric folder under `552500/`,
     so the mod loads normally.
4. A sidecar `vt2updater_version.txt` lets subsequent runs detect what's installed.

The Steam Workshop content path is found automatically by reading
`steamapps/libraryfolders.vdf` and locating the library that owns Vermintide 2 (App ID 552500).

## Double-load warning

If you're **also subscribed** to a mod on Steam Workshop, the tool will flag it. Both copies
(Steam's and the updater's) will load and VMF will probably complain about duplicate mod IDs.
To avoid this, unsubscribe from the affected mods on Workshop. The updater handles the rest.

## What it doesn't do

- It does **not** subscribe to or unsubscribe from Workshop items. That's manual.
- It does **not** upload anything. Download-only client.
- It does **not** modify your VT2 install, VMF launcher config, or any file under your VT2
  install root. Only writes to `steamapps/workshop/content/552500/10*/`.

## Version history

- **v0.2.0** (2026-05-21) — Deploys to a synthetic local-only ID (`10<workshop_id>`) so
  Steam can't wipe the folder. Replaces the broken v0.1.0 approach.
- **v0.1.0** (2026-05-21) — Initial release. Wrote to the real Workshop ID folder. **Don't
  use** — Steam reverts/deletes the folder on its next sync.

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
