# TradeOps-v2 — Institutional Trading Platform Master Roadmap
> **B4-05a, 2026-11-05:** [scoped account reader реализован](workstreams/B4_05A_SCOPED_ACCOUNT_SNAPSHOT_2026-11-05.md), **336/336 PASS**, environment/cleanup PASS, 314 source hashes совпали. Отдельный Application port/Deribit connector для BTC/ETH inverse future/option positions и cash balance/equity выбранной валюты: native units, manifest/native identity checks, local observation windows и freshness при использовании. Unknown/partial/stale результаты закрываются целиком; нет синтезированных zero positions, account-wide coverage, venue coherence, durable publication или READY. Это offline/DB-only, без vault/mainnet store/live venue/trades. Следом B4-05b — opt-in read-only qualification host с saved key/native pin и анонимными diagnostics, затем assessment integration; full ledger/coherent venue cut/durable gates/B5 остаются. #73 открыт.
> **B4-04c, 2026-11-05:** [active replay lifecycle связан с evidence gate](workstreams/B4_04C_ACTIVE_REPLAY_GATE_2026-11-05.md), **294/294 PASS**, environment/cleanup PASS, 311 source hashes совпали. Begin до reads, seal/drain с deadline, финальная cut revalidation и cleanup своего окна; timeout не ждёт независимый capture и не удаляет superseding window. Valid local prefix снимает только BufferedReplayUnavailable; venue/history/ledger blockers и READY/publication=false остаются. Два новых PG integration cases включают lost-ACK после реального commit без resend. Это offline/DB-only, без live account/mainnet store/trades. Следом — scoped read-only positions/balances и отдельная квалификация venue coherence/full ledger/durable gates; #73 открыт.
> **B4-04b, 2026-11-05:** [local receipt cut и bounded committed replay реализованы](workstreams/B4_04B_CAPTURE_REPLAY_2026-11-05.md), **275/275 PASS**, environment/cleanup PASS. Opt-in tracking связано с active scope/epoch и атомарным runner lease; ingress не ждёт DB commit, seal учитывает pending queue, stale/gap/overflow/lost-ACK закрывают replay. Три новых PG cases включают реальный commit с потерянным ACK и scripted adapter→PG. Это offline/DB-only квалификация локального prefix, не live/venue coherence/READY; mainnet store и credentials не использовались. Следом B4-04c — active cut lifecycle в evidence gate, bounded drain и финальная revalidation; venue coherence/coverage, ledger, positions/balances/durable gates остаются. #73 открыт.
> **B4-04a, 2026-11-05:** [application evidence gate реализован](workstreams/B4_04A_EVIDENCE_GATE_2026-11-05.md), **209/209 PASS**, environment/cleanup PASS. Реальный assessment **BLOCKED**: retained/enumerated facts **2/0**, known orders **1**, addressed **0**, retention-expired **1**; семь явных history/coherence/replay/ledger/retention blockers, без выдуманной отмены/READY/projection. Snapshot/query/known bindings и provenance проверяются, старый terminal order не опрашивается повторно. Журнал **2/2**, inbox/observations/incidents **2/2/0** неизменен, trading/writes **0**. Следом B4-04b: qualified coherent cut и bounded buffered replay с active scope/epoch, затем ledger/positions/balances/durable gate; #73 открыт.
> **B4-03c, 2026-11-05:** [адресное чтение known orders реализовано](workstreams/B4_03C_KNOWN_ORDER_READS_2026-11-05.md), **173/173 PASS**, environment/cleanup PASS. Из настоящего scoped SELECT-only snapshot выбрана одна известная заявка; один live lookup дал **Missing/10004**, current facts **0**, retained facts **2**. Журнал commands/attempts **2/2**, inbox/observations/incidents **2/2/0** неизменен. У get_order_state закрытые ордера доступны лишь 30 минут: старый cancel не превращён в свежую отмену/absence/zero fills. Отрицательный query path проверен, но historical-unfilled gap, B4/READY не закрыты. Далее — recovery orchestration/gate с явными unavailable-evidence/retention/coverage/coherence blockers, затем positions/balances/durable projection; #73 открыт.
> **B4-03b, 2026-11-05:** [bounded read-only recovery queries реализованы](workstreams/B4_03B_RECOVERY_QUERIES_2026-11-05.md), **137/137 PASS**, environment/cleanup PASS. Реальный pinned ETH-PERPETUAL сбор исчерпал пять поддерживаемых выборок, facts **0**, без trading/DB writes; постоянный journal независимо перепроверен **2/2/0**, commands/attempts **2/2** неизменны. Deribit запрещает `historical=true + include_unfilled=true` (400/-32602), поэтому **HistoricalUnfilledOrdersCovered=false, OrderHistoryComplete=false, CoherentRecovery=false**. Quiet live enumeration не доказывает fills, account coverage или READY. Следом B4-03c — адресные read-only факты для известных retained orders, затем coherent replay/positions/balances/durable gate; #73 открыт.
> **B4-03a, 2026-11-05:** [opt-in SELECT-only binding постоянного mainnet qualification store проверен](workstreams/B4_03A_RETAINED_READER_2026-11-05.md): **99/99 PASS**, environment/cleanup PASS; реальный новый reader прочитал retained open/cancel — facts/observations/problems **2/2/0**. Provisioning отдельный, без migrations; журнал commands/attempts **2/2** неизменен, venue requests **0**. Это исторический DB snapshot, не свежая сверка/READY. Далее B4-03b: bounded read-only open-orders/history/separate-fills collector с outage interval, overlap, pagination и явной неполнотой. Позиции/балансы, durable projection/gates и B5 fencing остаются; #73 открыт.
> **Точка переноса чата, обновлена 2026-11-05:** [продолжение после B4-04c](CONTINUE_TRADEOPS_2026-11-05.md). Query slices, blocked assessment, local replay и gate lifecycle проверены; venue coverage/coherence, account snapshots и durable gates ещё не завершены. Приватные технические указатели находятся только в ignored `.tradeops/CONTINUE_PRIVATE_2026-11-05.md`.
> **Разрешение на публикацию, 2026-11-05:** владелец явно подтвердил в чате: «Да, B4-02 и следующие проверенные этапы». Обычные push и безопасная интеграция следующих небольших проверенных этапов в main разрешены без отдельного запроса на каждый этап. Это разрешение только на код; оно не расширяет торговые разрешения и лимиты. Запреты force push, GitHub Actions и публикации private data сохраняются.
> **Архитектурное решение владельца, 2026-11-04:** [целевая модульная платформа](../architecture/TARGET_PLATFORM_ARCHITECTURE.md) — 10–20 независимых venue-коннекторов, разные протоколы, отдельные market data / arbitrage / risk / OMS / analytics. Границы кода закладываются сейчас; выделение процессов — по измеренной необходимости. Deribit-only V2 ограничивает поставку, не будущую архитектуру. [Корневой AGENTS.md](../../AGENTS.md) закрепляет это правило для дальнейшей работы.
> **Актуальное решение владельца, 2026-11-03:** TradeOps V2 поддерживает только **Deribit**.
> **Deribit FIX 4.4 через QuickFIX/n обязателен в V2**; историческое обозначение B6 как optional ниже этим решением заменено.
> CoinCall и остальные venue-направления отложены до V3; полное VAN parity отложено.
> Приоритет: B1/B2 → Deribit private events и recovery → reconciliation/risk/fencing → FIX qualification и controlled release.
> Multi-venue разделы ниже сохраняются как долгосрочный план, а не текущий scope V2.
> Состояние интеграции и backlog: [обновление 2026-11-03](BACKLOG_CLEANUP_AND_B1_FIX_2026-11-03.md).
> **2026-11-04:** [B2 DB-only review завершён](workstreams/B2_01_FINAL_REVIEW_2026-11-04.md), 28/28 выбранных проверок PASS. Следующий этап — B3 Deribit private events, сначала read-only; runtime OMS и торговая готовность этим не заявляются.
> **2026-11-04, продолжение:** [изолированный reusable PostgreSQL и template clones](workstreams/B2_POSTGRES_REUSE_2026-11-04.md) проверены: B2 28/28 и fixture 4/4 PASS, PG case time 340,9 → 155,9 s при прежних лимитах. [Inventory существующего B3 потока](workstreams/B3_01_PRIVATE_STREAM_INVENTORY_2026-11-04.md) выполнен, прежние offline contracts 12/12 PASS.
> **B3, 2026-11-04:** [scoped durable capture, первый store-only increment](workstreams/B3_01_SCOPED_CAPTURE_2026-11-04.md) реализован: application port, event v2 + отдельные observations, атомарный commit/quarantine без изменения inbox v1. Новые проверки 28/28 и B2 compatibility 28/28 PASS. Этот increment квалифицирует хранилище; последующий lifecycle описан ниже. Issue #73 открыт, trading readiness не заявляется.
> **B3, следующий increment 2026-11-04:** [read-only stream lifecycle](workstreams/B3_01_STREAM_LIFECYCLE_2026-11-04.md) реализован в существующем адаптере: account/scope verification, bounded reader/capture, heartbeat/reconnect/epoch и account-switch barrier; 52/52 целевых проверок PASS, включая loopback WS и реальный PG pipeline. Новый путь явно вызываемый, в работающий host автоматически не включён; все epochs остаются RECOVERING. Доверенная host-привязка и live read-only qualification — следующий gate; #73 открыт.
> **B3, архитектурная граница 2026-11-04:** [существующий WS/private transport выделен в TradeOps.Connectors.Deribit](workstreams/B3_01_CONNECTOR_BOUNDARY_2026-11-04.md), отдельная сборка без Infrastructure/EF/Npgsql; 52/52 повторных целевых проверок PASS. Общая целевая архитектура закреплена в отдельном документе и AGENTS.md. Legacy REST и Deribit-specific capture contracts ещё требуют дальнейшего разделения; host/live gate остаётся следующим.
> **B3, opt-in host 2026-11-04:** [серверный binding и отдельная read-only композиция Worker](workstreams/B3_01_READONLY_HOST_2026-11-04.md) реализованы: explicit false switches, отдельная restricted DB роль, scoped capture ownership, отсутствие legacy processors/sender, fail-stop. **83/83** offline/loopback/real-PG проверок PASS. Mode по умолчанию не выбран; роль/host не развёрнуты. Следующий gate — отдельная live read-only qualification; #73 открыт, B4/B5 readiness не заявляется.
> **B3, manifest preflight 2026-11-04:** [публичный каталог native metadata и проверка явного плана](workstreams/B3_01_INSTRUMENT_MANIFEST_2026-11-04.md) добавлены в connector; **35/35** catalog/parser cases PASS, два public mainnet GET через новый CLI PASS (BTC future/options). Private live всё ещё NOT_RUN: выбранная владельцем существующая B3 server binding в доступных настройках пока не найдена; создавать новую или выводить account identity из fingerprint нельзя. #73 открыт.
> **B3, live protocol proof 2026-11-04:** [защищённый qualification profile и ограниченный private-session probe](workstreams/B3_01_PRIVATE_SESSION_PROBE_2026-11-04.md) реализованы для уже сохранённого credential; повторный ввод ключей не нужен. **108/108** regression cases PASS. Отдельный настоящий 45-second WSS run PASS: auth/account pin/subscribe, 2/2 heartbeat replies, 0 problems. Private events=0, durable writes=0, DB used=false; WS→PG qualification, независимая account hierarchy и B4/B5 остаются открыты. Opaque fingerprint/name не заменяют native ID или owner attestation. #73 открыт.
> **B3, isolated live inbox 2026-11-04:** [реальный capture store подключён к live-сессии в marked clone под restricted role](workstreams/B3_01_LIVE_INBOX_2026-11-04.md). **117/117** offline/real-PG/fixture cases PASS. Live 45 s: `LIVE_INBOX_CONNECTED_NO_EVENTS`, auth/pin/subscribe и heartbeat 2/2, environment/cleanup PASS, legacy untouched. Natural events=0, поэтому live commit не квалифицирован и #73 открыт. Следующий полезный increment — B4 recovery/coverage contracts и bounded queries, без бесконечных quiet-прогонов или создания ордеров ради доказательства.

