# Ограниченный OMS-сценарий с запуском владельцем

2026-11-05. Реализация согласованных [границ](OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md).
Это отдельный qualification-only путь. Он не включает production execution switches,
не заменяет B5 risk/sender fencing и не даёт READY.

## Граница текущей поставки

Подготовлены explicit CLI mode `--owner-bounded-oms`, локальный Windows launcher,
WSL resource/store guard, native response recorder, actual private capture и
order-recovery assessment. Coding agent не запускает торговый режим. `Status`
не читает ключи, не обращается к бирже/БД и не запускает WSL.

Постоянный mainnet store **не обновлён и не активирован этим этапом**. Новый путь
отказывается до venue authentication, если отсутствуют отдельно подготовленные
schema/roles. Он никогда не вызывает старый migrating/admin opener, не выдаёт
grants и не создаёт сервер. Для активации нужна отдельная квалификация binding и
подготовка двух новых runtime roles в уже принадлежащем квалификации журнале.
Наличие кода launcher не означает live PASS или готовность запустить его сейчас.

## Границы и admission

- Только существующий encrypted native account pin и соответствующий сохранённый
  credential fingerprint. Полный scope строится из подтверждённых native id/type;
  tenant/environment/venue не выбираются из возвращённых order labels.
- Инструмент ETH-PERPETUAL, buy limit/GTC, ровно 1 USD native amount, post-only и
  reject-post-only. Это ограниченная проверка, не общий order-entry API.
- Цена округляется вниз по подтверждённому tick от 90% best bid. Quote age —
  максимум 5 секунд, допустимое опережение часов — 2 секунды. Перед placement
  metadata/book проверяются ещё раз; старая цена должна оставаться допустимой и
  не ближе 10%. Ближе к рынку она не сдвигается.
- Поддерживаются только active inverse ETH future, native USD, совместимые
  min amount/contract increments и простой tick schedule. Неподдерживаемые metadata
  завершаются без заявки, без увеличения количества или смены инструмента.
