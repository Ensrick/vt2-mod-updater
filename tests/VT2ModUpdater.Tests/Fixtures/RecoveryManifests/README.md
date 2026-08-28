# Producer manifest fixtures

`producer-tracked-manifest.json` and `producer-receipt-manifest.json` are
byte-for-byte outputs of the complete schema-2 manifest producer merged through
vermintide-2-tweaker PR #1439 as exact master commit
`5d397d4f5e47f1c1dc56a1866a436d661703112f`. Exact-master QA run
`33129113964` passed on that commit.

The producer's offline `qa/check_release_recovery_record.ps1 -SelfTest` builds
the tracked and receipt authority snapshots, deterministic ZIP coordinates,
and recovery children through `New-VtReleaseRecoveryRecord`. It serializes the
full manifests through the same `ConvertTo-VtReleaseManifestBytes` function as
`tools/publish-release/publish-release.ps1`. PowerShell 7 and Windows
PowerShell 5.1 reproduce the exact compact UTF-8-without-BOM bytes from fixed
Git and timestamp inputs. The consumer fixtures below are therefore pinned to
the reviewed, merged producer rather than to a provisional worktree.

The tests freeze the exact bytes with these SHA-256 values:

- tracked: `c367667af8ddf00c08d8b78f2fb5f8b791dc6b7897109f06316835d41a527dc6`
- receipt: `812f656096f178fecfcb59e2a74b37811b046ab187516b0df8b65cc1e43981ec`

These are offline contract fixtures only. They do not authorize ZIP download,
installation, deployment, release mutation, or updater-path integration.