> **B3, finite testnet acceptance 2026-11-04:** [отдельный сценарий одного ордера и отмены](workstreams/B3_02_TESTNET_ORDER_2026-11-04.md) реализован: bounded test risk, durable command/attempt до отправки, targeted cleanup и подтверждение open/cancel facts в PG. **132/132** scripted-exchange/real-PG cases PASS, включая потерянные ACK, unresolved cancel и unexpected fill. Владелец явно разрешил один такой testnet-прогон; повторное разрешение не требуется. Следующее внешнее действие — этот запуск после локального сохранения отдельного testnet-ключа; actual testnet trading **NOT_RUN**, ключ в известных хранилищах отсутствует. Mainnet остаётся read-only; больше quiet-прогонов вместо проверки события не планируется. Это acceptance harness, не production sender/B4/B5 readiness.

> **B3, testnet key and actual preflight 2026-11-04:** [отдельный ключ создан, зашифрован и проверен реальным API](workstreams/B3_02_TESTNET_KEY_2026-11-04.md). `account:read`, `trade:read_write`, остальные capabilities none; разрешение на один BTC-PERPETUAL ордер уже получено. Исправлены JSON-RPC HTTP POST и короткая REST session вместо connection token; **134/134** scripted/real-PG regression PASS. Настоящие auth/account/orders/positions запросы PASS, BTC orders/positions=0. Рынок BTC-PERPETUAL отвечает `halted`: placement/cancel=0, actual trading NOT_RUN, единственная попытка не израсходована. Следующий шаг — этот же bounded testnet cycle после `open`; повторный поиск ключей/разрешения не нужен, recurring agents не создаются.