- `min_price` — floor для sell, `max_price` — ceiling для buy; код не трактует их
  как симметричный диапазон и не подтягивает distant buy к sell floor. Основание:
  [official get_order_book](https://docs.deribit.com/api-reference/market-data/public-get_order_book).
- Перед запуском проверяются native account, малая equity/margin граница старого
  квалификационного счёта, отсутствие ETH open orders и bounded native position
  baseline. Существующие позиции и чужие заявки не изменяются.

Одна invocation делает максимум один placement и одну адресную cancellation.
Три invocation — верхняя граница этапа, а не автоматический цикл из трёх заявок.
Stage ID фиксирован в проверяемом коде: `B4-bounded-oms-v1`. Caller не может сменить
его для сброса лимита. Immutable Place commands с versioned provenance считают
использованный бюджет в постоянной БД, включая unsent/uncertain reservations.
Удаление локальной директории и перезапуск процесса не создают новый бюджет.

Exclusive local file lock удерживается весь сценарий. Отдельный account-scoped
PostgreSQL session lock арбитрирует admission; та же живая session проверяется перед
отправками. Сессия не переподключается. Это локальная защита квалификации, **не**
general physical fencing между разными хостами/WS/FIX. Production single sender
остаётся отдельным B5 результатом.

Непригодный ledger snapshot или unknown sends блокируют admission. Предыдущие
qualification Place должны либо остаться доказанно unsent, либо иметь сохранённые
Place/Cancel receipts, zero-fill terminal private evidence и непротиворечивые terms.
Fill, missing receipt, orphan cancel, reopening или quantity/price mismatch
блокируют следующие запуски. Старые legacy ACK не получают synthetic receipts и
не становятся account-ready от допуска нового ограниченного qualification run.

## Жизненный цикл

1. Проверить input/source/store; взять local/account admission и capture ownership.
2. Public metadata/quote preflight; затем bounded REST auth и native identity checks.
3. Запустить реальный private-stream capture, дождаться подтверждённой подписки.
4. Сохранить новый Place command и durable DispatchStarted до единственной отправки.
5. Сохранить прямой correlated response как native receipt v1; записать ACK.
6. Дождаться committed WS open, проверить адресное native state и отсутствие fill.
7. Сохранить Cancel command/attempt и отправить один `private/cancel` по native ID
   из direct placement receipt. Сохранить cancel receipt/ACK и committed WS terminal.
8. Адресно проверить terminal state, recent/historical own-order trades, identity,
   отсутствие ETH open orders и сохранение native position baseline.
9. Выполнить existing ledger/order recovery assessment при продолжающемся capture,
   сверить именно две текущие команды, затем снова проверить state/private evidence
   и позиции. Остановить свой capture и освободить свои leases.

Addressed trades API возвращает массив; codec не выдумывает `has_more` или `count`
для этого метода. Ответ ограничен 256 KiB и максимум 1000 нормализованных строк;
overflow/неполные поля не превращаются в нулевой ledger. См.
[official get_user_trades_by_order](https://docs.deribit.com/api-reference/trading/private-get_user_trades_by_order).
Прямые order-entry ответы не маскируются под WS observations. Реальные addressed
read replies сохраняются как RecoveryQuery observations исходного epoch.

## Остановки и значения результата

Собственный execution или positive cumulative из private stream немедленно
поднимает stop signal ещё до capture commit. Label используется только для
консервативной остановки, никогда как разрешение native identity binding.
Native response/read/retained snapshot дополнительно проверяются на fill.

При fill — STOPPED_FILL без последующих отправок/flatten. Уже отправленный cancel
может сам вернуть partial fill; это также остановка. Нельзя гарантировать отсутствие
fill за счёт post-only/расстояния или исключить сетевую гонку с уже начатой отправкой.
При неизвестном ответе нет resend, cancel-by-label, cancel-all, finally-cancel,
восстановления счётчика или повторного запуска. OwnOrderMayRemain относится к
текущей попытке; persistent commands/receipts/capture остаются для разбора.

`OMS_SCENARIO_PASS` означает проверенный ограниченный цикл и два current command
bindings. Global history/ledger/coherent-cut/replay blockers сохраняются. Legacy
command issues выводятся общим count, а не скрываются. AssessmentCompleted не
равен recovery success; AccountReady/TradingReady/DurableProjectionPublished=false.
`SKIPPED` не является execution qualification. PersistentStore=false в scripted
тесте прямо отделяет test clone от live journal.

## Launcher и ещё не выполненная активация

`scripts/deribit/Deribit-BoundedOmsLocal.ps1` по умолчанию выполняет только Status.
Торговый режим требует explicit `Run`, `OwnerLaunch` и точный committed source SHA;
он предназначен для запуска самим владельцем. Оба execution switches остаются false.
Старый одноразовый marker и runner не используются. Пароли/ключи идут только через
stdin/env одного owned child, runtime result не содержит native IDs, цен, позиций,
балансов, credentials или raw errors. Проверяются source/binary hashes и отдельный
anonymous diagnostics allowlist. Raw stderr не сохраняется/не показывается.

Store binding требует существующие роли:

| Роль | Назначение |
|---|---|
| tradeops_q_bounded_journal_v1 | intents/commands/attempts/outbox/aggregate + append receipts/incidents |
| tradeops_q_capture_v1 | существующая capture-only роль; receipt write запрещён |
| tradeops_q_bounded_reader_v1 | SELECT journal/private/receipt tables, без writes/sequence authority |

Новые роли требуют marker `tradeops-mainnet-qualification:bounded-oms:v1`; capture
сохраняет старый runtime marker. Все роли — NOSUPERUSER/без membership/CREATE/TEMP/
delete/truncate; reader additionally проверяется на отсутствие write authority.
Нужен receipt schema v1 из предыдущего этапа. Подготовка roles/schema не входит в
Run и не выполнялась на постоянной БД; admin fallback запрещён. WSL guard проверяет
existing container/network/volume/cluster ownership, пишет anonymous before/after
counters, контролирует peers/resources и не запускает/пересоздаёт контейнеры.

Следующий результат после scripted qualification — отдельно подготовленная и
проверенная persistent store/role binding. Реальный owner run, legacy resolution,
common history/capture basis и persisted recovery state остаются в
[плане B4 → QuickFIX/n](../orchestration/B4_TO_QUICKFIX_PLAN_2026-11-05.md).
