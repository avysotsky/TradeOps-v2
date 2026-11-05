# Продолжение TradeOps-v2 — полнота B4, retained terminal evidence

## Последний проверенный результат, 2026-11-05

Код `15cd9e605e871aba833ae65a586032309cb966fb`:
[отчёт](workstreams/B4_TERMINAL_EVIDENCE_2026-11-05.md),
[правило](../architecture/DERIBIT_RETAINED_TERMINAL_EVIDENCE.md).
**521/521 + 30/30 PASS**, final environment/cleanup PASS, 332 source hashes совпали.
Новая Application policy проверяет actual snapshot/selection/query bindings,
terminal report, native quantity, dedup и точное совпадение cumulative со всеми
известными executions. Missing/collision/conflicting/late-venue/overflow facts
закрывают proof; full-ledger/current-state/account-wide/coherence не объявляются.

Один live read-only run подтвердил **1 retained terminal order**, без addressed
lookup, повторной торговли или DB writes. Было девять blockers, осталось **семь**:
historical-unfilled gap, incomplete history, no coherent cut, no active buffered
replay, global execution ledger unproven, account snapshot coherence и account-wide
coverage unproven. RetentionExpiredOrders=1 остаётся диагностикой; validated=1
снимает только order-specific retention/unavailable требования к этому ордеру.
Scoped account observations valid (manifest 13 / accepted position rows 2 / balance
observed). READY/publication=false. Journal audit 2/2 и 2/2/0 неизменен, guard/peers PASS.
Diagnostics schema v2 включает validated count; durable v2/v1 encoding прежний.

**Следующий крупный результат:** полный relevant command/order/execution universe
и общий basis snapshot/history/actual capture/replay. Не подменять global coverage
совпадением количества для одного terminal order. Затем persisted recovered state
и end-to-end B4 gate/failure lifecycle по [плану до QuickFIX/n](B4_TO_QUICKFIX_PLAN_2026-11-05.md).
Старый terminal lookup/placement harness не повторять; новые торговые действия не
разрешены. B4/READY, B5 physical sender/risk и #73 остаются открытыми.

Девять blockers и v1 diagnostics ниже — исторический результат предыдущего кода;
текущее правило и семь оставшихся blockers описаны здесь.

## Текущий контрольный результат, 2026-11-05

Ориентироваться на [понятный план B4 → QuickFIX/n](B4_TO_QUICKFIX_PLAN_2026-11-05.md),
а внутренние номера ниже считать технической историей. Единый pipeline теперь
собран и проверен на реальном счёте; B4 полностью ещё не завершён.

Код `42d2f50fa860985f1cfe4f74e74a567a9be76122`:
[отчёт](workstreams/B4_05E_UNIFIED_ASSESSMENT_2026-11-05.md),
[контракт](../architecture/DERIBIT_UNIFIED_READONLY_ASSESSMENT.md).
**471/471 + 25/25 PASS**, offline environment/cleanup PASS, 328 source hashes совпали.
Новый opt-in `--assess-account-recovery` / `Deribit-AccountRecoveryLocal.ps1` связывает
SELECT-only retained reader, actual order/trade/known/account connectors и
`AssessAccountAsync`; public manifest/native pin/key прежнего scope, no trading path.

Один live run: **RECOVERY_ACCOUNT_EVIDENCE_BLOCKED**, AssessmentCompleted=true, exit 0.
Public manifest 13, accepted positions 2, balance observed, account observations valid;
retained/current facts 2/0, known orders 1, addressed 0, retention-expired 1.
Account-wide/coherence/READY/publication=false. Девять blockers: historical-unfilled
gap, incomplete order history, no coherent cut, no active buffered replay, unproven
execution ledger, terminal retention expired/known unavailable, account snapshot
coherence и account-wide coverage unproven. Scoped account data не заменяют proof.
Private values/IDs не публиковались, повторов/торговых вызовов/DB writes=0.
Постоянный audit commands/attempts 2/2, inbox/observations/incidents 2/2/0 неизменен;
guard и peers PASS. Старый terminal lookup и placement/cancel harness не запускались.

