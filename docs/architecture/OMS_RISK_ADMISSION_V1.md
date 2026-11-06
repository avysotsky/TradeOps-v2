# B5 — risk admission, controls и единственный sender

2026-11-06. Поддержанный режим B5 реализован и квалифицирован offline:
2245/2245 полного прогона, затем 29/29 адресной финальной правки; всего 2246
актуальных cases, без оставшихся сбоев/пропусков. [Результаты и evidence](../orchestration/workstreams/B5_RISK_EXECUTION_COMPLETION_2026-11-06.md).
Это не live PASS, не production activation и не универсальная multi-host модель.

## Поддержанный режим

Deribit V2: pinned main account, standard segregated BTC/ETH inverse futures,
native USD amount, limit/GTC placement и адресная отмена собственного OMS-ордера.
Options, linear contracts, subaccount execution, replace, trigger orders и FIX
order entry отклоняются. FIX 4.4/QuickFIX/n — следующий B6, с тем же физическим
владельцем и отдельными versioned native report contracts; WS receipt не выдается
за FIX ExecutionReport. Другие площадки — V3.

Обязательный путь: `IOmsOrderIntentGateway` → актуальная live B4 qualification →
policy/native valuation → атомарные command/outbox/reservation → durable attempt →
повторная проверка policy/controls/evidence → одноразовый physical-write permit →
owned WS socket → direct receipt/outcome → новая recovery. Caller-supplied risk
reference, JSON, READY boolean и старый receipt не дают разрешения на отправку.

Старый Deribit adapter больше не делает placement/cancel; реальные CLI-entrypoints
прежних testnet/mainnet/bounded OMS qualification отказывают до чтения stdin,
ключей и сети. Исторические scripted replay-сценарии остаются только для offline
совместимости, read-only mainnet-режимы сохранены. Старый terminal оставлен
read-only, status сообщает tradingEnabled=false, confirm/cancel не вызывают биржу.
UI/стратегии должны использовать новый gateway, не прямой коннектор. Legacy
`/api/risk` управляет прежним runtime, а не B5: операторские B5 controls проходят
через `OmsPostgresRiskControls` и реальную scoped роль БД. Frontend F3 и подключение
пользовательских каналов не объявляются выполненными этим этапом.

## Account policy и оценка риска

`oms_account_risk_policies_v1` — append-only, owner-provisioned, scoped revision,
strict JSON/schema/hash, validity window и разрешённые инструменты. Runtime не
может выдавать себе policy/permissions, менять node binding или делать migrations.
Нет policy — нет допуска. Generic RiskSettings и 1 USD тестового сценария не
используются как production policy.

`DeribitRiskObservationReader` получает native metadata/ticks, mark/index prices,
account summaries, hypothetical order margins и полный bounded transaction log
с 00:00 UTC. Проверяются account pin до/после, currency/units, timestamp, pagination,
коллизии IDs, capacity и scope. 5-second deadline, до 4 страниц по 1000 log rows на
валюту; неполнота отвергает оценку, без автоматического расширения лимитов.

`DeribitRiskInputsReader` рассчитывает USD notional без умножения USD-native amount
на USD price. Portfolio и concentration включают абсолютные позиции и remaining
open orders и абсолютную стоимость cash/underlying collateral; pending не
неттируется. Для inverse delta используется верхняя граница amount × max(1,index/mark)
со stress; дополнительная delta входит в portfolio/concentration bounds.
Reduce-only требует противоположной текущей
позиции и допустимого размера, а native reduce_only сохраняется при отправке.
FX stress снижает доступную equity и повышает margin/loss; equity ограничена ещё
и collateral каждой задействованной segregated currency. Equity берётся не выше
минимума native equity, margin_balance и used margin + available_funds. Требуются явные FX stress, margin
multiplier и limit-price deviation policy. Проверяются step/tick/band, eligibility,
per-order, position delta, portfolio, leverage, margin, concentration, loss и pending
USD/count. Неподдержанный продукт/режим/units закрывает всю оценку.

