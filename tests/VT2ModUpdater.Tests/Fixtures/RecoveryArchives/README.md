# Source-exact recovery ZIP fixtures

These two archives are copied byte-for-byte from the deterministic producer
fixtures merged in `Ensrick/vermintide-2-tweaker` commit
`699840b17f55e8053caa5638d383d6d9b2c0e395`.

- `producer-tracked.zip`
- `producer-receipt.zip`

Both are 546 bytes, Git blob
`ac7d9b31468400a229abb80ba3b82a77ca321672`, and SHA-256
`7d1f642208d5851b8cfa748e4207093c24de70a2a6377b2473b1b1996d86b4e0`.
They intentionally have identical archive bytes while their paired recovery
records prove the tracked- and receipt-authority paths independently.
