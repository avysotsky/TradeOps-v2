# FIX — согласованный пакет применён к retained store

2026-11-07, Europe/Kiev. **SCHEMA/NOLOGIN PROVISIONED; LIVE NOT RUN**.
Владелец ответил «разрешаю» на конкретный запрос применить подготовленные
18 migrations, четыре ограниченные роли и immutable FIX session. Повторное
разрешение на этот же пакет не требуется. Разрешение не расширено на другие
постоянные DB, торговые операции или произвольные account limits.

## Что выполнено

Применены точные SHA-проверенные bytes
[review pack v2](FIX_RETAINED_STORE_PREPARATION_2026-11-07.md) из кода `4689d79`:

| Проверка | Результат |
| --- | --- |
| Target | Existing owned qualification container/volume; точные DB/cluster/shared marker; прежние container ID/start/restart |
| History | 20 → **38** migrations, конец `20261107180000_AddFixSubaccountSessionV2`; source history проверен до каждого SQL этапа |
| Новые roles | capture/recovery/execution/protocol; все **NOLOGIN**, без super/create DB/create role/replication/bypass RLS |
| Старые roles | Атрибуты прежних identities не изменились |
| Circuit permission | Только activate, exact retained account scope; clear не выдан |
| FIX session | Версия **2**, прежний subaccount scope, exact key SHA и SenderCompID проверены |
| Protocol state | sender=1, target=1, last_logon_ms=0; outbound D/F/G и native reports отсутствуют |
| Risk policy / physical binding | Не создавались, таблицы пусты |
| Peer containers | ID/start/restart/running не изменились, контейнеры/порты не создавались |
| Resource guard | Before/after **OK** |

Непосредственно до применения создан новый custom-format backup 74296 bytes:
SHA-256 `f175c45a72d5dcff7554fc3ae6d45f0d834ae6863b6705bc094b763948e51488`.
PGDMP header и SHA проверены; restore private backup **не выполнялся**.
Прежний backup также сохранён и проверен, исходный журнал не переносился.

Для **всех девяти прежних OMS tables** совпали counts и SHA-256 полного
канонического row set до/после. Сохранены один aggregate/intent, две commands,
две attempts/outbox, два inbox facts и две capture observations; incidents и
account leases остались пустыми. Payloads, native facts, outcomes и старые
markers не переписывались. Новых OMS-команд не создано.

## Проверка прав без активации host

Выполнены неизменённые SELECT-проверки из опубликованных C# guards:
`OmsPostgresPrivateCaptureOwner`, `OmsPostgresRecoveryRuntimeGuard`,
`OmsPostgresExecutionRuntimeGuard` (включая exact scoped circuit permission),
`OmsPostgresFixRuntimeGuardV1`. Все **4/4 PASS**.

Метод: локальная admin connection выполняет `SET SESSION AUTHORIZATION` новой
ролью, после чего `current_user == session_user` и все privilege checks идут
от этой роли. Каждое соединение принудительно transaction-read-only. Исходники
SQL сверены по normalized SHA. Это каталоговая проверка прав под выбранной
session identity; фактический password LOGIN четырьмя roles не проверялся,
NOLOGIN не изменялся. Capture ownership, leases и physical authority не
получались. Это не `FIX_CONFIGURATION_CHECK_PASS`.

## Evidence и текущая граница

Private evidence сохранён под ignored
`.tradeops/fix-provisioning/apply-20261107T193546Z/`: before/after inventory,
row hashes, role attributes, backup+manifest, applied-files, result и
role-check. SQL stdout пуст, exit codes обоих этапов **0**; результат
`APPROVED_PACK_APPLIED`, каталоговый `PERMANENT_ROLE_CATALOG_CHECK_PASS`.
Private account IDs, данные журнала и секреты не публикуются.

Production code в этом шаге не менялся; повторять offline suite ради применения
уже проверенного SQL не требовалось. Предыдущие full/affected/TRX остаются
датированными evidence своих builds. Main остаётся на B5 `c99090d`.

Рабочий host ещё требует controlled LOGIN settings, локального доступа Windows
к внутренней DB, immutable physical node objects/binding, current scoped risk
policy и настоящей recovery qualification. Из отсутствующей policy нельзя
вывести разрешение на произвольные limits; outage anchor нельзя сдвинуть ради
READY. Активность на бирже, native logon и D→G→F не выполнялись. Live-квалификация
и будущая WebSocket qualification остаются открытыми.