**Следующий крупный результат B4:** правила достаточности/полноты ордеров и исполнений,
явная область восстановления и доказательство согласованности snapshot/history с
actual capture/replay. Недоступная история — конкретная блокирующая проблема с
аудируемым разрешением, не бесконечный повтор одного lookup. Затем сохранённая
восстановленная модель счёта и полный restart/gap/failure lifecycle с gate READY.
B5 risk/single sender и обязательный QuickFIX/n остаются по плану; транспортный
каркас FIX можно разрабатывать до финальной квалификации B4/B5, торговый допуск — после.
Новое торговое действие этим этапом не разрешено; #73 открыт.

Указание unified host как «следующего шага» ниже заменено этим дополнением.

## Последнее дополнение 2026-11-05 — B4-05d account assessment integration

Код `c13f02a2760ca1474b5f9bad7599471d06c6e375`:
[отчёт](workstreams/B4_05D_ACCOUNT_ASSESSMENT_2026-11-05.md),
[контракт](../architecture/DERIBIT_RECOVERY_ACCOUNT_ASSESSMENT.md).
**445/445 PASS**, environment/cleanup PASS, 323 source hashes совпали.
Explicit `AssessAccountAsync` / `AssessAccountWithReplayAsync` связывают actual
account port с Application gate; active window включает account read до seal.
Trusted expected manifest и raw collections копируются до awaited lifecycle;
scope/epoch/currency/kind/native units/window/freshness проверяются при финальном
использовании после drain. Partial/stale/foreign/malformed закрываются с typed
blockers; invalid retained evidence пропускает account read. Exceptions/cancellation
не удаляют newer superseding window, stale active epoch/session закрывает acceptance.

Два actual PG cases: production account reader через scripted HTTP, commit во время
positions RPC; success и lost ACK после реального commit без resend. Это **offline/
DB-only**, без vault/live account/mainnet journal/trades. Private amounts/rows не
выдаются и не публикуются. Account availability и local replay не снимают venue/
ledger/history blockers; AccountSnapshotCoherenceUnproven и AccountWideCoverageUnproven
добавляются всегда. AccountReady/publication=false; durable encoding прежний.
Старый B4-05c live PASS не обновлялся. Existing live hosts пока не используют новые entries.

**Следующий increment B4-05e:** bounded opt-in read-only host/qualification
`AssessAccountAsync`: existing saved native pin/key, public ETH/future expectations,
retained SELECT-only reader, только approved anonymous counts/typed blockers.
Не повторять placement/cancel harness или terminal lookup вне retention. Active
replay требует actual server-owned capture session, не caller flags. Далее venue
coherence/full relevant execution ledger, account-wide coverage, durable projection/
gates; B4/READY, B5 fencing и #73 открыты. Новое торговое действие не разрешено.

Указание B4-05d как «следующего шага» ниже заменено этим дополнением.

## Последнее дополнение 2026-11-05 — B4-05c explicit ETH/future manifest

Код `5eb68c0f37ad38a62b6ddba8a4338eb0117d30ec`:
[отчёт](workstreams/B4_05C_ETH_FUTURE_MANIFEST_2026-11-05.md).
**394/394 PASS + 32/32** diagnostics-validator cases, environment/cleanup PASS,
320 source hashes совпали. Отдельный `--read-eth-future-snapshot` и wrapper
`-Manifest EthInverseFutures` требуют нового purpose; default single-instrument
режим сохранён. Policy EthActiveInverseFuturesV1 выбирает 1–256 active inverse
ETH futures из bounded public catalog до auth, проверяет age/env/units/currencies,
замораживает manifest. Unsupported active metadata закрывает весь manifest;
inactive/expired/unmapped private rows всё ещё blocker, без fallback/retry.

Один live read-only запуск: **ACCOUNT_SNAPSHOT_SCOPED_PASS**, exit 0, timestamp
`2026-11-05T14:20:44.2199014Z`. Public manifest=13, accepted position rows=2,
BalanceObserved=true, MetadataValidated/QueriesCompleted/NativeBindingVerified/
FreshAtAssessment=true, Problems empty. Private значения/имена/native IDs не
публиковались. Это scoped ETH/future observation, не account-wide snapshot:
AccountWideCoverage/VenueCoherent/AccountReady/publication/TradingReady=false. DB/writes/mutating
calls=0; permanent journal не открывался, старый harness/terminal lookup не повторялся.
Старый B4-05b INCOMPLETE не переобозначен; это отдельный явно выбранный group mode.
Diagnostics SchemaVersion=2 с ManifestPolicy/count binding; durable encoding прежний.

