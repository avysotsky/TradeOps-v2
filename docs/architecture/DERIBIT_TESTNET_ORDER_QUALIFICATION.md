# B3 — one testnet order and durable private events

The owner's subsequent [small-account mainnet ETH decision](DERIBIT_MAINNET_ETH_QUALIFICATION.md)
supersedes waiting for this testnet scenario. This document still describes only
the original testnet entrypoint, which cannot enable mainnet trading.

The next acceptance run has a finite result: one BTC-PERPETUAL minimum-size passive
buy, its committed open event, a targeted cancellation and its committed cancelled
event. It never treats a quiet socket as an order-capture success.

## Boundary and approval

This is a separate acceptance executable, not a registered production sender or
proof of B4/B5 readiness. MAINNET remains read-only and both existing execution
switches remain false. There is no configurable exchange URL: HTTP and WSS are
fixed to `test.deribit.com`. An explicit testnet purpose, environment, dedicated
test-account declaration and one-order/cancellation approval are required.

The owner's [project rule](../../AGENTS.md) requires separate authorization
before a mutating testnet run. Authorization for this one-order scenario is now
received, and its separate testnet key is created/encrypted and verified by real
read-only requests. Actual placement remains **NOT_RUN** because BTC-PERPETUAL
reports `halted`; do not consume the one attempt on a stopped market.
Mainnet credentials are never read by
this launcher and are not copied, replaced or elevated.

## Exact first run

1. Command HTTP requests use JSON-RPC POST with a unique `session:to_b3_...` token
   and a 300-second lifetime. A `connection` token belongs to one connection and
   cannot serve independent HTTP requests. The private WSS capture connection
   retains its own existing read-only connection-scoped authentication.
   Authenticate with requested `trade:read_write account:read`, explicit denial of
   wallet/block capabilities, reject extra positive grants. Read authenticated
   native ID/type and require the independent private stream to match. An optional
   externally known native pin must match too. This diagnostic identity is not
   independently attested and does not establish a production parent hierarchy.
2. Require no BTC open orders or nonzero BTC positions on the dedicated account.
   Validate actual inverse BTC future metadata, `min_trade_amount <= 10 USD`, tick
   size, active market and a fresh book. Unsupported stepped ticks fail closed.
3. Create a marked isolated PG clone and a restricted capture role using the
   existing guarded fixture. Subscribe through the actual capture coordinator and
   store. Refresh instrument/book checks after subscriptions and before dispatch.
4. Buy the minimum native USD amount, capped at **10 USD**, at a tick-aligned price
   0.5% below the best bid, within the current venue band. Set `post_only=true`,
   `reject_post_only=true`, limit/GTC and one generated unique label. A passive
   order can still fill later: this is not a guarantee of no execution.
5. Before sending, flush a non-secret recovery marker and commit the intent,
   command, outbox and dispatch attempt through the existing OMS journal. The
   test-only risk policy is the closed set above; it is not the full B5 engine.
   There is exactly **one placement attempt** and no blind resend after any error.
6. Wait at most 10 seconds for that label's open order fact to commit. In `finally`,
   journal one cancellation attempt targeting only that unique label in BTC,
   including when the placement ACK was lost. Never call cancel-all, edit, sell,
   flatten or touch another order. Query explicit recent order state after cancel;
   an empty open-orders result is not cancellation proof.
7. Require both committed open/cancelled facts with zero filled quantity, a
   confirmed terminal exchange state, no capture problems/incidents and clean
   shutdown. Any observed fill fails this first scenario; no automatic flatten is
   authorized. Future fill/partial-fill and disconnect/recovery cases are separate
   acceptance scenarios and cannot be claimed by this run.

The generated marker stays in `.tradeops/b3-testnet-order/attempt/attempt.json`,
even after PASS. An unresolved attempt blocks another run. Review exchange state
before archiving the marker for a deliberately new attempt. Process termination
can prevent cancellation; neither a marker nor a DB lease is physical fencing.

## Local operation

Use a dedicated, funded **Deribit testnet** account. Its virtual funds and API keys
are separate from mainnet. Never paste keys into chat or command-line arguments.
Windows enrollment prompts for hidden input and saves only DPAPI CurrentUser
ciphertext under ignored `.tradeops/`:

```powershell
./scripts/deribit/Deribit-TestnetOrderLocal.ps1 -Action Status
./scripts/deribit/Deribit-TestnetOrderLocal.ps1 -Action Save
# Only after the owner authorizes the specific one-order scenario:
./scripts/deribit/Deribit-TestnetOrderLocal.ps1 -Action Run -ExpectedHead <verified-40-character-SHA> -ApproveOneTestnetOrderAndCancellation
```

Run requires committed clean source, builds before decrypting, passes credentials
only over child stdin, and checks the exact capture SHA and final guard/cleanup.
The parent does not kill a child that could be cancelling. A timeout/unknown result
requires inspection of the marker, not another placement. No automatic retries or
recurring quiet runs are installed.

The reusable root runner adds `b3-live-testnet-order` with the same owned-server,
resource and neighboring-container guards. It removes the generated role/clone,
reports only sanitized evidence and preserves the recovery marker. The tool has
no arbitrary database target in its input. Actual run authorization is enforced by
operator workflow; a JSON boolean is not an authentication or RBAC mechanism.

Offline tests use scripted exchange responses and real isolated PG:

```powershell
./scripts/testing/Run-OmsPostgresTests.ps1 -Suite b3-testnet-order
```

The current suite has 134 cases. Its HTTP fake asserts POST, a unique REST session
and bounded expiry; it rejects connection-scoped/mismatched grants. It does not
replace actual venue qualification. [Key enrollment and live read-only results](../orchestration/workstreams/B3_02_TESTNET_KEY_2026-11-04.md).

## Official semantics checked

- [public/auth](https://docs.deribit.com/api-reference/authentication/public-auth):
  REST session vs connection scope and bounded token expiry.
- [private/buy](https://docs.deribit.com/api-reference/trading/private-buy): amount,
  label, limit/GTC and post-only rejection parameters.
- [public/get_instrument](https://docs.deribit.com/api-reference/market-data/public-get_instrument)
  and [get_order_book](https://docs.deribit.com/api-reference/market-data/public-get_order_book):
  native minimum amount, tick metadata, book freshness and bands.
- [private/cancel_by_label](https://docs.deribit.com/api-reference/trading/private-cancel_by_label):
  exact label plus BTC currency filter; requires trade write scope.
- [private/get_order_state_by_label](https://docs.deribit.com/api-reference/trading/private-get_order_state_by_label):
  explicit state, recent closed-order window; not a general historical recovery API.