> **Решение владельца, mainnet ETH 2026-11-04:** не ждать testnet; продолжить на ранее сохранённом реальном малом счёте. Владелец обновил торговое право существующего ключа и выбрал **ETH-PERPETUAL, минимум 1 USD**. Сохранённый native pin совпал, реальный API подтвердил trade:read_write и открытый рынок. [Первый ограниченный прогон](../architecture/DERIBIT_MAINNET_ETH_QUALIFICATION.md): один post-only ордер не более 1 USD native amount, адресная отмена, без повторной отправки/автоматического flatten, постоянный OMS journal и сравнение исходной позиции. Ключ, счёт и разрешение повторно не запрашивать. Обычные host switches остаются false; broad production execution/B4/B5 не квалифицированы.

> **B3, actual mainnet ETH proof 2026-11-04:** [один настоящий ETH-PERPETUAL ордер 1 USD выставлен и адресно отменён](workstreams/B3_03_MAINNET_ETH_2026-11-04.md), `MAINNET_ETH_CAPTURE_PASS`. Open/cancel факты сохранены в постоянный PostgreSQL: commands/attempts=2/2, inbox/observations/incidents=2/2/0. Исполнений нет, исходная позиция сохранилась, environment/peers PASS. Повторного ордера не было, marker завершённого прогона остаётся. Ключ и native pin прежние; права обновил владелец. #73 не закрывается автоматически; B4/B5/FIX и account-wide readiness остаются отдельными gates.

