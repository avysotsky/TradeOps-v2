# Deribit ETH FIX — live qualification readiness

Дата: 2026-11-07, Europe/Kiev. Расширение B6 `c1ac85b`,
ветка `TradeOps/B6-quickfix`. Статус: **LIVE NOT RUN**.
Реальных торговых команд в этой проверке не отправлено. Документ фиксирует
процедуру и границы. Код owner runner добавлен; [инженерная приёмка](../orchestration/workstreams/FIX_PRICE_AMEND_QUALIFICATION_2026-11-07.md)
**2418/2418 full + 107/107 Windows PASS**, guard/peers/cleanup PASS.
Документ не является live evidence.

## Запрошенная цель

Владелец запросил live-проверку размещения, снятия/отмены и переноса ордеров
ETH-фьючерсов по FIX, затем отдельную квалификацию WebSocket.
Для текущего сохранённого счёта действуют [согласованные mainnet-границы](OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md):
ETH-PERPETUAL, максимум 1 USD native notional, одна активная заявка, до трёх
placement attempts на этап, post-only, не менее 10% от рынка с соблюдением правил
биржи. Существующие marker/budget не сбрасываются. При fill — остановка и отчёт,
без автоматического flatten. Другие ETH expiry/linear инструменты не включаются
автоматически в этот scope.

Подготовка кода и анализ evidence выполняются ассистентом; реальные торговые
команды должен запускать владелец. Секреты и private account data не включаются
в репозиторий, команды в чате или публичный отчёт.

## Проверенная готовность

| Область | Факт на текущем коде | Статус live |
| --- | --- | --- |
| FIX session/TLS/protocol store | Реализованы, offline/loopback квалифицированы | NOT RUN |
| B4/B5 risk/physical owner/native journal | Интегрированы; новое расширение принято полным offline suite 2418/2418 | NOT RUN |
| Placement `35=D` | Поддержан узкий inverse-future профиль с verified 1 USD/contract | NOT RUN |
| Адресная отмена `35=F` | Поддержана только для доказанной собственной заявки | NOT RUN |
| Перенос цены `35=G` | [Versioned price-only mutation через B4/B5](../architecture/DERIBIT_FIX_PRICE_AMEND_V2.md); размер/флаги неизменны | NOT RUN |
| Owner-launched FIX qualification runner | `Deribit-FixQualificationLocal.ps1`, отдельный host mode; по умолчанию только Status | NOT RUN |
| Старый bounded OMS runner | Entry point намеренно возвращает отказ после B5; это исторический WS-сценарий | НЕ ИСПОЛЬЗОВАТЬ ДЛЯ FIX |
| Permanent store/session/четыре роли/policy | B6 их автоматически не мигрировал и не provision-ил | NOT VERIFIED |

