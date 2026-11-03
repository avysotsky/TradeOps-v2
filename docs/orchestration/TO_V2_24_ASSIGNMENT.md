# TO-V2-24 — Deribit authenticated read-only evidence contract (assignment / design gate)

**Status:** REGISTERED / DESIGN-ONLY, NOT IMPLEMENTED. Separate stacked Draft PR.
**Base:** owner Gate-A/Gate-B-qualified TO-V2-23 exact
`b586cbafc2b29455708de221dbd5771bb1840b8f`.
**Venue:** Deribit production/mainnet; account has authorized read-only key.
**Hard constraints:** zero order changes, zero transfers, zero new credentials in
GitHub/chat; no merge, no main changes, no package publish.

## Verified starting fact (not evidence beyond counts)

Owner ran `TradeOps.DeribitLiveReadOnlySmoke` from exact TO-V2-23 SHA
against the production account with privately entered read-only
credentials on 2026-10-23. Sanitized output:

```text
TRADEOPS DERIBIT LIVE READONLY PASS account_currencies=14 positions=2 open_orders=0 mutating_calls=0
```

The **14** means returned account-summary currency entries, not funded
wallets. The **2** are raw `get_positions` entries, not yet normalized
portfolio exposures. `open_orders=0` is not an empty trade history.
Proof and source-of-truth gate: PR #25 owner Gate B comment.

## Motivation / existing code gaps

Legacy `DeribitTestnetExchangeClient` has methods for placing/cancelling
orders; NEVER redirect its base URL to mainnet. It filters positions by
`kind=future`; the new isolated mainnet read-only probe deliberately
queries `currency=any` without `kind`, seeing other instrument kinds.

Deribit has non-interchangeable quantity semantics: for inverse futures
`size` may be denominated in quote currency, while option position
`size` is in base units. A bare `Quantity=2` is not automatically two
exchange contracts, two BTC, or USD notional. Unsupported or unverified
contract metadata must fail closed.

Current `DeribitLiveReadOnlyClient` returns **counts only**, so
historical order/execution proof, detailed instrument identity,
instrument multipliers, fee currencies, snapshot completeness,
reconnection and reconciliation are NOT validated.

## Design deliverable for next implementation worker

1. **Typed raw observation record** for `private/get_positions`, not
   legacy `Position` entity: provider key; authenticated account scope
   (bound at credential/session layer, never inferred from symbol);
   `instrument_name`; explicit `kind` (future/option/spot/combo);
   `direction` (buy/sell/zero); exact decimal `size`, optional
   `size_currency`; source timestamp only when actually returned.
   Retain the units and never silently convert to common quantity.
2. **Strict instrument classification:** check required identity,
   type/direction compatibility, unique instrument records within
   snapshot, finite decimal precision and no conflation of option
   side with settlement exposure. Unknown kind, missing required fields,
   malformed decimal, inconsistent duplicate instrument, unexpected
   position metadata must remain *unresolved*, never silently dropped.
3. **Read-only orders and user trades:** design a separate immutable
   per-currency/time-bounded `private/get_user_trades_by_currency_and_time`
   evidence collector (proposed API, verify current official parameter
   semantics before implementation). Every page must preserve trade ID,
   order ID, instrument, direction, amount, price, fee, fee currency,
   timestamps and exact continuation/coverage status where supplied.
   No inference that one response is historical completeness.
4. **Security:** distinct non-`IExchangeClient` mainnet source;
   hardcoded allowlist of HTTPS read endpoints; no buy/sell/edit/cancel,
   no account transfers, no automatic retry, redirects or telemetry.
   Credentials in local process memory only; output allowlisted
   aggregate counts and per-kind statistics only. Prohibit stdout JSON
   bodies, account identifiers, API tokens, individual order/fill IDs
   or balances; secrets never shipped to runner/GitHub.
5. **Test matrix:** synthetic positions future/option/spot/combos,
   inverse-vs-linear unit preservation; no silently dropped positions;
   401/403/429/5xx, malformed JSON-RPC, duplicate instrument IDs,
   pagination missing/continuation ambiguity, wrong account,
   out-of-order/double trades, fee precision and missing required fields.
6. **Gating:** new implementation workstream must qualify WSL2 exact
   HEAD build/xUnit 0 failed/skipped and cleanup 6/6. Owner mainnet
   opt-in smoke for expanded read path is a *separate* Gate B, only
   after Gate A, with a sanitized diagnostic marker. No authenticated
   account data from owner required in GitHub or chat.

## Out of scope (explicit)

- Enabling DeribitLive in capability catalog or API/Worker DI.
- Any real or Testnet order placement, cancellation, modification,
  transfers, broker retries, or WebSocket trading session.
- Migrations of real Orders/Fills or turning synthetic scoped journal
  into a production account write-path.
- Publishing real account portfolio or transaction history.
- Reusing legacy `DeribitTestnetExchangeClient` mutation methods
  for mainnet.

## Dependencies / integration

TO-V2-23 frozen; TO-V2-22/21 and previous accepted SHA unchanged.
No new code worker should start until current official Deribit API shapes
are checked and owner/operator accepts read-only result redaction.
Later distinct workstreams: offline execution evidence parser -> safe
owner read-only pagination acceptance -> scoped reconciliation design ->
WebSocket continuity -> Testnet paper/test order lifecycle. Live
trading is separately blocked pending explicit approval and limits.
