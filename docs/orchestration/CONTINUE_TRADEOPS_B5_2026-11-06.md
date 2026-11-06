# Продолжение TradeOps-v2 после B4 — задание B5

Дата передачи: 2026-11-06, Europe/Kiev. Репозиторий: `D:\Projects\TradeOps-v2`.
Это инструкция для нового чата. Сейчас требуется начать разработку B5, а не
повторять B4 или ограничиваться очередным планом.

## 1. Сначала прочитать

1. Корневой `AGENTS.md` и применимые вложенные инструкции.
2. [Целевая архитектура](../architecture/TARGET_PLATFORM_ARCHITECTURE.md).
3. Начало [master roadmap](INSTITUTIONAL_PLATFORM_MASTER_ROADMAP_2026-10-25.md),
   затем требования B5/B6.
4. [Итог B4](workstreams/B4_RECOVERY_COMPLETION_2026-11-06.md) и
   [recovery contracts/runtime](../architecture/OMS_RECOVERY_RUNS_V1.md).
5. [План до QuickFIX/n](B4_TO_QUICKFIX_PLAN_2026-11-05.md),
   [PostgreSQL tests](../testing/OMS_POSTGRES_TESTS.md),
   [mainnet policy](../testing/OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md).

Проверить Git и текущие файлы. Код B4 опубликован в `main` коммитом
**`24bcc0cd949a94f28d95352bb208eb376c9ddd3e`**. Текущий HEAD может быть его
документальным потомком, включая этот handoff; не откатывать его ради совпадения SHA.
Ветка реализации B4: `TradeOps/B4-recovery-completion`.

Локальная память проверялась, поиск завершился `LOCAL MEMORY UNAVAILABLE
(OperationalError)`. Не утверждать, что история восстановлена из памяти, и не
ремонтировать её в рамках TradeOps. Продолжать по репозиторию/evidence. Никаких
фоновых LLM/API-процессов для памяти.

## 2. Что уже выполнено

B4 реализован и квалифицирован **offline**:

- Общий account-scoped RR-снимок commands/attempts/outbox/receipts/private facts,
  решений оператора и retained recovery checkpoint.
- Global private capture с реальным native pin/subscription ACK, epoch/replay и
  invalidation до commit; события cash/positions учитываются отдельно от fills.
- Полный current inventory и relevant currency histories, native clock, явные
  units, выявление неизвестных продуктов, дублей и конфликтов.
- Атомарная публикация orders/fills/positions/balances, recovery head и audit event;
  разрешение доказанных started/ambiguous commands без resend.
- Live runtime gate, защищённый от restart, stale epoch/generation, нового ingress
  и изменения журнала; сохранённый JSON не создаёт полномочий.
- Scoped operator acceptance/withdrawal конкретного внешнего ордера по точному
  evidence digest, с настоящей ролью БД. Это не receipt и не снятие hard blockers.
- Не теряются fills, найденные через history: исходные query facts сохраняются
  в immutable checkpoint, читаются следующим RR-снимком и дедуплицируются с WS.
- Отдельный `Worker:Mode=DeribitAccountRecovery`, без legacy workers/sender,
  с раздельными capture/recovery DB identities и проверкой существующих прав.

Это поддержанный ограниченный режим, а не универсальное разрешение торговли.
Политика `deribit-quiescent-recent-v1` требует тихого replay и окна с overlap/I/O
не более 25 минут; пригодность текущей модели ограничена пятью секундами.
Inverse USD-native futures и base-native options нормализуются; неподдержанные
linear position units, expired/unknown metadata, capacity и gaps оставляют BLOCKED.

Постоянная mainnet БД/роли не мигрировались. Реальных заявок при завершении B4 не
выставлялось, consumed marker старого one-off не сбрасывался. Старые ACK без direct
receipt остаются нерешёнными. B4 READY не является B5 risk approval или sender fencing.

## 3. Проверки уже выполнены — не запускать заново для знакомства