**Следующий increment B4-05d:** scoped account port в Application assessment:
actual scope/epoch/manifest/currency/kind/window/freshness validation при использовании,
private values только in memory, не доверять saved diagnostics или success flags.
Active capture/local replay не заменяют venue coherence/full execution ledger.
Account-wide coverage, durable projection/gates, B4/READY, B5 fencing и #73 открыты.
Новое торговое действие этим кодовым этапом не разрешается.

Указание B4-05c как «следующего шага» ниже заменено этим дополнением.

## Последнее дополнение 2026-11-05 — B4-05b read-only qualification host

Код `a8e79832b70775fb6fef83fe7310747ca121a56e`:
[отчёт](workstreams/B4_05B_ACCOUNT_QUALIFICATION_2026-11-05.md).
**364/364 PASS**, дополнительно **22/22** Windows diagnostics-validator cases;
environment/cleanup PASS, 318 source hashes совпали. Explicit
`--read-account-snapshot` и `Deribit-AccountSnapshotLocal.ps1`: saved DPAPI key/native
pin, active public ETH-PERPETUAL metadata validation до auth, production reader,
только whitelist diagnostics; без DB/capture/trading path, execution switches false.

Один live mainnet-запуск: **ACCOUNT_SNAPSHOT_INCOMPLETE / UnmappedInstrument**,
MetadataValidated=true, exit 1. В ETH/future ответе есть инструмент вне manifest
ETH-PERPETUAL; private значения и имя не публиковались. Принятых rows=0 и
BalanceObserved=false означают отказ от выдачи всего набора, не нулевой счёт.
QueriesCompleted/NativeBindingVerified/FreshAtAssessment=false; account-wide
coverage/coherence/READY/publication=false. DatabaseUsed=false, writes/mutating
calls=0. Повтора, автоматического расширения manifest, старого order harness или
terminal lookup не было; постоянный mainnet journal в этом этапе не открывался.

**Следующий increment B4-05c:** явно задать поддерживаемый ETH/future manifest из
trusted public metadata до следующего opt-in чтения. Не фильтровать unknown row,
не выводить units из имени и не расширять запрос на другой account/currency/any.
Unavailable/expired metadata оставлять blocker. После квалификации coverage —
account port в Application assessment. Venue coherence/full execution ledger,
durable projection/gates, B4/READY, B5 fencing и #73 остаются открытыми.
Новое торговое действие этим кодовым этапом не разрешается.

Указание B4-05b как «следующего шага» ниже заменено этим дополнением.

## Последнее дополнение 2026-11-05 — B4-05a scoped account reader

Код `04a2762343d77b3dd8036a410607ca72016a7c9d`:
[отчёт](workstreams/B4_05A_SCOPED_ACCOUNT_SNAPSHOT_2026-11-05.md),
[контракт](../architecture/DERIBIT_SCOPED_ACCOUNT_SNAPSHOT.md). **336/336 PASS**,
environment/cleanup PASS, 314 source hashes совпали. Application port + независимый
connector читают один BTC/ETH currency/kind group: inverse futures в USD с отдельным
base size, options в base coin; cash balance/equity выбранной валюты и margin flags.
Native identity до/после и на position rows, явный manifest, row/byte/time bounds,
observation windows и проверка свежести при использовании. Unknown rows/ошибки/
stale очищают весь private result; пустой массив не создаёт zero positions.

Это **offline/DB-only**, без vault/mainnet store/live venue/trades. Account-wide
coverage, venue coherence, AccountReady и durable publication всегда false.
Reader пока не подключён к application gate/host и не меняет его blockers.