Нелинейная margin не оценивается суммой маржи отдельных маленьких заявок.
`OmsPostgresPendingRiskReader` читает committed незавершённые commands с точными
hash/units, исключая candidate. Native get_margins получает для каждого инструмента
полную pending + новую + remaining open quantity по минимальной известной limit
price. Берётся максимум buy/sell, положительная верхняя комиссия и margin multiplier.
В reservation сохраняется полный cumulative bound; повторный запас намеренно
допустим, understating очередной ступени маржи — нет. Повреждённые, неподдержанные
или превышающие capacity pending rows закрывают оценку целиком.

Loss v1 — консервативная метрика: сумма отрицательных cashflow и change плюс
абсолютные комиссии за UTC-день, затем отрицательные текущие session RPL/UPL,
без profit offsets. Возможен намеренный двойной запас по дебетам/settlement;
это не equity drawdown и не точный net PnL. Полное дневное прохождение обязательно.
Session RPL сам по себе не подменяет дневную историю.

Native observations кешируются максимум 5 секунд, по точным command/state/pending-vector hashes;
cache bounded 128. Approval не кешируется: policy читается и оценка проверяется
заново перед отправкой. Новый private ingress/gap/epoch немедленно инвалидирует
live B4 revision, даже пока запись inbox ещё не завершилась.

## Durable admission, pending и settlement

Account head → command rows → evidence counter сериализуют journal transition.
Транзакция включает CAS агрегата, intent/command/outbox, reservation, control stamp
и success audit. Следующая admission принимает только publication evidence version
или собственный `evidence_after` того же run/generation/state; сторонняя запись
требует новой B4 сверки. B4 gate не ослаблен ради собственных записей.

Reservation idempotent по command ID/canonical digest. Restart/unknown outcome не
обнуляют pending. Partial fill сохраняет полный консервативный резерв; позиции
берутся один раз из account model, executions повторно к ним не прибавляются.
`oms_risk_settlements_v1` исключает резерв только по текущей coherent B4 publication:
точный original placement receipt, owned native identity, terminal report и
полная сумма deduplicated fills. Отсутствие open order или cancel ACK недостаточно.
Terminal settlement уникален для команды, immutable и привязан к published state.
Неизвестные/неотправленные без доказанного terminal исхода резервы не освобождаются
по таймеру, рестарту или желанию нового sender.

Адресная cancel admission проверяет текущую account policy, parent reservation,
placement receipt, open native order и полное совпадение aggregate/intent/terms.
Она не создаёт новую exposure reservation и не уменьшает исходную. Foreign orders,
замена target/amount и отсутствие policy отклоняются. `Reserved` здесь означает
принятую durable admission с нулевым новым риском, не освобождение parent risk.
После cancel ACK по-прежнему нужны recovery и отдельные fills.

## Kill switch, circuit breakers и аудит

Независимые KillSwitch/CircuitBreaker latches: global, tenant/venue/environment,
account/subaccount. Global действует в одной authoritative risk БД, не федерация
между разными БД. Каждый writer использует actual database role membership,
exact scoped permission, CAS revision, idempotent decision ID и immutable audit.
Activate-only runtime не имеет права clear. Любой change, включая activate→clear,
инвалидирует старый control stamp. Старые reservations без stamp не получают
полномочий при migration.

Все admissions берут shared locks global→venue→account; control writers и policy
changes берут соответствующий exclusive lock. Durable loss/existing-margin breach
активирует account circuit после rollback отказанной admission. Capture disconnect,
gap или смена epoch после READY активируют latched account circuit в host.
Ошибка сохранения обязательного circuit/audit закрывает выполнение, не даёт approval.

Stop policy: block new increasing-risk admissions/dispatch starts, retain pending,
разрешить только явно запрошенную и доказанную addressed cancellation через тот же
owner/gate. Никаких автоматических cancel-all или flatten. Уже committed started
attempt считается in-flight/possibly-sent и может завершиться при гонке остановки;
kill ACK не означает отсутствие ордеров/fills. Native facts/recovery определяют исход.
Clear не снимает B4 hard blockers и не возрождает старые permits.

Каждый возвращаемый risk result аудируется. Tentative CAS откатывается перед
отдельным immutable denial; success audit коммитится с reservation. Gateway denial
без live baseline помечается zero run/hash sentinel и StaleBaseline, никогда approval.
Unknown send сохраняет started/ambiguous outcome и pending, без resend.