Составная приёмка: **2110 test-case rows, 0 оставшихся сбоев, 0 skipped**, output
validators **30/30 + 30/30**. Один полный прогон и только адресные исправления.
Первый полный TRX не был полностью зелёным: 2084 PASS / 21 FAIL из 2105 actual
cases; discovery показывал 2090, data theories расширились во время исполнения.
Ошибки закрыты адресно; затем добавлены пять regression cases. Итог не выдавать
за один зелёный полный прогон.

Локальные файлы:

- `.tradeops/b4-final-qualification.json` — сводный аудит.
- `.tradeops/verify_b4_acceptance.py` — независимая сверка TRX/source hashes.
- `.tradeops/postgres-reuse/20261106T072944-b4-final-18046/` — полный прогон.
- `20261106T075847-targeted-15099/` — 47/47 PASS.
- `20261106T083626-targeted-12076/` — 66 PASS / 2 FAIL; ошибочное ожидание 74
  сохранено в evidence, не переименовано в PASS.
- `20261106T084423-targeted-7680/` — промежуточные 1 PASS / 1 FAIL clock fixture.
- `20261106T085033-targeted-15908/` — финальные 2/2 PASS restart/archive/dedup.

Последние четыре каталога также находятся под `.tradeops/postgres-reuse/`.
Проверены реальные exit codes/TRX, guard, peer containers и cleanup. Совпали 412
source hashes. Не коммитить `.tradeops/`, raw private data или секреты.

## 4. Первое задание нового чата — начать B5

Работать самостоятельно в ветке `TradeOps/B5-<задача>`, без субагентов, worker-чатов
и очередей. После короткого inventory сразу перейти к реализации. Единица
дальнейшей работы — законченный B5; внутренние части не выдавать за отдельные
закрытые этапы и не останавливаться после каждого небольшого изменения.

Первый конкретный результат: обязательный account-scoped risk admission для нового
OMS, использующий актуальную B4-модель и атомарное durable pending-risk reservation.
Нельзя обойти отказ через UI, стратегию или прямой адаптер. Сначала изучить имеющееся,
выбрать интеграцию и переиспользование, затем реализовать реальное поведение.

Начальные точки inventory:

- `src/TradeOps.Application/Services/RiskEngine.cs`, `RiskControlService.cs`,
  `SignalExecutionService.cs`, их interfaces/models.
- `src/TradeOps.Application/Models/RiskSettings.cs` и
  `src/TradeOps.Infrastructure/Risk/InMemoryRiskState.cs`.
- `src/TradeOps.Application/Oms/OmsJournalContracts.cs`,
  `src/TradeOps.Infrastructure/Persistence/Oms/OmsPostgresJournal.cs`.
- `src/TradeOps.Application/Oms/OmsRecoveredAccountContracts.cs`,
  `OmsRecoveryRetainedFacts.cs`, `DeribitRecoveryCoherencePolicy.cs`.
- `src/TradeOps.Worker/DeribitAccountRecoveryWorker.cs`:
  `ReadCurrentAsync()` и `RequestRecovery()`.
- Существующие reservations, operational risk state, gateways/terminal paths и
  соответствующие tests — сверить фактические реализации, не названия файлов.

Не объявлять legacy RiskEngine/InMemoryRiskState готовым институциональным B5.
Существующие generic `RiskSettings` по умолчанию и quantity limits нельзя молча
переносить на Deribit native USD/base units. Лимит 1 USD тестового сценария также
не является production risk policy. Нет требуемой account policy, цены/маржи,
валидных units или свежего состояния — отказ, без выдуманных допусков.

### Результаты, необходимые для закрытия B5

1. Instrument/account eligibility; per-order notional, position delta, portfolio
   exposure, leverage/margin, reduce-only, concentration, loss и pending-risk limits.
2. Атомарное account-scoped резервирование pending exposure с idempotency,
   конкурентными admissions, restart и неизвестными исходами. Нельзя освобождать
   резерв по одному cancel ACK или повторно учитывать один fill.