**Следующий increment B4-05b:** opt-in read-only host/wrapper с существующим saved key
и encrypted native pin; public catalog validation и только анонимные diagnostics
охвата/свежести/статуса. Не публиковать позиции/балансы/native IDs; не повторять старый
placement/cancel harness и lookup terminal order. Затем integration с assessment,
отдельно venue snapshot/history coherence и полный relevant execution ledger,
durable projection/gates. B4/READY, B5 fencing и #73 остаются открытыми.
Новое торговое действие этим этапом не разрешается.

Указание scoped reader как «следующего шага» ниже заменено этим дополнением.

## Последнее дополнение 2026-11-05 — B4-04c active replay gate lifecycle

Код `35cc3f6`: [отчёт](workstreams/B4_04C_ACTIVE_REPLAY_GATE_2026-11-05.md),
[контракт](../architecture/DERIBIT_ACTIVE_REPLAY_GATE.md). **294/294 PASS**,
environment/cleanup PASS, 311 source hashes совпали. Новый explicit application entry
`AssessWithReplayAsync` использует реальный active coordinator/session: begin до
snapshot/query/known reads, seal, bounded drain и финальная cut revalidation.
Всегда cleanup только своего окна; timeout/exception/cancellation не блокируют
независимый capture commit и не удаляют более новое superseding window.

Валидный local prefix снимает **только BufferedReplayUnavailable**. Venue coherence,
history/ledger gaps, AccountReady=false и DurableProjectionPublished=false остаются.
Replay payload не применяется к reducer и не публикуется. Два новых PG integration
cases проверили actual reader + overlapping commit и lost-ACK без resend.
Это **offline/DB-only**: mainnet store/vault/venue/trades не использовались.
Старые live qualification hosts не имеют active capture и остаются на base assessment.

**Следующий increment:** scoped read-only positions/balances с явными native units,
currency coverage и freshness/incompleteness, сохраняя границы connector/application.
Параллельно требуется venue snapshot/history coherence и полный relevant execution
ledger; local drain не заменяет proof и не сдвигает query cutoff на receive time.
Затем durable projection/recovery gate; B4/READY, B5 fencing и #73 остаются открытыми.
Новые торговые действия не нужны и не разрешаются этим кодовым этапом.

Указание B4-04c как «следующего шага» ниже заменено этим последним дополнением.

## Последнее дополнение 2026-11-05 — B4-04b local capture cut/replay

Код `e304917`: [отчёт](workstreams/B4_04B_CAPTURE_REPLAY_2026-11-05.md),
[контракт](../architecture/OMS_PRIVATE_CAPTURE_REPLAY.md). **275/275 PASS**,
environment/cleanup PASS, 308 source hashes совпали. Opt-in replay limits в coordinator;
по умолчанию tracking disabled. Deribit adapter регистрирует receipts до queue,
не ожидая DB commit; active scope/epoch/runner identity проверяются, empty epoch запрещён.
Begin включает pending overlap, seal фиксирует received prefix, read до полного commit
не выдаёт фактов; gap/overflow/lost-ACK/stale binding/reuse/supersession/expiry блокируют.

Три новых настоящих PG cases: committed Inserted/Duplicate prefix сверяется production
reader; lost ACK после настоящего commit не вызывает resend и блокирует replay;
scripted adapter/parser/receipt→PG path проверен. **Это offline/DB-only**, не новый live
event/recovery/READY. Mainnet store/vault/venue не использовались; launchSettings прежний.
Local CanReplay не означает VenueCoherent/AccountReady и не является durable checkpoint.
Текущий evidence gate пока сохраняет BufferedReplayUnavailable/CoherentCutUnavailable.

**Следующий increment B4-04c:** active window/cut lifecycle в evidence-gate orchestration,
bounded drain и финальная token/scope/epoch revalidation. Разделять local prefix и
venue snapshot/history coherence; не подменять proof caller-флагами и не сдвигать
query cutoff на receive time. Затем complete ledger, positions/balances, durable
projection/gate; B4/READY, B5 fencing и #73 незавершены. Торговое разрешение не расширено.

Указание B4-04b как «следующего шага» ниже заменено этим последним дополнением.

## Последнее дополнение 2026-11-05 — B4-04a evidence gate

