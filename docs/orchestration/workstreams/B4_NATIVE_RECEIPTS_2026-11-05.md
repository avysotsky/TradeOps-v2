# B4: сохраняемая связь команды с native order/outcome

2026-11-05. Код `1f60a1512a174f6f81b757138be02f57ccfe9397`.
Реализованы direct JSON-RPC receipt v1, отдельный append-only журнал,
чтение receipt в общем снимке и сверка привязки с private reports в order gate.
Это частный результат полноты B4; восстановленная рабочая модель счёта пока не
публикуется, READY остаётся false.

## Что изменилось

- В контракте сохраняются original immutable command, attempt, session/request
  context, метод и времена отправки/получения, native order и echoed terms.
  Connector parser разбирает прямой success response и ничего не отправляет.
- Receipt journal требует существующую durable attempt, проверяет actual command
  JSON/hash и dispatch/outbox state, записывает новую связь транзакционно.
  Повтор не создаёт вторую связь; конфликт сохраняет первый receipt и durable
  incident. Это не повторная физическая отправка.
- Адресная отмена требует предыдущую placement-связь того же aggregate/native ID.
  Регресс cumulative/time, уже terminal placement и новая Place поверх существующей
  aggregate version не получают допустимую связь v1.
- Combined ledger reader включает receipts в тот же READ ONLY / REPEATABLE READ
  снимок, что commands/attempts/outbox/private facts. Повреждения, чужие bindings,
  повторные RPC/native identities и неполная Cancel ancestry закрывают весь снимок.
- Gate recomputes связь по actual receipts и accepted private reports. Снимаются
  только command-specific issues для доказанного случая. Отсутствие/противоречие
  private report остаётся явным blocker; receipt не выдаётся за synthetic WS event.
  Цены и количества отдельных fills не подменяют immutable order terms.
- Command v1/private v2/capture v1 encoding/hashes и incident category contract не
  менялись. Новый формат — отдельный schema v1. Защита capture-owner теперь также
  отказывает роли с правом записи receipts.

[Полное правило, доверенный producer и совместимость](../../architecture/DERIBIT_NATIVE_COMMAND_RECEIPTS_V1.md).

## Граница квалификации

Только offline/DB-only, owned disposable PostgreSQL clones. Даже случай с
OmsEnvironment.Mainnet использует synthetic account в test clone; это не запрос
реального счёта. Mainnet store, grants, vault, pinned account и historical live
evidence не использовались и не менялись. Миграция постоянной БД не запускалась.
Старый одноразовый order harness и marker сохранены.

Текущий mainnet-host ещё не производит эти receipts. Следующий bounded OMS host
должен передавать original per-call correlation с фактически проверенной native
session и сохранять parsed direct response. Отдельно квалифицируется readonly
store/role binding и lifecycle с actual capture/recovery. Два исторических ACK
нельзя автоматически «исправить» выдуманными direct receipts по label: для legacy
resolution понадобится отдельная аудируемая процедура.

Global history/execution-ledger/replay/cut/account-coherence/coverage requirements
сохраняются. Receipt — не risk verdict, sender fencing, полный fill ledger или
разрешение торговать. Real order scenario запускается владельцем в пределах
[согласованных параметров](../../testing/OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md).

Далее common history/capture basis, persisted recovered state и full recovery
lifecycle по [плану B4 → QuickFIX/n](../B4_TO_QUICKFIX_PLAN_2026-11-05.md).

## Проверки

Финальный набор: **687 total/executed/passed**, failures/skips=0, runner exit 0;
предыдущие 596 и **91 новый**, все новые outcomes Passed. Среди новых — 28 actual
PostgreSQL случаев, включая gate composition; остальные используют pure/scripted inputs.
Новые проверки включают actual migrations и PostgreSQL journal/reader/gate,
идемпотентность после lost commit ACK, откат до commit, concurrent receipts,
native/RPC collisions, full scope, corrupted evidence, append-only/rollback guards,
SELECT-only RLS и запрет receipt-write authority у capture owner. Конкурентный MVCC
случай записывает receipt и private report, пока reader ждёт intent table: reader
обязан увидеть согласованную старую пару, следующий snapshot — новую.

Первый прогон выявил ошибку нового incident kind: существующий CHECK БД допускает
только CommandCollision/EventCollision/IntentCollision. Исправлена реальная запись
инцидента: сохранена категория CommandCollision, correlation имеет префикс
`deribit-receipt-v1:`. Existing durable incident contract не расширялся.
При внутренней проверке добавлены четыре отрицательных случая для initial Place
version и Cancel progression. Первый прогон завершён **680/683 PASS**, 3 failures;
evidence сохранён в `.tradeops/postgres-reuse/20261105T184405-b4-receipts-23639/`.
Cleanup PASS, guard before/after OK и peer diff пустой; общий environment_status=FAIL
в этом runner также наследует ненулевой test exit. Production checks не ослаблялись.

Финальный повтор после исправления и дополнительных условий:
`.tradeops/postgres-reuse/20261105T185428-b4-receipts-26513/`.
**Environment/cleanup PASS**, guard before/after OK, peer-container diff пустой.
Test clones, app roles и active test sessions отсутствуют, один owned template
оставлен для повторного использования. Все **348 normalized source hashes** совпали.
SHA256 test DLL:
`70BA562CDBCBA1A41136F0474F5A1204A465A178A8E7556BC03C11A535E94AF8`.
Windows Release build с prescribed SDK/serial flags: exit 0, zero errors,
15 существующих предупреждений. Реальный PowerShell validator diagnostics:
**30/30 PASS**, VaultUsed/VenueUsed=false.

Пользовательский untracked launchSettings.json не изменён и не включён в коммиты.
Публикация помечена `[skip ci]`; Actions не вызывались.