3. Account/venue/global circuit breakers, durable kill switch, RBAC и immutable
   audit. Cancel/flatten policy должна быть явной; не добавлять реальные финансовые
   действия как побочный эффект разработки или проверки.
4. Обязательная граница перед физической отправкой. Утративший владение процесс
   не может отправить; WS и будущий FIX подчиняются одному execution owner.
   Metadata/advisory lock и проверка перед RPC сами по себе не доказывают fencing.
5. Meaningful tests: параллельный oversubscription, stale/gap baseline, partial
   fills, unknown send/cancel, kill-switch race, restart и stale owner.

Важная интеграционная ловушка: B4 evidence version меняется при записи команды/
attempt/outbox. Нельзя просто ослабить B4 gate или проверять старую версию после
собственного admission и постоянно отказывать. Нужен проверяемый атомарный переход
baseline → reservation/admission с явным version/ownership binding. Решение о риске
и отправке нельзя восстановить из JSON/boolean или принять от клиента.

При изменении durable contracts — явное версионирование и совместимость. Сохранять
модульные границы; Deribit connector не зависит от PostgreSQL/EF/Infrastructure.

## 5. Режим работы и публикация

- Внутри B5 проверять только изменённое поведение и непосредственно затронутые
  компоненты. Полный набор — перед закрытием B5; выявленные сбои исправлять адресно.
  Не запускать сотни/тысячи тестов после маленькой правки или просто после handoff.
- SDK: `D:\DotNet\dotnet.exe`, последовательно, `-m:1 -nr:false
  -p:BuildInParallel=false -p:UseSharedCompilation=false`,
  `DOTNET_CLI_USE_MSBUILD_SERVER=0`. Не устанавливать SDK и не менять global.json.
- PostgreSQL — marked reusable test server и owned disposable clones. До/после
  тяжёлой Ubuntu-работы resource guard; при отказе не начинать. Не трогать постоянные
  БД, Docker daemon, WSL/global settings, Comments/Command Service и чужие процессы.
- Обычная публикация проверенного кода и безопасная интеграция в main уже разрешены;
  не запрашивать её заново для каждого изменения. Без force push/rebase.
  GitHub Actions не включать/не запускать; commits с `[skip ci]`.
  Не создавать новый Issue/PR для существующей задачи. Issue #73 — исторический B3,
  не обозначать его номером нового B5; QuickFIX/n имеет существующий #40.
- Не менять/коммитить `src/TradeOps.Api/Properties/launchSettings.json`: это
  пользовательский untracked файл. Сохранённый SHA-256:
  `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.
- Общаться по-русски, сообщать конкретный результат/остаток, не заменять разработку
  отчётами о микроитерациях. Не спрашивать повторно уже согласованное.

## 6. Mainnet и маршрут к FIX

Следовать отдельной [mainnet policy](../testing/OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md).
Сохранённые границы: pinned основной счёт, ETH-PERPETUAL, максимум 1 USD native
notional, одна активная заявка, до трёх placements за этап, post-only, минимум 10%
от рынка с учётом правил биржи; адресная отмена собственной заявки; при fill —
остановка/сообщение без flatten. Не расширять лимиты, не делать cancel-all и не
сбрасывать старый marker. Ключ/разрешение повторно не запрашивать. Текущая граница
ассистента: подготовка и анализ сценария; реальные placement/cancel запускает владелец.
Оба ordinary execution switches остаются false. Согласованные лимиты не являются
live PASS, разрешением на новую схему/роли постоянной БД или production readiness.

После B5 — B6: обязательный Deribit FIX 4.4 через **QuickFIX/n**, dictionary/TLS,
session/message store, sequence/resend/gap recovery, normalized execution reports
и та же B4/B5/ownership цепочка. Каркас FIX можно готовить по roadmap, но доступ к
торговле требует завершённой интеграции риска и физического sender ownership.
Другие площадки — V3; архитектура всей платформы рассчитана на 10–20 коннекторов.