> **B4-01, 2026-11-04:** [реализовано восстановление native order ledger из сохранённых и recovery-фактов](workstreams/B4_01_ORDER_RECOVERY_2026-11-04.md): dedup WS/history/ledger, scope/epoch/window/coverage, сверка отдельных сделок с cumulative quantity, явные блокирующие расхождения вместо выдуманных отмен. Полный прогон 247/247; после добавления account/epoch/window в результат — 47/47 affected recovery cases, включая два PG replay, environment/cleanup PASS. Торговых действий не выполнялось. Следующий шаг — scoped/version/hash-validated durable reader и checkpoint/incidents, затем coherent read-only collection history/fills/positions/balances и durable projection/recovery gate. B4/READY и B5 sender fencing ещё не завершены; #73 остаётся открыт.

> **B4-02, 2026-11-05:** [реализован scoped durable reader и связка с восстановлением ордеров](workstreams/B4_02_DURABLE_READER_2026-11-05.md). **87/87** affected recovery/contract/PG cases PASS: read-only repeatable-read snapshot, canonical SHA/schema/scope, accepted inbox/observations, quarantine и любые scoped incidents блокируют projection. Полный bounded reread не пропускает поздние commit и не выдаётся за resumable checkpoint. Миграции, durable v1/v2 encoding и host execution flags не изменены. Следом — opt-in read-only binding/qualification постоянного store и coherent bounded venue queries, затем позиции/балансы, durable projection/recovery gate; B4/READY и B5 fencing не завершены. Реальных торговых действий не было; #73 открыт.