Код `d2be4cf3ea5fa31689234cdce9abf74083c1db10`:
[отчёт](workstreams/B4_04A_EVIDENCE_GATE_2026-11-05.md),
[контракт](../architecture/DERIBIT_RECOVERY_EVIDENCE_GATE.md). **209/209 PASS**,
environment/cleanup PASS, 305 source hashes совпали. Application gate связывает
retained reader → bounded query sources → только нужные addressed lookups, проверяет
scope/epoch/snapshot/window/provenance. Старые terminal orders вне 30 минут не polling;
old open orders всё ещё адресуются, Missing никогда не превращается в cancel.

Live `Deribit-RecoveryAssessmentLocal.ps1`: **RECOVERY_EVIDENCE_BLOCKED**, retained/current
facts **2/0**, known orders **1**, addressed **0**, retention-expired **1**. Семь blockers:
historical-unfilled gap, incomplete order history, coherent cut unavailable, buffered
replay unavailable, execution-ledger coverage unproven, terminal retention expired,
known-order unavailable. CanRebuildOrders/AccountReady/DurableProjectionPublished=false.
Audit commands/attempts **2/2**, inbox/observations/incidents **2/2/0** неизменен;
guards/peers PASS, writes/trading **0**. Это проверенный отказ, не завершённое recovery.

**Следующий increment B4-04b:** qualified coherent cut и bounded buffered replay evidence
с привязкой к активному capture scope/epoch, явными gap/overflow/stale-epoch blockers.
Не подменять proof caller-флагами complete и не сдвигать cutoff на receive time.
Затем complete relevant execution ledger, positions/balances и durable projection/gate.
B4/READY, B5 fencing и #73 остаются незавершёнными. Новые торговые действия и повтор
lookup старого terminal order не нужны и не разрешаются этим кодовым этапом.

Указание recovery evidence gate как «следующего шага» ниже заменено этим дополнением.

## Последнее дополнение 2026-11-05 — B4-03c и unavailable evidence

Код `2dafd818b24d7b57b76b59019bb7ef49222021cf`:
[отчёт](workstreams/B4_03C_KNOWN_ORDER_READS_2026-11-05.md),
[контракт](../architecture/DERIBIT_KNOWN_ORDER_READS.md). **173/173 PASS**,
environment/cleanup PASS, 302 source hashes совпали. Application selector из scoped
durable snapshot и отдельный connector port для 1–32 адресных known orders.
Live `Deribit-KnownOrderRecoveryLocal.ps1` выбрал из постоянного SELECT-only snapshot
один известный ордер; один get_order_state дал **10004/Missing**. QueriesCompleted=true,
AllOrdersFound=false, CurrentFacts=0, RetainedFacts=2. Это **KNOWN_ORDERS_UNRESOLVED**,
не успешное восстановление текущего статуса. Native IDs/секреты не публиковались.
Audit commands/attempts **2/2**, inbox/observations/incidents **2/2/0** неизменен,
guards/peers PASS. Новый ордер, повтор старого harness или запись в БД не выполнялись.

У `get_order_state` нет historical flag и закрытые ордера доступны лишь 30 минут.
Старый cancel остаётся сохранённым фактом, Missing не означает свежую отмену,
absence или zero fills. Повторный запрос старого ордера не исправит retention gap.

**Следующий increment:** application recovery orchestration/gating с явными
unavailable-evidence, retention/coverage/coherence blockers и scope/epoch binding.
Не объявлять query exhaustion coherent recovery. Затем полный relevant execution
ledger, buffered replay, позиции/балансы и durable projection/recovery gate;
B5 physical sender fencing остаётся. B4/READY и #73 не закрывать преждевременно.

Указание B4-03c как «следующего шага» ниже заменено этим последним дополнением.

## Последнее дополнение 2026-11-05 — B4-03b проверен с coverage limitation

Код `39161395cad009c57bbb192a0298ea1c3b7951d0`:
[отчёт](workstreams/B4_03B_RECOVERY_QUERIES_2026-11-05.md),
[контракт](../architecture/DERIBIT_RECOVERY_QUERIES.md). **137/137 PASS**,
environment/cleanup PASS. Opt-in `--collect-recovery` и отдельный Windows wrapper
`scripts/deribit/Deribit-RecoveryCollectionLocal.ps1`; scope ETH-PERPETUAL,
прежний encrypted pin/key, read-only REST session, без DB/trading и повторных ордеров.
Живое перечисление пяти поддерживаемых выборок завершилось, facts **0**, quiet case.
Постоянный reader после этого: **2/2/0**, journal commands/attempts **2/2** неизменен.

