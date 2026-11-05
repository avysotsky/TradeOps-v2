# B4-03a — retained mainnet store reader qualification

2026-11-05. Implementation commit `9465a608db2070cff52caba3be2c6ed4905178d7`.
This completes the store-binding portion of B4-03, not the venue collector or B4.

## Delivered

- Explicit opt-in read/provision modes separated from the migrating opener and
  completed one-order harness. No automatic host registration or exchange requests.
- Exact owned persistent store checks, separate marked SELECT-only runtime role,
  effective privilege checks and DPAPI native-pin account/subaccount binding.
- Actual B4-02 reader over the retained scoped open/cancel facts, canonical integrity,
  schema/linkage checks and blocking incidents. Only anonymous counts/status emitted.
- [Binding and provisioning contract](../../architecture/DERIBIT_RETAINED_RECOVERY_READER.md).
  Durable payloads, migrations and connector dependencies remain unchanged.

## Verification

Windows Release build completed with exit 0, zero errors (15 existing warnings),
using the prescribed SDK and bounded build settings. `b4-binding` actual TRX:
**99 total/executed/passed, 0 failed/skipped**, runner exit 0, environment/cleanup PASS.
This includes the existing 87 recovery/reader cases and 12 binding cases covering
invalid pin/hierarchy, persistent-store identity refusal on a disposable clone,
idempotent provisioning, restricted real reads and refusal of extra effective rights
(table INSERT, role membership, TEMP, executable user function and schema CREATE).
All destructive/injection cases used owned disposable clones.

The first invocation was started before the new build finished and selected the
old 87-case assembly. Its count gate correctly failed; it is not qualification
evidence. The final 99-case run used the successfully rebuilt current assembly.

Actual retained-store qualification, each exit 0:

- Explicit provisioning: `RECOVERY_READER_PROVISIONED`, migrations 0.
- Read: `RETAINED_SNAPSHOT_PASS`, accepted facts 2, observations 2, blocking problems 0.
- Runtime SELECT-only gate passed. Persistent journal counts before/after both
  operations: commands/attempts 2/2, inbox/observations/incidents 2/2/0.
- Resource Guard before/after PASS; complete container lifecycle snapshots unchanged.
- Venue requests 0, mutating venue calls 0. Existing order harness/attempt marker
  were not invoked or rewritten. Both ordinary execution switches remained false.
- Owner's untracked launchSettings SHA256 remains
  `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

Private evidence remains ignored locally:
`.tradeops/postgres-reuse/20261105T084616-b4-binding-22198/`,
`.tradeops/mainnet-recovery/20261105T084935-provision-reader-20617/`,
`.tradeops/mainnet-recovery/20261105T084957-read-snapshot-4871/`.
Test DLL SHA256: `62E305EAE09B13C7B12F613BF9929D419A54276D0A84D18775CEA869D2512663`.
Qualification DLL SHA256: `B7A0F70B41FD162DA1027F1CEB0CD589E4C6CE0EA024C5B0BEB06F1AF2B220A3`.
No native identities, credentials, labels, balances or account payloads are published.

## Remaining B4-03 and later gates

Next: bounded read-only Deribit open-orders/history/separate-fills collection over
an explicit outage interval, overlap, pagination and explicit incompleteness, using
the saved key/native pin. No repeat trading qualification is authorized or needed.
This old database snapshot is not fresh venue reconciliation, account-wide coverage,
coherent replay or READY. Positions/balances, durable projection/recovery gates and
physical sender fencing remain subsequent work. Issue #73 stays open.
