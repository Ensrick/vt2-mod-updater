# Recovery-record fixtures

`valid-tracked.json` and `valid-receipt.json` are frozen outputs of
`New-VtReleaseRecoveryRecord` at vermintide-2-tweaker commit
`7e5732c77d6d9ad43f333f1fa5555e135fc9207e`. The input output set was built by
that commit's `New-VtBundleOutputSet`; the normalization proof was built by its
`New-BuildOutputNormalizationPolicyProof`; and the deterministic ZIP coordinate
was built by its `New-ReleaseZipBytesFromImmutableOutput`.

The unit tests derive hostile copies from these producer outputs in memory.
This keeps duplicate-property attacks representable as raw JSON and prevents a
second hand-maintained "valid" schema from drifting away from the producer.

These fixtures establish only the offline schema-1 consumer contract. They are
not a release-history lookup, artifact download, ZIP verifier, restore plan, or
installed-state proof.
