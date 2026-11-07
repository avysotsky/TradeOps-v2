# FIX — подготовка сохранённого окружения

Дата: 2026-11-07. Ветка `TradeOps/B6-quickfix`. **LIVE NOT RUN**.
Настройки и DB roles подготавливает разработчик; владельцу не требуется знать
пароли, названия ролей или искать неизвестный configuration file.

**После этой подготовки владелец дал разрешение, и пакет применён.**
[Актуальный итог: 38 migrations, четыре NOLOGIN roles, exact session и сохранность журнала](FIX_RETAINED_STORE_PROVISIONING_2026-11-07.md).
Ниже сохранено evidence подготовки до применения; это не текущее утверждение
об отсутствии schema/roles.

## Установленные факты

Read-only Docker metadata + SELECT с `default_transaction_read_only=on`
подтвердили существующий qualification store: owned container/volume/network,
ожидаемые DB/cluster/shared marker. Состояние: 20 migrations до
`20261104120000_AddOmsPrivateCaptureObservations`, 9 OMS tables, 2 commands,
2 attempts, 2 inbox facts, 2 capture observations, 0 integrity incidents.
Четырёх новых B6 identities и B4/B5/B6 schema ещё нет. Опубликованного DB port нет;
container подключён только к внутренней qualification network.

Encrypted native-pin metadata и retained scope совпадают и определяют отдельный
субаккаунт. [Versioned поддержка самостоятельного subaccount key](../architecture/DERIBIT_FIX_DIRECT_SUBACCOUNT_V2.md)
сохраняет этот scope; main account автоматически не подставляется. Торговые
credentials не расшифровывались; запросов биржи и торговых операций не было.

Все private данные и review SQL находятся только под ignored
`.tradeops/fix-provisioning/`. Идентификаторы native account, пароли и содержимое
retained facts в Git и этот документ не включаются.

## Конкретный review pack

`tools/TradeOps.FixProvisioningPlan` — offline generator. Принимает private
инвентаризацию и новую output directory. DB opening interceptor запрещает
открытие любых соединений. Не содержит Apply, venue client или trading mode.
Принимается только полный точный prefix известных migrations; пропущенная,
неизвестная или уже актуальная история отвергается.

Актуальный пакет `.tradeops/fix-provisioning/review-20261107-v2` (первый
`review-20261107` сохранён как development artifact и не предназначен для Apply):

1. `01-migrations.review.sql`: 18 migrations после сохранённого B3, включая v2
   subaccount session CHECK. Guards проверяют точные DB/cluster/shared marker и
   migration history перед применением. Повтор разрешён только при уже полном
   target history; неизвестный/частичный history требует разбора.
2. `02-roles-session.review.sql`: одна transaction; четыре новые constrained
   **NOLOGIN** roles capture/recovery/execution/protocol, grants по runtime guards,
   activate-only circuit permission только для сохранённого account scope,
   immutable session с полным key SHA и детерминированным SenderCompID.
   Отказ, если роль уже существует. Нет UPSERT/reset/rebind, пароль не включён.
3. `review-manifest.json`: migration bounds, scope-free статусы и SHA-256 двух SQL.

SHA-256 актуального schema SQL:
`732cc57f3db216be4e9056a7e0dd937fd950fcf56681efe0200039d1e0aa01b6`;
roles/session SQL:
`6ee04ed1b2bd637ba5d854fe31481507da29cfef4c283bd7ac9ac84354d17ea9`.

На момент подготовки пакет не применялся к постоянной БД. Runtime по-прежнему не мигрирует и не
выдаёт себе права. Policy defaults, execution-node binding и изменение старого
business evidence отсутствуют. Не изменяются старые три B3 identities.

Read-only custom-format backup создан отдельно:
`retained-readonly-20261107T180139Z.dump`, 74296 bytes,
SHA-256 `4d2c214da62020a77a302737351b9a197be4149dd52f3348e54ad777ff950c4b`.
Формат PGDMP проверен; restore этого private backup **не проверялся**.
Исходный журнал остаётся на месте.

## Что разрешает следующий шаг

