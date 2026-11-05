# B4: команды, отправки и private events в одном снимке

2026-11-05. Код `d985a4ee21ce6c7456b5092a81d8690007e0f638`.
Новый путь восстановления перечисляет весь сохранённый журнал команд
счёта вместе с попытками отправки, outbox и private events. Это закрывает пробел
в исходном наборе данных: незавершённая отправка учитывается даже при пустом inbox.
B4 полностью не завершён; готовность счёта и публикация состояния не разрешены.

## Реализация

- Application-порт `IOmsRecoveryLedgerReader` и PostgreSQL-адаптер читают обе стороны
  в одной `REPEATABLE READ / READ ONLY` транзакции. ID и timestamp снимка общие.
  Повторного независимого чтения private events нет.
- Полный account scope, canonical command v1/hash, intent/provenance, непрерывная
  цепочка aggregate versions, outbox и единственная attempt v1 проверяются.
  Повреждение, отсутствующие связи и превышение лимита не дают usable partial result.
- `AssessWithLedgerAsync` связывает фактический снимок с существующим order gate.
  Непригодный журнал блокирует запросы к бирже. Команды других инструментов
  учитываются с явной проблемой охвата, а не отбрасываются фильтром.
- Для отправленных команд сохранённый ACK не является подтверждённым native outcome.
  Начатые/неоднозначные отправки, недоказанная identity placement, исход cancel/replace
  и несверенный reject получают отдельные проблемы; добавлен общий blocker команд.
  Неотправленные команды подсчитываются отдельно, без права отправки или повторения.
- Durable schemas/encodings/hashes не менялись; новые значения blockers добавлены
  в конец enum. [Правило и ограничения](../../architecture/DERIBIT_COMMAND_RECOVERY_LEDGER.md).

## Граница результата

Локальная целостность журнала не доказывает полный venue execution ledger. Даже
пустой журнал не исключает внешние ордера или пропущенные исполнения. Поэтому
`ExecutionLedgerCoverageUnproven`, history/replay/coherence/coverage blockers остаются.
Рабочее восстановленное состояние ещё не сохраняется.

Новый путь проверяется на owned disposable PostgreSQL clones и не активирован в
mainnet account host: существующая read-only роль не имеет новых SELECT grants.
Действующий host и diagnostics v2 сохраняют предыдущий маршрут. Mainnet-запросы,
торговые действия, изменение прав постоянной БД и запись в постоянный журнал
не выполнялись. Исторические семь blockers прошлого live run остаются его
результатом, а не результатом квалификации нового пути.

Следующий результат — versioned durable native identity/outcome evidence и сверка
перечисленных незавершённых команд. Затем общий history/capture basis, сохранённое
восстановленное состояние и полный B4 lifecycle по
[плану до QuickFIX/n](../B4_TO_QUICKFIX_PLAN_2026-11-05.md).

## Проверка

Набор `b4-ledger`: предыдущие 521 проверки, 28 регрессионных проверок исходного
журнала и 47 новых. Новые случаи включают 23 real PostgreSQL проверки: все dispatch
phases, Place/Cancel/Replace chain, full scope isolation, лимиты обеих сторон,
повреждения и отсутствующие связи, orphaned journal, SELECT-only role и
конкурентное обновление между чтением private events и commands. Последний сценарий
требует согласованной старой пары, а следующий снимок — согласованной новой пары.
Application-проверки покрывают каждый исход отправки, другие инструменты, invalid
snapshots, дубли, cancellation и запрет дополнительного private read.

Первый полный прогон: 591/596 PASS, пять ошибок подготовки новых отрицательных
fixtures (append-only/outbox protections и конструктор пустого ID сработали до
проверяемого кода). Подготовка исправлена только в disposable fixtures; production
проверки не ослаблялись. Evidence первого прогона сохранён:
`.tradeops/postgres-reuse/20261105T170257-b4-ledger-16808/`.
Cleanup PASS, guard before/after OK и peer-container diff пустой; итоговый
environment_status=FAIL у этого runner также отражает ненулевой exit тестов.

Финальный повтор: **596 total/executed/passed**, failures/skips=0, runner exit 0.
Environment/cleanup PASS; guard before/after OK, peer-container diff пустой.
Все **337 normalized source hashes** совпали с текущими исходниками; все 47 новых
случаев имеют outcome Passed в TRX. Evidence:
`.tradeops/postgres-reuse/20261105T171239-b4-ledger-21451/`.
SHA256 test DLL:
`E75CC375E02B02A2668E7BD92E78EEC1C753A68318C76FE5E018FF81F17AABA7`.
После cleanup нет test clones/app roles/активных test sessions; оставлен один owned
reusable template. Windows Release build prescribed SDK/serial flags: exit 0,
zero errors, 15 существующих предупреждений. Реальный PowerShell diagnostics
validator: **30/30 PASS**, vault/venue не использованы.

Пользовательский untracked launchSettings.json не менялся и не включён в коммиты.
GitHub Actions не запускались; публикация помечена `[skip ci]`.
