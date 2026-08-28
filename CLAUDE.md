# CLAUDE.md — vt2-mod-updater

Single-purpose Windows tool that downloads the latest VT2 mod bundles from the `vermintide-2-tweaker`
GitHub release and deploys them into a **synthetic-ID** folder under Steam's Workshop content
root that Steam can't reconcile or wipe.

## What this is for

The user (Ensrick) ships ~17 VT2 mods. Most are friends-only on Workshop. Some friends never
get auto-updates because Steam's friends-only Workshop sync is unreliable, and new friends
often don't have visibility into the friends-only items at all. The tool short-circuits
Workshop entirely: read GitHub releases, copy bundles into a local-only folder under
`552500/`, done.

This is the **consumer** side. The **producer** side — the script that builds + publishes the
release assets — lives in the `vermintide-2-tweaker` repo at `tools/publish-release/publish-release.ps1`.

## Architecture

- `src/VT2ModUpdater/VT2ModUpdater.csproj` — WPF, `net9.0-windows`, single-file self-contained
  Release build (~70 MB exe).
- `Models/ReleaseManifest.cs` + `ManifestEntry` — schema mirror of what `publish-release.ps1`
  emits in the tweaker repo. Coupled — change both or neither.
- `Services/`
  - `GitHubReleaseClient` — `GET /repos/{owner}/{repo}/releases/latest`, downloads the
    `manifest.json` asset, downloads per-mod zip assets.
  - `SteamPaths` — reads registry for Steam install, parses `libraryfolders.vdf` to find
    the library that owns App 552500, returns the workshop content path.
  - `Deployer` — computes synthetic ID, verifies the release hash, creates
    `<workshop>/<synthetic_id>/` if missing, extracts zip there, and writes version plus
    installed-content integrity sidecars.
  - `RecoveryHistoryResolver` — disabled, filesystem-free source-exact historical lookup.
  - `SourceExactZipStager` — disabled recovery-only primitive which streams one resolved
    numeric archive into a private same-volume sibling, verifies the exact flat output set,
    and never installs or replaces the intended target. Do not wire it to `Deployer`; the
    later journaled directory-transaction phase owns replacement.
- `ViewModels/MainViewModel.cs` — orchestration. Async fetch → populate rows → enable buttons.

## Why synthetic IDs — the v0.1.0 → v0.2.0 lesson

v0.1.0 wrote into the real Workshop folder `<workshop>/<real_id>/`. That's a Steam-managed
directory: Steam tracks expected file contents, and on its next sync (auto verify, restart,
manual integrity check) it reconciles by either reverting the folder to the Workshop-published
version or **deleting it entirely** if it thinks the user is unsubscribed. A friend's
career_tweaker folder got wiped this way after a v0.1.0 update — they recovered via
unsubscribe + resubscribe on Workshop.

v0.2.0 deploys to `<workshop>/<"10" + real_id>/` instead — a folder Steam has no record
of and can't reconcile. VMF doesn't validate folder names; it loads any
`<workshop>/<id>/<mod>.mod`, so the synthetic folder is picked up normally. The user already
uses synthetic IDs in the 9000000XXX range for local-mod dev (e.g. `9000000003/gt.mod`),
confirming the pattern works.

