# Small-account ETH-PERPETUAL qualification

Owner decision, 2026-11-04: stop waiting for testnet; use the existing saved small
mainnet account and its existing key, whose trading permissions the owner updated.
The owner explicitly selected ETH-PERPETUAL because its native minimum is 1 USD.
The encrypted account pin and credential-ID fingerprint identify that account;
neither an account name nor the vault's historical `ReadOnly` filename determines
the actual API grant. Do not ask for the same account/key/authorization again.

[First real result](../orchestration/workstreams/B3_03_MAINNET_ETH_2026-11-04.md):
one 1 USD placement and targeted cancellation PASS, both private facts committed,
no fill, existing position preserved, persistent journal retained. The completed
attempt marker prevents automatic repetition.

## First bounded case

- Fixed mainnet endpoints; pinned native account ID/type mandatory. Explicit
  `MAINNET_SMALL_ETH_ONE_ORDER_V1` purpose in a separate executable. Testnet's
  original entrypoint still refuses mainnet input.
- ETH-PERPETUAL only, inverse future, ETH settlement, USD-native quantity. Fetch
  current metadata; maximum amount **1 USD**, not 1 ETH. One passive limit buy,
  post-only with rejection, one unique label and no placement retry.
- Account total equity must be positive and at most 20 USD, with at least 1 USD
  free margin buffer. This is a closed qualification policy, not the full B5 engine.
  Existing positions are observed, not closed. Compare ETH-PERPETUAL direction/size
  before and after; do not compare changing mark prices or PNL. No existing ETH open
  orders may be present when the case starts.
- Dedicated REST session, 300-second token expiry, account read and trade write;
  deny wallet/block capabilities. Independent private WSS capture requests read-only
  scopes and verifies the same native account. Ordinary app execution switches stay
  false; this dedicated authorized executable does not register a production sender.
- Commit intent/command/outbox/dispatch attempt before the one physical placement.
  Include a venue-time `valid_until` admission deadline of 5 seconds. Flush a
  non-secret recovery marker before sending; any previous marker blocks repetition.
- Wait for the own-label open fact to commit, then cancel only that label in ETH.
  Confirm explicit terminal order state and the committed cancelled fact. A missing
  open order or an ACK alone is not cancellation/fill proof. Lost placement ACK does
  not trigger resend. A fill, changed baseline position, collision, disconnect or
  unknown state fails the case and stops further placements. No automatic flatten.

## Persistent journal

Real-money events must not use a disposable test clone. A separate owner-marked
PostgreSQL17 server uses a dedicated persistent Docker volume, internal network,
no published ports, 384 MiB, 0.5 CPU and 128 PIDs. Durability settings remain on.
Database, roles and private event/journal rows are retained on every exit. This
is a new owned qualification store; existing persistent databases and neighboring
services are untouched. Resource Guard and peer snapshots surround operations.

Before migrations, verify cluster/database ownership markers. Runtime capture and
command journal use separate ordinary roles, without superuser/create/schema rights
or legacy Orders/Fills writes. The existing capture owner verifies its role and
holds its scoped lease. Existing ambiguous/started commands or integrity incidents
prevent a new mainnet attempt. A DB lease/local lock does not establish general
production physical fencing or B4/B5 readiness.

The new runtime reuses the existing durable OMS journal, versioned capture facts and
private stream. It does not change their schema/namespaces/hashes. Mainnet's entry
point cannot select a disposable fixture; injected fixture factories exist only
for scripted unit tests, whose results do not claim persistent retention.

## Operation and evidence

`scripts/deribit/Deribit-MainnetEthLocal.ps1` provides Status, StoreCheck and Run.
StoreCheck creates/verifies only the owned store, without exchange calls. Run
requires clean exact committed source, builds before decrypting, uses the existing
encrypted credential and pin, and passes secrets only through child stdin. Do not
terminate/repeat a child which may still be cancelling. Review the retained journal
and recovery marker after uncertainty.

The `b3-mainnet-eth` suite has 144 scripted-exchange/real-test-PG cases. The separately
authorized real-money case is never run by this suite. A live PASS qualifies only
this own-order event/cancel path; full fills/recovery/account-wide coverage and
production strategy/sender readiness remain separate gates.

Local evidence stays under ignored `.tradeops/b3-mainnet-small-account/` and
`.tradeops/mainnet-qualification/`. Publish only sanitized counts/outcomes, never
keys, native IDs, raw responses or database connection strings.

Official semantics: [public/auth](https://docs.deribit.com/api-reference/authentication/public-auth),
[private/buy, including valid_until](https://docs.deribit.com/api-reference/trading/private-buy),
[native instrument metadata](https://docs.deribit.com/api-reference/market-data/public-get_instrument).
