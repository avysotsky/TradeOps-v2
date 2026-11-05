# B4-05c — explicit ETH inverse futures account manifest

2026-11-05. Implementation `5eb68c0f37ad38a62b6ddba8a4338eb0117d30ec`.
The explicit active-group policy passed its single live read-only qualification:
**ACCOUNT_SNAPSHOT_SCOPED_PASS**. This qualifies scoped observations, not account-wide
coverage, coherent recovery, durable publication or READY.

## Delivered

- New separate `--read-eth-future-snapshot` mode and
  `Deribit-AccountSnapshotLocal.ps1 -Manifest EthInverseFutures`, purpose
  READ_ONLY_ETH_INVERSE_FUTURES_V1. The legacy single-instrument mode remains the
  default. Mode/purpose mismatch refuses before network I/O; account pin, ETH/future
  query filters, read-only grants and ordinary execution switches are unchanged.
- `DeribitEthFutureAccountManifest` constructs policy EthActiveInverseFuturesV1 from
  public metadata before private authentication. Exact environment/currency/kind,
  local UTC receipt age ≤30 seconds, unique names, 1–256 active instruments including
  ETH-PERPETUAL, reversed futures, ETH base/settlement, USD quote/native units. One
  unsupported active row refuses the entire manifest. No units are inferred from
  symbols or private rows; the sorted selected list is read-only.
- Inactive entries are excluded by this named policy. Positions outside it, including
  inactive/expired/absent metadata, remain UnmappedInstrument; no filtering of private
  rows, metadata fallback, automatic manifest growth or retry.
- Diagnostics explicitly advance to SchemaVersion=2 with selected ManifestPolicy
  and actual RequestedInstrumentCount. Wrapper validation binds policy/count/metadata
  and position capacity to the selected mode. Old unversioned reports stay historical
  files; durable contracts and DB schemas are unchanged.
  [Contract](../../architecture/DERIBIT_SCOPED_ACCOUNT_SNAPSHOT.md).

The [public endpoint documentation](https://docs.deribit.com/api-reference/market-data/public-get_instruments)
warns that its results are cached and can include inactive instruments. Local receipt
age does not prove venue cache freshness, tradability or a coherent account cut.

## Offline verification

Windows Release build: exit 0, zero errors, 15 existing warnings, prescribed SDK and
serial flags. Actual `b4-account-manifest` TRX: **394 total/executed/passed**, zero
failed/skipped; runner exit 0, environment/cleanup PASS. Prior 364 cases plus 30
manifest/freshness/unit/coverage/mode/native identity and redaction cases.
Actual PowerShell output-validator tests: **32/32 PASS**, without vault or venue.
New console entry with empty input: exit 1, sanitized Input refusal, group policy v2
and zero requested instruments before metadata; no order-path fallback.

Evidence: `.tradeops/postgres-reuse/20261105T141616-b4-account-manifest-28662/`.
**320 normalized source hashes** matched. Test DLL SHA256:
`D08F76D5FA2FE44C028E7F5247451E7B3F05B00410EC48C01A0DC10127CAA7D7`.
All four live DLLs matched their copies loaded by the test project. Host DLL SHA256:
`CFDCE8653DE5E14DF45BD00A5D8E84A3A1C91145B2D130EC351CC4B120421B20`.
Guard before/after PASS, peer container snapshots unchanged, owned test clone/role/
session cleanup PASS. Reusable test server/template retained; offline venue results
were scripted. Owner launchSettings hash remains
`CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

## Single live qualification

One invocation after the implementation commit, with the existing saved DPAPI
credential/native pin, assessed at `2026-11-05T14:20:44.2199014Z`:

- **ACCOUNT_SNAPSHOT_SCOPED_PASS**, host/wrapper exit 0; no retry.
- SchemaVersion=2, ManifestPolicy=EthActiveInverseFuturesV1;
  RequestedInstrumentCount=13 from public metadata, accepted PositionCount=2.
- MetadataValidated, QueriesCompleted, NativeBindingVerified, FreshAtAssessment
  and BalanceObserved=true; Problems empty. No actual names, sizes, balances or
  native account IDs were emitted. Counts refer to accepted rows, not a claim that
  every row is nonzero or that unqueried currencies have no holdings.
- AccountWideCoverage, VenueCoherent, AccountReady, DurableProjectionPublished and
  TradingReady=false. FreshAtAssessment describes that instant, not a reusable lease.
- DatabaseUsed=false, DurableWrites=0, MutatingCalls=0. No mainnet journal was opened
  or audited; the old order/cancel harness and terminal-order lookup were not rerun.

The anonymized summary at
`.tradeops/account-snapshot/20261105T142042-a0743764530249c8a9f11ab5f78cbfe6/summary.json`
records the implementation SourceCommit and matching four assembly hashes. No private payloads
or raw stderr were saved; no position or order was changed.

The previous B4-05b single-instrument INCOMPLETE result remains valid historical
evidence. It was not overwritten or reclassified as success; this is a separate,
explicitly selected group policy.

## Next increment

B4-05d: compose the scoped account port with Application assessment. Validate the
actual returned scope/epoch, expected manifest, currency/kind, observation windows
and freshness at use. Consume private observations in memory, never the historical
anonymous diagnostics or caller-provided success flags. Keep active capture ownership
and local receipt replay distinct from venue snapshot/history coherence.

Complete relevant execution ledger, qualified venue cut, account-wide coverage,
durable projection/recovery gates and B5 physical fencing remain separate gaps;
B4/READY and #73 are open. No new trading operation is authorized by this increment.
