# TO-V2-46 — Read-only LIVE own orders on Deribit RAW L2 price levels

## Owner-accepted base

The owner accepted the existing dense vertical Deribit DOM and Last/Mark/Index
presentation at PR #48, exact SHA
6a807ea4c2c50fcc896e75e3cea26fb759185b51.
The accepted 18px order-book rows, columns (Price | Amount | Cumulative),
selected instrument tick precision and RAW book WebSocket are unchanged.

## New feature

Own **open resting LIMIT orders** are shown with small amber/gold markers
alongside their exact resting price level in the public price ladder. Hovering
reveals the user's remaining size and number of own orders at that exact
price and side. The own-order count is shown just above the DOM. The overlay
does NOT add own size to public book size (which could double count) and
never confuses buy own orders with sell or other instrument orders.

Not all own orders will appear as individual markers if they are outside the
top 10 visible L2 levels. Such orders count in the owner total, and are not
attached to an unrelated price row. Additional next iteration can extend
the displayed price range from the already full-depth L2 backend.

## How source events are handled

1. Existing authenticated READ-ONLY WS connection with Deribit continues
   to receive book.{instrument}.raw, trades.{instrument}.raw and
   ticker.{instrument}.raw (market L2 path unchanged).
2. Add **private/subscribe** to
   user.orders.{instrument}.raw on the same already-authenticated private
   read-capable exchange connection. This channel carries single-order
   create/edit/partial-fill/filled/cancelled events.
3. Once subscribe is acknowledged, send exactly one
   **private/get_open_orders_by_instrument** RPC on the same WS. This method
   requires exchange scope trade:read; it does NOT create/change orders.
   The initial snapshot is necessary because subscriptions alone cannot
   be treated as a complete list of previously resting orders.
4. Buffer subscribed order notifications received while waiting for the
   snapshot. Hydrate the snapshot into a capped in-memory dictionary keyed
   by exact exchange order ID; then replay buffered events, retaining the
   latest exchange last_update_timestamp for each ID. Terminal states
   leave tombstones so delayed/stale snapshots cannot resurrect orders.
5. Aggregate all remaining open LIMIT amount = amount - filled_amount by
   exact (instrument, direction, price), without mutating public depth.
6. Publish only **derived aggregates**, never IDs, labels, API keys or
   full account order payload, via the existing **localhost Vite proxy**
   WebSocket to the React UI. Public market book packets also carry the
   current own-order aggregate so the latest overlay can recover from
   browser-side event coalescing.
7. On failed private subscribe, failed snapshot, invalid/private event,
   reconnect or missing permission, clear own state and display an
   explicit **unavailable / synchronizing** status; never pretend there
   are zero orders when private data is uncertain. The public RAW book
   continues uninterrupted when only private order access is denied.

## Security and correctness boundaries

- Both ExecutionEnabled=false and MainnetExecutionEnabled=false remain
  forced in the owner-local one-command PowerShell launcher.
- There are NO private/buy, private/sell, private/cancel, private/edit,
  preview confirmations, writes or automatic order retries introduced.
- Existing read-only Windows CurrentUser DPAPI keys stay in .NET process,
  with no typed operator key, no browser storage and no exposure of IDs
  or order labels in local WS output.
- This overlay is observational, not sufficient evidence to reconcile
  execution or to give precise queue position. The user's own resting
  amount may be included in the exchange public liquidity already; do
  not add it to the public volume column as a total.
- Subscription/snapshot exchange permissions and behavior require an
  owner-local acceptance run. Offline tests exercise fake raw updates,
  cancellation vs snapshot race, partial fills, cross-side grouping,
  stale event guard, disconnected state and invalid event fail closed.
- Source is Draft/unmerged. Exact new SHA owner build, CI and actual
  trading-account private WS acceptance are NOT YET VERIFIED.

## Owner acceptance

1. Stop the prior terminal (Ctrl+C). Start exact-SHA pinned new Draft
   branch launcher; it runs .NET / Node tests and builds React.
2. Confirm public ETH-PERPETUAL RAW L2 LIVE still updates immediately;
   existing positions/accounts remain accessible and density/price format
   unchanged.
3. In the DOM, confirm own-order indicator reads 0 if and only if a
   reconciled snapshot succeeded with zero open resting orders.
   If the vault key lacks trade:read or private subscription fails,
   expect 'нет доступа' rather than zero.
4. If the account already has an open resting limit order inside visible
   top 10 depth levels, confirm yellow marker appears at correct side
   and price; hover shows own remaining amount. Never place a real order
   simply to perform this test.
5. If an order is modified/cancelled independently in Deribit, observe
   the marker update from user.orders RAW without waiting 8 seconds.
6. If disconnected, overlay must clear and resync before being shown as
   ready; public market must keep failing closed on sequence gap.

Official Deribit specification:
https://docs.deribit.com/subscriptions/user/userordersinstrument_nameraw
https://docs.deribit.com/api-reference/subscription-management/private-subscribe
https://docs.deribit.com/api-reference/trading/private-get_open_orders_by_instrument

## Next vertical slices

- Persistent independent strategy-facing market data ingestion while
  React is closed (separate read feed from execution).
- Broader depth window for own orders away from best 10 book levels;
  price tick grouping from instrument specification.
- Real-time own fills/executions and actual order state reconciliation
  with unique IDs on a secure operator-only backend; account-level
  multi-currency and subaccount partitioning.
- Dedicated testnet manual order execution with explicit permissioned
  API key (never promoted from current production read-only DPAPI vault),
  full audit and risk gates; mainnet only with owner authorization.