Mapping is deterministic: `synthetic = "10" + real_workshop_id`. e.g. ct's real ID
`3712929235` → synthetic `103712929235`. 12-digit synthetic IDs are well above the current
real Workshop ID range (~3.7B), so they won't collide.

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
      "sha256": "228ed038b0a243256121c52df7ed67dcb85479b3039c261099a4f3e191d38e08",
      "visibility": "public"
    }
  ]
}
```

Each `asset_filename` is a sibling release asset whose contents are the mod's `bundleV2/`
directory plus a `vt2updater_version.txt` (the publish script writes the latter; the tool
also writes a fresh one after every deploy).

## Bundle integrity verification (Issue #30)

After downloading a zip, the consumer hashes its bytes (`SHA256.HashData(...)` →
lowercase hex via `Deployer.ComputeSha256Hex`) and compares the result against the
manifest entry's `sha256` field. The logic lives in `Deployer.VerifyBundleIntegrity`
and the retry loop is `MainViewModel.DownloadAndVerifyAsync`. Behaviour matrix:

| Manifest `sha256`          | Verification result            | Action                                                                              |
| -------------------------- | ------------------------------ | ----------------------------------------------------------------------------------- |
| Matches computed hash      | `Matched`                      | Proceed to extract.                                                                 |
| Differs from computed hash | `Mismatch`                     | Refuse to extract. Retry the download once; if still mismatched, MessageBox + skip. |
| Null / empty / whitespace  | `SkippedNoExpectedHash`        | Older manifest predating Issue #30 — debug-log and proceed (backwards compat).      |
| Not 64-character hex       | `MalformedExpected`            | Producer/schema bug — fail closed and do not extract unverified bytes.               |

Why the retry: GitHub's CDN occasionally serves a partial response and a clean re-fetch
usually resolves transient corruption. After two consecutive mismatches the install
stays on its previous version and the user is told to re-run the updater later.

Tamper test coverage lives in `tests/VT2ModUpdater.Tests/IntegrityTests.cs` —
`TamperTest_OneBitFlipDetected_E2E` confirms a one-byte mutation in the zip is
caught and refused.

## Installed version detection

The tool reads `<workshop>/10<real_id>/vt2updater_version.txt`. If present, that's the
installed version; otherwise treated as "Not installed". The sidecar is safe to live in
the synthetic folder because Steam doesn't manage that folder.

## Post-install verification (Issue #32)

The "Verify Installed Bundles" button in the bottom row classifies every installed mod
into one of five states without re-downloading anything:

| State | Meaning |
| ----- | ------- |
| `OK` | Sidecar present, installed-files hash matches sidecar, manifest hash matches latest. |
| `OUT_OF_DATE` | Sidecar untouched, but the latest manifest's `sha256` has moved on. |
| `TAMPERED` | Sidecar present but installed-files hash no longer matches what was stashed. |
| `NO_SIDECAR` | Folder exists but no `.vt2updater_sha256.txt` — legacy install pre-dating #32. |
| `NOT_INSTALLED` | Synthetic folder is missing. |

At deploy time `Deployer.DeployZipBytes` now writes a second sidecar
`.vt2updater_sha256.txt` next to `vt2updater_version.txt`, containing two key=value
lines:

```
manifest_sha256=<lowercase-hex zip hash from manifest, or empty on older manifests>
installed_files_sha256=<lowercase-hex Merkle-style hash of extracted file contents>
```

The Merkle hash (`Deployer.ComputeInstalledFilesHash`) sorts the folder's files by
filename, then feeds `filename_len_le32 || filename_utf8 || content_len_le64 ||
content_bytes` for each file into a running SHA-256. We can't byte-compare against the
original zip — re-zipping a directory never byte-matches `Compress-Archive`'s output —
so we hash the extracted layout and stash that instead.

`Deployer.VerifyInstalled(root, real_workshop_id, latest_manifest_sha256)` returns an
`InstalledVerification` record. The view-model surfaces per-row results via
`ModRow.VerifyState` (drives `StateLabel` + `StatusColor`) and writes per-category
counts into the status bar. TAMPERED never auto-re-downloads — the user might have
intentionally modified files; we surface a MessageBox and let them click Update.

Coverage lives in `tests/VT2ModUpdater.Tests/InstalledVerificationTests.cs` —
`TamperedInstalled_OneByteFlip_ReturnsTampered` is the load-bearing test.

## Double-load with real Workshop subscriptions

If the user is subscribed to a mod on Workshop AND the updater has deployed the synthetic
copy, both will load. VMF will warn or error on duplicate mod IDs. The UI flags this per
row ("also subscribed on Workshop — unsubscribe to avoid double-load"). The tool deliberately
does NOT delete the real folder to fix this — that's the kind of destructive op that caused
the v0.1.0 incident. User unsubscribes manually.

## What this tool deliberately doesn't do

- **Doesn't subscribe to or unsubscribe from Workshop items.** Friend handles their own state.
- **Doesn't touch the real Workshop folder.** Ever. Only writes to `<workshop>/10*/`.
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
  regex. Stress-tested against multi-library + edge cases on 2026-05-21.
- WPF MVVM: hand-rolled `ObservableObject` + `RelayCommand`, no `CommunityToolkit.Mvvm`
  dependency. Don't pull one in for a 200-line view model.
- **Never write into `<workshop>/<real_id>/`.** That was the v0.1.0 bug. Synthetic folders
  only.