Deribit поддерживает native изменение работающего ордера через
[`Order Cancel/Replace Request (35=G)`](https://docs.deribit.com/fix-api/production/order-cancel-replace).
Новая реализация поддерживает только цену ETH-PERPETUAL без fills; изменение
количества остаётся неподдержанным. Native OrigClOrdID, QtyType и ExecInst сверены.
Cancel → новый placement не засчитывается как проверка native Replace.

## Матрица будущего owner-launched прогона

| Проверка | Обязательное evidence для PASS |
| --- | --- |
| Preflight | Точный encrypted native pin; fixed-origin identity verification; current public metadata/contract_size; quote freshness; price/amount increments и venue price band; отсутствие любой активной заявки и незавершённой своей попытки; доступный durable stage budget |
| Logon/session | TLS/certificate validation; accepted native logon; provisioned session key и persistent cursors; отсутствие queued gap/reset/fault; actual current B4 qualification |
| Placement | B5 reservation/command/outbox/binding до I/O; точные одобренные terms на wire; retained ExecutionReport; согласованные native IDs и private/history observation; отсутствие fills |
| Visibility | Собственная заявка видна через независимый native read; price/quantity/flags совпадают; чужие заявки не изменены |
| Перенос цены | Отдельная durable mutation/admission, исходная native identity, новые одобренные terms, новый change ID/time и согласованная recovery-модель |
| Адресная отмена | Только собственная проверочная native identity; retained cancel response; terminal observation и полный deduplicated fill/fee ledger; отсутствие оставшейся своей заявки |
| Итог | Все попытки и их неизвестные исходы учтены; исходные позиции/чужие заявки сохранены; persistent journal и protocol cursors сохранены; отдельный PASS/FAIL/UNKNOWN по каждой операции |

Перед каждым изменением цены заново проверяются freshness, post-only, distance,
venue band и риск. Если все исходные ограничения одновременно невыполнимы,
сценарий останавливается без PASS; размер, расстояние и счёт не изменяются автоматически.
Неизвестный исход не повторяется и не становится PASS после перезапуска.
Любой fill закрывает сценарий без новых торговых команд ассистента.

Преднамеренные fills, частичные исполнения, transport/DB corruption и kill races
не создаются на mainnet для покрытия тестов. Их offline evidence маркируется
отдельно; отсутствие такого события в live run не означает live PASS этого кейса.

## Owner launch и оставшийся live gate

Runner выполняет одну последовательность Buy Limit GTC 1 USD post-only → native
price G ниже прежней цены минимум на tick → own F → current cancelled recovery.
Он не создаёт fills намеренно. Постоянный budget берётся из immutable OMS commands
с provenance `owner-fix-eth-qualification-v1`, максимум три placements; unknown и
unsent тоже блокируют повтор до разбора. Дополнительная DB session lease ограничивает
параллельный сценарий, а физическим владельцем остаётся обязательный B5 OS fence.

До запуска владелец подготавливает постоянную DB с B6 migrations и
`20261107010000_AddFixPriceAmendV2`, четыре ограниченные identities, current scoped
policy, immutable FIX session row/key SHA и physical node objects. Protocol role
дополнительно требует SELECT/INSERT на `oms_fix_amend_messages_v2`, без mutation/
delete/truncate. Для одной изменяемой заявки policy должна допускать два pending
reservations и их консервативную сумму. Runtime не повышает лимиты, не мигрирует
DB и не seed-ит роли/session/root. Постоянное provisioning здесь не выполнялось.

В локальном окружении владельца должны быть `ConnectionStrings__TradeOpsCaptureDb`,
`ConnectionStrings__TradeOpsRecoveryDb`, `ConnectionStrings__TradeOpsExecutionDb`,
`ConnectionStrings__TradeOpsFixDb`, `DeribitOms__LockRoot`, `DeribitFix__SenderCompId`,
`DeribitCapture__Tenant`, `DeribitCapture__Account`, `DeribitRecovery__InitialFromUtc`.
Outage anchor — фактический сохранённый момент; не сдвигать его ради успешного gate.
Секреты и private account data не помещать в Git/чат/публичные логи.

PowerShell 7.5, проверенный чистый committed checkout. Единственное допустимое
untracked исключение — пользовательский `launchSettings.json`.

```powershell
# Без credentials, network или trading.
& D:\Projects\TradeOps-v2\scripts\deribit\Deribit-FixQualificationLocal.ps1 -Action Status

# Реальные финансовые заявки. Запускает только владелец.
# Значение должно совпадать с квалифицированным commit из итогового отчёта.
$qualifiedCommit = (git -C D:\Projects\TradeOps-v2 rev-parse HEAD).Trim()
& D:\Projects\TradeOps-v2\scripts\deribit\Deribit-FixQualificationLocal.ps1 `
    -Action Run -OwnerLaunch -ExpectedHead $qualifiedCommit
```

Launcher использует существующий SDK и DPAPI vault/native pin, сверяет fingerprint
credentials и native main account, source/DLL hashes, передаёт ключ только дочернему
процессу. Host независимо проверяет fixed-origin pin и public metadata. Обычные
execution switches остаются false; обычный FIX mode квалификацию не запускает.
Сценарий ограничен 150s, transport 240s. Timeout launcher не убивает процесс и не
разрешает повтор: проверить процесс и постоянный журнал.

При unknown/reject/fill/timeout/потере lease/evidence сценарий останавливается.
После успешного placement заявка может остаться, если следующий шаг остановлен;
смотреть `OwnOrderMayRemain` и разбирать native outcome отдельно. Автоматической
отмены по старой revision и flatten нет. `UnexpectedFill=null` означает неизвестность.

`FIX_QUALIFICATION_SCENARIO_PASS` требует Place/G/F ACK, current terminal cancelled,
сохранённых native position sizes/units/direction, `UnexpectedFill=false`,
`OwnOrderMayRemain=false` и exit0. Переменный base equivalent inverse позиции
не сравнивается как native size ([native поля позиции](https://docs.deribit.com/api-reference/account-management/private-get_positions)).
Sanitized JSON сохраняется под ignored `.tradeops/fix-qualification`; он не заменяет
постоянные commands, bindings/reports, private/history и recovery publications.
После owner run ассистент анализирует эти факты по матрице. WebSocket квалифицируется
отдельно: FIX evidence не является WS PASS.

До подтверждённого owner live run утверждение «весь FIX-функционал ETH проверен live»
не подтверждено. Offline-итог B6 сохраняется в своей исходной ограниченной границе.
