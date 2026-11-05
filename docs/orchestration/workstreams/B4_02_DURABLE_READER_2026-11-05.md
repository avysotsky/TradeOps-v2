# B4-02 — durable recovery reader

Implement the next B4 slice: obtain validated, account-scoped committed private
facts after restart and pass them to the existing order recovery reducer.

Delivered application read port, PostgreSQL read-only repeatable-read adapter and
read-side composition. Canonical v2 facts/v1 observations and all migrations remain
unchanged. Reader reconstructs canonical digests, validates scope/metadata/schema,
links accepted observations to inbox facts and preserves blocking quarantine and
integrity incidents. Bounded complete rereads avoid treating timestamps or sequence
allocation as commit-safe checkpoints. No host registration or trading change.

[Reader contract and bounds](../../architecture/OMS_PRIVATE_RECOVERY_READER.md).

Validation pending actual build/TRX/source/environment/cleanup evidence. Target suite:
`b4-reader`, 87 cases (47 recovery, 15 composition, 25 PostgreSQL reader). Existing
two recovery PG tests now use the adapter instead of their unfiltered SQL decoder.

Next: explicit opt-in read-only store binding/qualification and coherent bounded
venue history/fill collection, then positions/balances and durable recovery gates.
Production provisioning, resumable checkpoints, projection publication and B5
physical sender fencing remain unqualified. B4 is not complete and #73 stays open.
The saved mainnet credential, consumed order marker and retained store are untouched.