## Физическое владение

Поддержан один provisioned Windows/Linux узел на venue/environment. Immutable
`oms_execution_bindings_v2` связывает OS machine identity, канонический local lock
root и фактические OS identities directory + открытого lock-file object. Windows
использует volume/file ID на local fixed drive; Linux — device/inode на local
ext4/XFS/Btrfs/tmpfs. Network, overlay/9p и неизвестные filesystems отклоняются.
Same textual path в другом namespace, replacement root и unlink/recreate lock
не создают второго владельца. V1 metadata сохранена, но больше не даёт полномочий
и не обновляется автоматически. Other node/root, symlink/reparse root, expiring lease takeover и automatic
cross-host failover запрещены. Уникальность OS identity и непереносимость deployment
на клон — операционные предпосылки этого режима; cloned identities и multi-host
deployment не квалифицированы.

`OmsSingleHostPhysicalFence` держит OS-exclusive FileStream handle всю жизнь owner.
Он намеренно грубее account scope: один процесс на venue/environment, поэтому
разные credentials/tenant aliases одного native account не дают второго sender.
Process pause/потеря SQL connection не освобождают этот handle. Отзыв сначала
инвалидирует permits и abort-ит transport, затем дожидается операции/transport,
и только потом освобождает handle. Crash освобождает OS handle вместе с процессом.
Lock file не удаляется при передаче, чтобы новый процесс блокировал тот же inode.
Runtime открывает только существующий пустой pinned file. Явный operator helper
`OmsExecutionNodeProvisioning.Prepare(root, scope)` подготавливает локальный файл и
возвращает descriptor v2; он не пишет binding в БД, не выдаёт права и не запускает
торговлю. Provisioning DB policy/roles/binding остаётся отдельным действием владельца.

Каждый socket write потребляет одноразовый непублично создаваемый permit точной
команды. Последняя проверка live revision и вызов фактического socket Send находятся
под одним ingress barrier. После смены owner старый permit не может начать write.
SQL locks/metadata сами по себе не объявляются fencing. Shared kill/head locks
защищают пред-send переход; evidence counter остаётся locked через socket write,
так что посторонняя journal mutation не коммитится между финальной проверкой и
отправкой. OS handle защищает физическую передачу владения. Queued work
bounded 128; reconnect/resend и автоматический takeover отсутствуют.

## Host и совместимость

`Worker:Mode=DeribitOmsExecution` + explicit `DeribitOms:Enabled=true`, provisioned
LockRoot, отдельные capture/recovery/execution DB identities в одной БД, актуальная
account policy и node binding. Оба ordinary execution switches остаются false.
Default mode не регистрирует этот sender. Только host после restricted-role checks
открывает pinned WS session; fail-stop после uncertain send, без automatic restart.

Journal v1, private event v2, receipt v1 и recovery v1 encodings сохранены.
Новые append-only risk/control/policy/settlement/cancel tables versioned v1.
`oms_execution_bindings_v2` усиливает физическую привязку без изменения v1 данных.
`oms_execution_dispatches_v2` расширяет admission kinds на Place/Cancel, сохраняя
предыдущий `oms_risk_dispatches_v1` для совместимости; старые строки не дают resend.
Migration rollback отказывается уничтожать аудит. Authority нельзя восстановить
десериализацией. Коннектор зависит от Application/Domain, не от Npgsql/Infrastructure.

Постоянная mainnet БД/роли/ключи/позиции не менялись, реальных placement/cancel не было,
старый consumed marker не сбрасывался. Отдельная live activation/qualification
остаётся за владельцем по сохранённой mainnet policy.

Источники native semantics: [instrument rules](https://docs.deribit.com/api-reference/market-data/public-get_instrument),
[hypothetical margins](https://docs.deribit.com/api-reference/trading/private-get_margins),
[transaction log](https://docs.deribit.com/api-reference/account-management/private-get_transaction_log),
[transaction-log cashflow semantics](https://support.deribit.com/hc/en-us/articles/25944587269021-Transaction-log).
