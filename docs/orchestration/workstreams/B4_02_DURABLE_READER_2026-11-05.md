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

Validation: **87/87 PASS**, zero failures/skips, on code revision
`8040469d4d3da297aa881bfe50281e3ad71bc204`. `b4-reader` covers 47 recovery,
15 composition and 25 PostgreSQL reader cases. The two prior recovery PG tests now
use the adapter instead of their unfiltered SQL decoder. Release build passed with
zero errors and 15 pre-existing analyzer warnings. All affected recovery cases were
run; the unrelated 200-case B3/journal/capture regression set and UI E2E were not rerun.

TRX, cleanup and environment PASS. Normalized source manifest matches the tested
revision; test assembly SHA-256:
`23acdfc17fea210af1cb4f3aac92cbe7557783632275fc24542fa46b77f7cf91`.
Local evidence: `.tradeops/postgres-reuse/20261105T081630-b4-reader-10972/`
and `.tradeops/b4-reader-summary.json`, excluded from Git.
The same reusable server ID/start/restart remained; no test clones/roles or sessions
remained after cleanup. Peer snapshots are identical; Resource Guard passed before
and after. The user's launchSettings file is unchanged.

Next: explicit opt-in read-only store binding/qualification and coherent bounded
venue history/fill collection, then positions/balances and durable recovery gates.
Production provisioning, resumable checkpoints, projection publication and B5
physical sender fencing remain unqualified. B4 is not complete and #73 stays open.
The saved mainnet credential, consumed order marker and retained store are untouched.