В [AGENTS.md](../../AGENTS.md), раздел «Среда и безопасность», есть явное правило:
«Не трогать … постоянные БД, Docker daemon, глобальные настройки или WSL».
Поэтому текущая подготовка ограничена read-only обследованием, backup и SQL для
review. Для применения конкретного пакета к этому retained store нужна одна
отдельная отмена запрета владельцем. Это разрешение на DB schema/roles/session,
а не разрешение ассистенту отправлять финансовые заявки.

После применения schema/NOLOGIN roles ещё не означают readiness. Разработчику
остаются controlled LOGIN secrets в локальном encrypted profile, проверенный
локальный путь доступа Windows host к внутренней DB без пересоздания старого
container, immutable node provisioning, явная scoped current risk policy и
фактическая recovery coverage. Outage anchor нельзя сдвинуть, а старые command
outcomes нельзя объявить resolved ради READY. Эти gates должны честно отказать
при недостатке evidence. Политика не выводится из названия малого счёта.

Реальный D→G→F runner запускает владелец после готовности host; ассистент
готовит окружение и анализирует evidence. Подготовка schema не является
FIX_CONFIGURATION_CHECK_PASS, native account verification, live authority или
live qualification. [Матрица и процедура](DERIBIT_FIX_LIVE_QUALIFICATION_READINESS_2026-11-07.md).

## Offline evidence

В разработке: 12/12 subaccount cases (включая actual-engine D→G→F), затем
5/7 PASS в первом provisioning run: два FAIL из-за отсутствовавших attempts в
test setup, raw evidence сохранён. После исправления fixture — 2/2 upgrade
cases PASS. Оба сценария обновляют synthetic retained B3 history, сравнивают
точные прежние rows, проверяют идемпотентность schema script, четыре реальные
ограниченные identities и отказ повторного session/role provisioning.
Это не проверка migration на постоянном store.

Совместный affected run `20261107T180525-targeted-16602`: **100 PASS / 1 FAIL из
101**. FAIL — initial recovery cut в `lost-ack` до placement: за три попытки не
получен coherent baseline (`MissingLiveCapture`, `ActivityDuringRecovery`,
`InventoryUnavailable`, `NativeClockUnavailable`). В fixture ограничение заменено общим
8s deadline; каждый retry создаёт настоящий recovery cut, без reuse overlapped
baseline, с проверкой работающего FIX transport и диагностикой capture status.
Production coherence/ingress/SQL gates не менялись ради этого результата.

После исправления только затронутые gateway/lease cases:
`20261107T181456-targeted-13422` — **18/18 PASS**. Это 15 FIX E2E cases, включая
subaccount D→G→F и lost-ACK/restart, плюс 3 protocol/budget cases. В объединении
с неизменёнными зелёными cases первого run — **104 уникальные проверки,
0 оставшихся FAIL/SKIP**. Составная адресная приёмка, не один 104-case run.
Новые account/schema/history guards и четыре реальные роли прошли в общем run.

Windows **49/49 PASS**: новый subaccount contract/transport/host, существующие
native pin/metadata refusals, FIX host и read-only configuration shape. Output
validator **32/32 PASS**, launcher syntax/Status PASS. Resource guards before/
after, peer-container diff и test clone/role/session cleanup проверены отдельно.
Raw FAIL не удалены и не заменены PASS.

Независимая сверка `.tradeops/fix-provisioning/verification.json`: **517 текущих
normalized source hashes** совпали, все staged code paths покрыты manifest.
Последняя test assembly SHA-256:
`eb4e00345e7d4bbaed56c1a593ff77fa0de2d587e569851d1b6e92e8db3d68c9`.
Backup и оба актуальных review SQL сверены по SHA; пользовательский
launchSettings не менялся. Матрица 104 cases составлена по последним outcomes
двух TRX, а не сложением totals с повторными cases.

Полный baseline **2418/2418** относится к `a243ffc`, дополнительный preflight
**39/39** — к `b8c5a41`. Новый full suite в этой подготовке не запускался.
Эта приёмка не закрывает provisioning/recovery/live gates и не подтверждает
работу реального subaccount FIX logon. Main остаётся на B5 `c99090d`.