**Ограничение, подтверждённое API:** `historical=true` вместе с `include_unfilled=true`
запрещены (HTTP 400, -32602, параметр include_unfilled). Collector читает supported
historical subset с include_unfilled=false и всегда сохраняет
**HistoricalUnfilledOrdersCovered=false, OrderHistoryComplete=false,
CoherentRecovery=false**. Полноту account/recovery/READY не заявлять.

**Следующий increment B4-03c:** адресные read-only запросы фактов для известных
retained orders, отсутствующих в history subsets; не терять unfilled cancel case,
явно обрабатывать unsupported/missing. Затем coherent outage + buffered replay,
полный relevant execution ledger, позиции/балансы и durable projection/recovery gate.
Торговое разрешение не расширялось; завершённый B3 harness/marker не повторять.

Указание B4-03b как «следующего шага» ниже заменено этим последним дополнением.

## Актуальное дополнение 2026-11-05 — B4-03a проверен

Код `9465a608db2070cff52caba3be2c6ed4905178d7`: отдельные `--provision-reader`
и `--read-snapshot`, без вызова старого migrating opener/торгового runner.
[Отчёт B4-03a](workstreams/B4_03A_RETAINED_READER_2026-11-05.md): **99/99 PASS**,
environment/cleanup PASS. Новый reader реально прочитал постоянный store через
SELECT-only роль: retained open/cancel facts/observations/problems **2/2/0**.
Journal commands/attempts **2/2** не изменён, venue requests **0**. Native binding
взят из прежнего encrypted pin; секреты/IDs не публиковались. Provisioning выполнен,
повторять его для обычного чтения не нужно. Reader запускается отдельно через
`scripts/deribit/Deribit-RecoveryReaderLocal.ps1 -Action ReadSnapshot` после bounded build.

**Следующий increment B4-03b:** bounded read-only collection открытых ордеров,
истории и отдельных fills за явный outage interval с overlap, pagination и явной
неполнотой. Использовать существующие key и native pin, без новых торговых действий.
Позиции/балансы, coherent replay, durable projection/recovery gate и fencing остаются.
Историческое чтение store не означает свежую сверку с биржей или B4/READY.

Ниже сохранена исходная точка передачи после B4-02 для истории; её утверждения
«B4-03 ещё не реализован» и первые три пункта ближайшего задания заменены этим
дополнением. Текущий Git и roadmap проверять перед продолжением.

Точка передачи от 2026-11-05. Источник текущих решений — [AGENTS.md](../../AGENTS.md),
[целевая архитектура](../architecture/TARGET_PLATFORM_ARCHITECTURE.md) и актуальные
примечания в начале [master roadmap](INSTITUTIONAL_PLATFORM_MASTER_ROADMAP_2026-10-25.md).
Проверить текущий Git: эта точка передачи может быть старше последующих изменений.

## Состояние на момент передачи

- Последний опубликованный код: main `66d23db012630558ef6f2708ed9b1b136c6ee2a5`.
  Это завершённый B4-02 с записью общего разрешения на дальнейшие публикации.
  Коммит самой точки передачи может быть новее. B4-03 ещё не реализован.
- B3: настоящий ETH-PERPETUAL ордер **1 USD** один раз выставлен и отменён.
  Исполнений не было, исходная позиция сохранилась. Постоянный журнал:
  commands/attempts=2/2, inbox/observations/incidents=2/2/0.
  [Отчёт](workstreams/B3_03_MAINNET_ETH_2026-11-04.md).
- B4-01: deterministic order/execution recovery reducer, scope/epoch/window/coverage,
  dedup и blocking discrepancies. Полный прогон 247/247; после добавления context
  результата повторены все 47 affected recovery cases.
