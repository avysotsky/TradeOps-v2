# B4-03a — opt-in reader for the retained mainnet qualification store

The existing qualification executable has two explicit read-side modes:
`--provision-reader` and `--read-snapshot`. Both return before the trading runner
and before `QualificationDatabase.OpenAsync`; neither runs migrations, authenticates
to Deribit, registers a sender or changes execution switches.

`scripts/deribit/Deribit-RecoveryReaderLocal.ps1` exposes `ProvisionReader` and
`ReadSnapshot`. Build the executable using the project's bounded Windows SDK
settings before running either mode. There is no implicit build, provisioning,
server startup or schema upgrade on the read path. The separate shell runner
accepts only the existing running owner-marked persistent container/network/volume;
it refuses missing/stopped/foreign resources. It records Resource Guard, peer
snapshots, assembly SHA, source commit and anonymous database counts before/after.
It never changes Docker/WSL configuration or removes persistent resources.

## Provisioning boundary

Provisioning validates the exact database, owner, database ownership comment,
PostgreSQL 17 cluster marker and durability settings before any write. It creates
one marked login with SELECT only on inbox, capture observations and incidents,
plus database CONNECT and schema USAGE. PUBLIC temporary-object access is revoked
on this owned qualification database so the reader cannot inherit TEMP. No table,
schema, migration, journal row or existing capture/journal role is changed.
The reader password is derived in memory using the existing store secret and a
distinct role name; it is never printed, passed as an argument or saved as evidence.
An existing unmarked role is refused; a marked existing role is not silently repaired.

The runtime gate independently rejects elevated flags, role memberships, database
CREATE/TEMP, user-schema CREATE, table write privileges, sequence mutation, and
executable user functions (trigger-returning functions cannot be invoked directly).
Required SELECT privileges and the role marker must be present. The provision
command also validates a fresh runtime connection before reporting success.

## Account binding and retained evidence

The Windows wrapper decrypts the previously saved DPAPI native pin and verifies
its environment, native account type/ID and credential-ID digest against the saved
vault's client ID. The client secret is not decrypted. Only the native binding is
passed through standard input, never command arguments or logs. The application
constructs the original qualification tenant/venue/environment/account/subaccount
mapping from that binding; it does not discover identity from database contents,
names or fingerprints. This is a saved binding, not fresh venue authentication.

The actual `OmsPostgresPrivateRecoveryReader` validates the complete scoped snapshot
using its existing canonical/schema/linkage/incident checks. Qualification expects
the two retained 1 USD ETH-PERPETUAL open/cancel facts for one order and zero filled
amount; any different/incomplete history refuses this specific historical case.
Only counts and status are emitted. No IDs, labels, payloads, balances or secrets
are published. The historical attempt marker is never opened for writing.

This proves read access to retained evidence. It makes no fresh venue request,
does not establish outage coverage, and does not advance READY or publish a durable
projection. Bounded venue collection of open orders/history/separate fills with
overlap/pagination/incompleteness is the next B4-03 increment. Positions/balances,
coherent replay, durable recovery gates and physical sender fencing remain later work.