**Status:** architecture and delivery roadmap integrated into main; Deribit-only V2 scope remains current; dedicated owner-approved ETH qualification is separate from global execution enablement.
**Date:** 2026-10-25.
**Owner reference:** [ZentoEra Senior Quant Developer](https://www.zentoera.com/careers/quant-developer); [TradeOps-v2 issue #40, Deribit FIX 4.4](https://github.com/avysotsky/TradeOps-v2/issues/40).
**Canonical repositories:** public `avysotsky/TradeOps-v2` is the implementation; public, sanitized `avysotsky/alexvysotsky.com` is a **UI/UX and integration-behavior reference only**, NOT a backend to transplant.
**Tech decision:** .NET 10 / C# owns trading domain, executions, reconciliation, risk and platform APIs; React + TypeScript for the UI; PostgreSQL for transactional truth and event/audit ledgers; ClickHouse for historical market data / analytical replay; Python research adapters where valuable; C++/SBE only after measured need. ZentoEra's role explicitly calls for Python and C++; TradeOps targets its *functionality*, not an identical technology stack.

## Platform acceptance infrastructure policy (owner decision, 2026-10-25)

**GitHub Actions is PROHIBITED for TradeOps-v2.** Treat GitHub only as Git/Issues/Draft PR/review transport; never request Actions permissions, enable runs, or gate work on hosted checks. Build and test each exact current PR HEAD in the owner's isolated **local WSL2 Ubuntu + Docker** environment. Use .NET 10, Node 24 and disposable PostgreSQL 17; verify source SHA before/after; preserve separate worktrees, time-stamped logs, exit codes and test totals. The absence of GitHub Actions is not an incident or a blocker. A test without real local execution is still NOT_RUN. [Corrected CI-01 specification #65](https://github.com/avysotsky/TradeOps-v2/issues/65) / [Draft PR #66](https://github.com/avysotsky/TradeOps-v2/pull/66).

## 0. Non-negotiable invariants
1. The owner-approved small-account ETH-PERPETUAL case above is the explicit mainnet qualification exception. Ordinary ExecutionEnabled=false AND MainnetExecutionEnabled=false remain unchanged. No other accounts, larger amounts, arbitrary mainnet orders, cancel-all or position flattening are authorized by this exception; no testnet waiting is required for this bounded case.
2. A **single active execution owner per venue/account/environment**. DB leases/fencing alone cannot prevent a paused old process from sending to an exchange: all outbound commands MUST pass through a sole credential-owning execution gateway that validates a current fencing token immediately before physical send, drains/stops stale workers, and fails closed across failover. Use deterministic account-scoped order identity. No local OrderId, venue OrderId, ClientOrderId/label, execution ID, or FIX ID reused across accounts/venues.
3. OMS is sole authority for commands and persisted local state; exchange is external source of observed facts. UI and strategies only submit intents to OMS; neither may infer success from HTTP 200 or WS RPC response alone.
4. Persist validated command intent and order identity *before sending*; write command state / audit atomically with the journal. A timeout/disconnect after possible transmission is AMBIGUOUS/UNKNOWN and MUST NOT blindly retry. Reconcile via order ID, label, fills, histories; no-result lookup by itself is not proof of non-execution.
5. Recovery is mandatory after all session loss, restart, account switch, failover and sequence gaps: block risk-increasing orders, re-subscribe, obtain coherent REST/RPC snapshots and fills covering the gap, deduplicate/replay buffered events, compare, clear gates only on verified consistency. Never equate missing open orders with canceled orders.
6. Cross-venue transactions cannot be atomically rolled back after fills; use an explicit, risk-budgeted multi-leg **saga** with compensate/unwind/hedge policy and recorded residual exposure.
7. No secrets in Git, public logs, browser, telemetry or test fixtures; segregate credentials by environment and role, redact all FIX/WS auth and sensitive account fields. All positions, orders, PnL and margin scoped to actual account.
8. Versioned contracts, contract tests, deterministic replay/fault injection, immutable audit, metrics and rollback plans are part of each stage, not deferred hardening.

## 1. Existing baseline (verified from published source, not a production audit)
- TradeOps-v2 has `OrderManager`, `OrderStateMachine`, `OrderReconciliationService`, `PositionReconciliationService`, persistent `Order` and lifecycle records, `IExchangeClient`, `IExchangeEventStream`, and adapters for Deribit, Binance, Bybit, OKX, Hyperliquid, IBKR, etc., with *different readiness levels*. An adapter existing is NOT proof of production support.
- `DeribitTestnetExchangeClient` uses HTTP JSON-RPC for trading, with `label` and ambiguous timeout recovery; `DeribitPrivateWebSocketStream` consumes private order/trade notifications in testnet. Neither constitutes complete OMS / exchange recovery by itself.
- Draft PR #54 is clock diagnostics stacked on RAW order book work; Draft PR #55 is browser-side private REST snapshot freshness, temporarily polled every 8 s. #55 is **not** intended as the long-term authoritative OMS event state. Don't merge or change active branches without CI/owner gates.
- VAN UI reference: `frontend/dist/deribit.html`, `frontend/dist/coincall.html`, `frontend/dist/coincall-spreads.html`, `frontend/dist/assets/deribit-*.js`, `frontend/dist/assets/coincall-spreads-full.js`, and tests. Two `*-spread-workstation.html` entries in sanitized snapshot are empty placeholders; do not claim working markup in them. VAN backend is a reference only. Preserve its proven compact desktop trading layout and workflows, but reimplement in React with typed contracts and accessible responsive behavior.
- Deribit guidance (updated Sep 2026) recommends persistent JSON-RPC WS; provides HTTP and legacy FIX. Dedicated FIX internet endpoints `fix-test.deribit.com:9883` and `fix.deribit.com:9883` (TLS). FIX remains an optional adapter; Starbase SBE/Drop Copy is distinct and requires eligible connectivity. Deribit connection/session limits must be accounted for.

## 2. Target system boundaries
```text
React Trading Workstations (Deribit / CoinCall / Multi-venue / Risk / Analytics)
         | typed REST commands + SignalR/WebSocket read models
API Gateway/BFF, auth, RBAC, operator audit
         |
         +--> Strategy Runtime / External Signals --> Pre-trade Risk ----+
         |                                                               |
         +--> OMS Command Ingress --> durable intent/outbox --> Executor --+--> Exchange Adapters
         |                                    |                             (WS JSON-RPC / FIX 4.4 /
         |                                    v                              REST recovery / IBKR native)
         |                           Order + Execution Ledger                 |
         |                                    ^                                v
         +<-- materialized read models <--- Ingest/Dedup <--- Private WS / FIX ExecutionReport
                                               ^
                                      Recovery / Reconciliation
                  PostgreSQL (authoritative state, ledger, outbox, account leases)
                  ClickHouse (tick/L2/funding/basis analytical market data, replay)
                  Metrics/Tracing/Alerts, disaster-recovery and change control
```
Order state and exchange execution truth never originate in React. Market L2 and private account streams have separate QoS, reconnect and recovery rules. Never use ClickHouse as the authoritative order store.

## 3. Delivery sequence (implementation waves; two coordinated tracks)
Every gate requires a named PR, repeatable tests, acceptance report and owner sign-off; parallel tracks must not silently merge into one another.

### G0 — Baseline and release-control inventory (blocking, docs/test changes only)
- Pin and audit current main + PR #53/#54/#55 ancestry and test status; do not mistake Draft for merged.
- Build capability/readiness matrix per venue/protocol/environment; enumerate `ExecutionEnabled` and safety gates. Document process topology, secret custody, UI pages and REST/WS contracts.
- Inventory VAN screenshots/page DOM, desktop dimensions, tabs, order ticket, account selector, order book, positions, orders, history, funding, spread/basis, portfolio. Preserve behavior and styling via screenshot baselines; do not copy its backend.
- **Exit:** source/feature inventory, dependency graph, contract risk ledger, signed baseline and invariant tests.

### B1 — Canonical identifiers, domain contracts and OMS lifecycle
- Define `AccountKey(tenant,venue,environment,account/subaccount)`; local intent ID (immutable), command ID, client order ID (venue-constrained), exchange order ID, order version, execution/trade ID, FIX ClOrdID/OrigClOrdID/ExecID with venue-specific mappings.
- Distinguish intent -> command dispatch -> exchange acknowledged -> partially filled -> filled/canceled/rejected; Unknown/CancelPending/ReplacePending and exchange-only orders. Immutable creation terms; decimal + units/currency/instrument metadata.
- Define typed ports: `IOrderEntryTransport`, `IPrivateExecutionStream`, `IExchangeRecoveryQueries`, `IVenueCapabilities`, `IInstrumentRules`. Do not force unsupported broker semantics into a fictional common denominator.
- **Exit:** full state transition table and unit tests for duplicate IDs, out-of-order fills, cancel/fill race, terminal-state invariants, account isolation.

### B2 — Durable command journal, deduplication and single writer
- PostgreSQL tables: intents, commands/attempts/outbox, orders/current projection, raw normalized events/inbox, fills (unique account+venue+execution identity), reconciliation runs/issues, account leases/fencing, immutable audit.
- Persist before dispatch; atomic transactional outbox; distinguish `not_sent`, `dispatch_started`, `acknowledged`, `ambiguous`. Stale-leader fencing and conservative unknown handling. Account-specific monotonic versions / event ordering.
- **Exit:** kill process at every send/commit boundary; replay without duplicate local intents, no blind resend after possible transmission, no cross-account collision. Prove old gateway cannot send after new owner takes over (including in-flight/stalled packets); where exchange cannot honor a remote fencing token, avoid overlapping connectivity/credential ownership and explicitly classify remaining failure uncertainty. PostgreSQL rollback/recovery evidence.

### B3 — Deribit primary private WebSocket order execution (testnet first)
- Long-lived dedicated authenticated command WS + independent private events WS; JSON-RPC ID correlation and timeout; bounded buffers, heartbeat, scope/refresh, reconnect/backoff and connection limits. Market public RAW remains isolated.
- Native placement/edit/cancel only behind OMS+Risk; normalize server reports and `user.orders` / `user.changes` / trades; support all required instruments with tick, min amount, contract units and fee precision.
- REST/RPC retained for snapshot/history lookups. Existing browser polling is replaced by backend-owned read model events, not eliminated as a recovery option.
- **Exit:** mock transport tests including dropped ACK and network partition; controlled buy/cancel/partial-fill evidence. The owner-approved 1 USD ETH mainnet case has its own limits and evidence; other mainnet mutations remain outside that authorization.

### B4 — Exchange/OMS Reconciliation Engine
- Reconnect gate: `DISCONNECTED -> RECOVERING -> RECONCILING -> READY`; suppress new risk-increasing orders until coherent baseline.
- Capture subscription epoch and buffer events; fetch comprehensive open orders + history + trade fills + positions + balances for all relevant symbols/currencies and entire outage interval with overlap/safety margin. Compare both directions (local->exchange, exchange->local). Confirm closed via history/fills, not empty open-order snapshot.
- Deduplicate fills by exchange identity, handle revision/partial fill, unknown external orders, canceled-but-filled races, unavailable history and inconsistent cumulative quantities. Persist issues and operator overrides (audited).
- **Exit:** deterministic replay of outage at every transition, 0 double-counted fills, no fabricated cancellations, fail-closed on unresolved gap, alerts.

### B5 — Pre-trade and runtime Risk Engine / kill switch
- Every order command checks instrument constraints, allowed-account policy, per-order notional, position delta, portfolio exposure, leverage/margin, reduce-only, cumulative pending risk, loss and concentration controls.
- Account/venue/global circuit breakers, human override RBAC, immutable audit; kill switch blocks new orders and triggers documented cancellation/flatten policy with unknown-state recovery (cancel acknowledgements are not proof of completed cancel).
- **Exit:** risk denial can never be bypassed by UI/strategy/direct adapter; stochastic partial-fill and sudden-gap tests, operator drill for kill switch.

### B6 — Deribit FIX 4.4 adapter (existing issue #40)
- Separate persistent FIX session + dictionary and encoded message store, sequence/resend/gap fills, logon/heartbeat/test request/logout; QuickFIX/n evaluation/maintenance, dictionary/Deribit custom tags, TLS and certificate verification.
- Normalize NewOrderSingle, CancelReplace, Cancel, ExecutionReport; map venue-specific ClOrdID fields without assumptions. Feed same OMS command/event contracts. FIX recoveries still require B4 account/fill reconciliation.
- **Exit:** offline FIX replay including duplicated ExecID, gaps, reset/sequence recovery; separate operator-authorized TESTNET login/order/cancel report. Fix feature flag OFF in production by default. Not a prerequisite for WS-based first credible OMS.

### B7 — Multi-venue adapter qualification
- Start with Deribit and CoinCall, then Binance/Bybit/OKX/Hyperliquid, then expand to 10–20 *qualified* venues by independent capability certifications. Leverage existing adapters with readiness reviews; implement native broker API paths for IBKR where permitted.
- For each: instrument mapping, qty units, client IDs, order types, reduce-only, margin mode, websocket liveness, recoverability, pagination, scopes, rate limits, cancels and execution IDs. No blanket claim that all venues support FIX.
- **Exit:** per-venue contract suite + mock fault injection + sandbox evidence + documented unsupported features; multi-account isolation.

### B8 — Multi-leg EMS and hedge recovery
- Explicit execution plan/group ID, simultaneous (as feasible) independent sends, leg deadlines, partial-fill ratio, residual delta, target hedge, maximum unhedged risk, risk-weighted routing.
- Saga states `PLANNED/SUBMITTING/PARTIAL/EXPOSED/HEDGING/COMPENSATING/COMPLETE/FAILED_MANUAL` with bounded retries only after known outcomes. No real atomic rollback between exchanges.
- **Exit:** simulation of one-leg fill + other-leg rejection, both unknown, disconnect during hedge; position exposure and max-unhedged-duration SLO enforced with risk shutdown, no duplicate hedge.

### B9 — Market data normalization and analytics
- Per-venue public WS book/trade/funding/mark/index streams, L2 sequence + snapshot validation and time sync, contracts, fees and borrow/funding currency normalization.
- Reliable async ingestion to ClickHouse: raw+normalized ticks, funding, basis, bars, book checkpoints; defined retention/partitioning, idempotent replay, late/out-of-order handling. Keep hot in-memory book and risk path independent from ClickHouse lag.
- **Exit:** reproducible replay identical to normalized source events, fault and throughput benchmarks, documented loss/gap detection.

### B10 — Funding/basis/arbitrage scanners and research APIs
- Opportunity ranking after **maker/taker fees, expected slippage, depth, transfer/borrowing/funding, expected execution risk and capital/margin costs**. No rankings by raw funding alone.
- Cross-venue contract and hedge compatibility; scan staleness flags, reference pricing and units. Strategies never call exchange adapters directly.
- **Exit:** deterministic fixtures with negative net opportunities suppressed, costs explained, UI scanner available read-only.

### B11 — Historical backtesting / replay and strategy runtime
- Event-time reproducible playback, tick/L2/funding/mark and fill simulator (queue/partial fills/cancel latency/rate limits/slippage/fees/funding, uncertainty ranges); no peeking into future data.
- Deployment modes backtest -> paper -> sandbox -> gated live, same risk and command contracts, deterministic run version/data/config.
- **Exit:** seeded replay reproducibility, benchmarks and comparison paper-vs-replay with limitations reported; pluggable strategies.

### B12 — Operations / observability / security / HA
- Structured events, P50/P95/P99 command-to-ACK/fill delay, disconnection windows, quote age, reconciliation time, unresolved commands, risk trips, loss/recovery metrics. NTP/clock offset alerting.
- Tenant isolation, RBAC, environment-specific key storage and credential rotation, immutable audit chain, DB backup/restore, ClickHouse data recovery, disaster drills, schema migrations, capacity/rate-limit tests, CI supply chain scans.
- **Exit:** demonstrable recovery from crash, failover with fencing, DB restore + reconciliation, no credential leaks, external review and operational runbooks.

### F1 — VAN Deribit React UI fidelity (starts after G0; can run parallel to B1–B6)
- Build component inventory from `deribit.html` and `assets/deribit-*.js`. Replace DOM/global-function orchestration with typed React state/hooks, scoped CSS/design tokens and a versioned UI contract.
- Retain existing professional desktop layout: compact multi-row header, instrument selector, RAW order book+precision/depth, public trades, chart, order ticket, order/position table, account, history, fees/funding, spread workstations, responsive modal/docking flows. Do **not** regress verified RAW L2 behavior.
- **Exit:** screenshot/golden at defined viewport sizes, keyboard/navigation/e2e interaction parity; all previews read-only with mock data before B4/B5 readiness.

### F2 — VAN CoinCall React UI fidelity and venue features
- Inventory `coincall.html`, `coincall-spreads.html`, `assets/coincall-spreads-full.js`; build separate CoinCall workstation and shared cross-venue component package.
- Spot/futures/options if present/qualified, spread and two-leg panels, filters, depths, bid/ask visualizations, account balances/orders/positions/history/funding; CoinCall-specific order ticket and precision rules stay explicit and typed.
- **Exit:** screenshots/e2e parity, synthetic CoinCall adapter data, isolated account contexts and no accidental use of Deribit backend semantics.

### F3 — Shared institutional React workstations
- Compose exchange-agnostic OMS read models via authenticated gateway + SignalR/WebSocket updates: true exchange state, reconciling/unknown/partial, last confirmed times and discrepancy alerts. Avoid browser as an independent order-state cache.
- Portfolio, positions, open/closed orders, fills, exposure and risk, multi-leg hedge dashboard, funding/basis scanner, and replay UI. Commands require role, risk verdict and fresh readiness gate.
- **Exit:** two simultaneous account views without contamination; reload/reconnect display persisted OMS truth; screenshots and browser automation pass.

### R1 — Controlled institutional release/certification
- Complete B2–B5 before *any* high-risk real-money operation; B6 FIX may then be qualified as an additional transport. B7–B12 and F1–F3 deliver functional parity with ZentoEra's platform requirements.
- Feature gates: `mock` default, `sandbox` individually approved, `mainnet-readonly` default; mainnet trading separate security/operational authorization.
- Progressive canary, operator approval, rollback/disable plans and acceptance evidence. Never treat backlog closure as proof of institutional readiness.
- **Exit:** signed release manifest listing qualified venues, exact protocol/permissions/limits, latency and recovery SLO achieved, data provenance and incident runbooks.

## 4. Dependency / delivery priority
- **Critical execution path:** G0 -> B1 -> B2 -> B3 -> B4 -> B5 -> B8 -> R1.
- FIX path: B1+B2 -> B6; integrate B4; existing #40 covers the venue adapter. It is independent of initial WS execution.
- Multi-venue: B1+B2+B4 -> B7 -> B8; 10–20 venues follow capability qualification rather than a fixed large simultaneous release.
- Data/research: G0 -> B9 -> B10 -> B11.
- Frontend: G0 -> F1 and F2 in parallel; connect real private OMS only once B4/B5 read models and gates are ready -> F3.
- SRE/security B12 is continuous; its certification is a gate for R1, not a final cosmetic phase.

## 5. Suggested initial GitHub work packages (one Draft PR per coherent change)
1. **G0-01**: PR-stack audit, VAN UI inventory, test/screenshot fixtures, capabilities matrix; no change to execution.
2. **B1-01**: order/execution/FX-/venue-specific identity and state contract tests.
3. **F1-01**: React Deribit layout shell and screenshot parity, synthetic data and existing RAW book preserved.
4. **B2-01**: durable outbound command journal + outbox + crash-point tests.
5. **F2-01**: React CoinCall terminal shell and regression fixture extraction.
6. **B3-01**: Deribit dedicated private WS read-only events + durable ingress (MAINNET READ-ONLY).
7. **B4-01**: reconciling-state gate and incomplete snapshot/unknown outcome recovery tests.
8. **B5-01**: mandatory risk enforcement, circuit breakers and kill-switch simulation.
9. **B3-02/B6-01**: separately authorized Deribit TESTNET execution and FIX session adapter; no mainnet trading.
10. **B7/B8/B9+**: CoinCall venue qualification, multi-leg EMS, scanners/ClickHouse, replay, remaining qualified adapters and release evidence.
No blind merges from PR #55; front-end REST freshness from #55 is explicitly a transitional read-only UI state before B4/F3 replaces it.

## 6. Acceptance scenarios to automate across PRs
- Lost WS RPC ACK after actual accepted/filled order; user repeats click; process restarts: **one intent; no unqualified retransmission; actual fills discovered**.
- Private event gaps, out-of-order duplicates; snapshot arrives older than an execution: **no status regression or double-booked fills**.
- Cancel during fill, cancel response lost, replace collision: **no claimed cancel without exchange evidence**.
- 2 venues / 2 accounts with colliding local client labels/exchange IDs: **strict account/venue identity isolation**.
- One-leg execution, second venue offline; hedging cannot execute: **bounded residual-exposure alarm + saga recovery**, no fictional rollback.
- Leader failover during dispatch: **new leader cannot race old leader; stale fencing rejected**.
- Kill switch during rapid strategy orders, network partition, bad prices, insufficient collateral: **zero fresh risk-increasing sends after gate**.
- ClickHouse unreachable: **OMS/risk/execution remain functional**, market recorder alarms and buffers with backpressure, never lie about complete history.
- Browsers close/reopen during exchange sessions: **server continues private event capture and reconciliation; UI resumes from authoritative projections**.
- Validate 10–20 qualified venue integrations with exact documented capabilities, not a single passing mock test.

## 7. Requirements traceability against ZentoEra
| ZentoEra requirement | Planned proof |
| --- | --- |
| 10–20+ venue integration, REST/WS/FIX | B6+B7 venue capability ledger and independent contract fixtures |
| Institutional multi-account OMS | B1+B2+B4 durable ledger and full account reconcile |
| Simultaneous cross-exchange arbitrage / rollback | B8 saga + bounded hedge/compensating orders, no fictitious atomic undo |
| Order state, partial fills, audit | B1–B5 + replay and recovery scenarios |
| Funding/basis, depth scanners | B9+B10 normalized market data, net economics and staleness |
| ClickHouse market data pipeline | B9 + B12 performance/data completeness checks |
| Risk Engine, kill switch | B5 + B12 end-to-end no-bypass evidence |
| Historical realistic backtesting | B11 historical replay / simulated execution fixtures |
| Independent strategies / REST APIs | B11 + B12 and versioned OMS BFF contracts |
| Python/C++ stack wording | Intentional nonliteral match: core .NET, optional Python/C++ specialized modules |
| VAN Deribit React UI parity | F1 screenshot + behavior + RAW/clock parity |
| VAN CoinCall React UI parity | F2 screenshot + behavior + venue-specific trading previews |

## 8. Sources
- ZentoEra current posting: https://www.zentoera.com/careers/quant-developer
- Deribit API guidance: https://support.deribit.com/hc/en-us/articles/38507223482781-API-guidance
- Deribit connection best practices: https://support.deribit.com/hc/en-us/articles/25944603459613-Connection-Management
- Deribit FIX hosts: https://support.deribit.com/hc/en-us/articles/25944617582877-Server-Infrastructure
- TradeOps FIX issue #40: https://github.com/avysotsky/TradeOps-v2/issues/40
- VAN sources: https://github.com/avysotsky/alexvysotsky.com