- B4-02: application read port, PostgreSQL read-only repeatable-read snapshot,
  canonical SHA/schema/scope/metadata checks, inbox/observation linkage,
  quarantine/incidents блокируют projection. **87/87 PASS**, включая настоящий PG.
  [Отчёт](workstreams/B4_02_DURABLE_READER_2026-11-05.md),
  [контракт](../architecture/OMS_PRIVATE_RECOVERY_READER.md).
- Live binding нового reader к постоянному store, venue recovery collector,
  позиции/балансы, durable projections/gates и physical sender fencing не выполнены.
  B4/READY не заявлены, issue #73 открыт. Обычные execution switches остаются false.

## Ближайшая реализация: B4-03

1. Сделать отдельный opt-in read-only путь для нового reader к **существующему**
   постоянному mainnet qualification store. Проверять store ownership и account
   scope; не подменять production binding disposable clone и не выводить account
   identity из fingerprint/name. Секреты и native IDs не печатать.
2. Использовать SELECT-only runtime роль. Привязка чтения не должна молча запускать
   migration, торговый runner, повтор первого ордера или общую конфигурацию БД.
   Если нужна отдельная provisioning-операция — ограничить её проверенным owned
   store, отделить от чтения, квалифицировать на clones. Не менять соседние сервисы.
3. Реальным новым reader прочитать сохранённые open/cancel факты, проверить
   canonical integrity, полный scope и отсутствие blocking incidents. Сохранить
   только обезличенный результат. Не выдавать один старый DB snapshot за свежую
   сверку с биржей или completed B4.
4. Затем реализовать bounded read-only collection открытых ордеров, истории и
   отдельных fills за outage interval с overlap, pagination и явной неполнотой.
   Использовать существующий сохранённый ключ и encrypted native pin, без повторного
   запроса ключей, нового аккаунта или ожидания testnet. Позиции/балансы и coherent
   replay → durable projection/recovery gate являются следующими частями B4.

Не повторять прежний торговый harness. Разрешённый одноордерный case уже завершён,
attempt marker сохраняется. Новая публикация кода не означает новое торговое разрешение.

## Рабочие ограничения и разрешения

- Владелец 2026-11-05 явно разрешил: «Да, B4-02 и следующие проверенные этапы».
  Обычный push и безопасный fast-forward main следующих небольших проверенных этапов
  кода разрешены без нового запроса на каждый этап. Без force/rebase, Actions и
  публикации private data. Не создавать Issue/PR для существующей #73.
- Работать самостоятельно, без агентов/worker-чатов. Ветки `TradeOps/<этап>-<задача>`.
- V2 — Deribit, включая обязательный FIX 4.4/QuickFIX/n; другие venues — V3.
  Connector не зависит от Infrastructure/EF. Private facts v2 / observations v1
  и существующие migrations не менять без versioned compatibility решения.
- Не трогать пользовательский untracked `src/TradeOps.Api/Properties/launchSettings.json`.
  Не останавливать/reconfigure WSL/Docker, Comments/Command или соседние постоянные БД.
- Память через локальный recall в этой сессии недоступна (`OperationalError`).
  Продолжать по файлам/Git/evidence, не ремонтировать её и не утверждать, что она
  была прочитана. Никаких фоновых LLM/API memory processing.

## Проверки и локальные материалы

Сборка: существующий `D:\DotNet\dotnet.exe`, Release, `--no-restore -m:1 -nr:false
-p:BuildInParallel=false -p:UseSharedCompilation=false`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`.
Текущий целевой suite: `scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-reader`.
Не повторять проверки без изменения кода или новой причины. WSL тяжёлые операции
только с Resource Guard до/после и snapshots/cleanup собственных test resources.

Локальная, не публикуемая памятка: `.tradeops/CONTINUE_PRIVATE_2026-11-05.md`.
В ней только пути и технические указатели; ключи и native account IDs туда не писать.
Точные результаты/TRX/source hashes находятся в `.tradeops/`; последний проверенный
reader suite — `.tradeops/postgres-reuse/20261105T081630-b4-reader-10972/`, 87 PASS.

Завершить каждую следующую итерацию обновлением актуальных notes roadmap и отдельным
workstream report. Обновлять эту точку передачи, если по ней ещё продолжают работу.
