# B5: account-scoped risk admission v1 — реализация в работе

2026-11-06. Это начальная реализация B5, не закрытие этапа и не send permission.

`OmsPostgresRiskAdmission` связывает live `OmsQualifiedRecovery`, опубликованный
B4 run/generation/state digest, актуальную evidence version, серверные policy и
valuation, pending reservation и immutable OMS command/outbox. Команда, outbox,
aggregate CAS и резерв фиксируются одной транзакцией. Старые command v1,
private-event v2, receipt v1 и recovery v1 encodings не меняются. Добавлена
отдельная migration/table `oms_risk_reservations_v1`, schema version 1.

Account recovery head сериализует admissions одного полного account scope.
Суммируются все retained reservations этого scope, включая предыдущие recovery
runs. Противоположные заявки не неттируются. Cancel/replace пока отклоняются.
Путь v1 не освобождает резерв ни по ACK, ни по ambiguous outcome, ни по новому
recovery. Это консервативное ограничение до реализации proven settlement;
повторный fill сейчас вообще не уменьшает pending и не может освободить его дважды.

Версия B4 не ослаблена: следующая admission принимает только исходную publication
version или `evidence_after` предыдущей собственной reservation того же
run/generation/state digest. Любая другая запись журнала ломает цепочку. Locks
берутся в совместимом с publication порядке: command/intent arbitration, recovery
head, existing command rows, evidence counter. Ни один из этих locks не является
physical-send fencing.

Повтор той же команды сравнивается по canonical digest; другой payload с тем же
ID возвращает collision. Команда, ранее записанная без reservation, не становится
одобренной задним числом. Audit reservation содержит policy/valuation JSON и digest,
actor DB role, baseline и обе evidence versions. UPDATE/DELETE/TRUNCATE запрещены;
rollback migration отказывается удалять непустой журнал риска.

`IOmsRiskInputsReader` — доверенный серверный порт. Пользовательская команда не
содержит решение риска. Нет production reader/default policy и регистрации sender.
Порт обязан получить account policy и доказанные instrument rules, цены, FX,
portfolio/loss/margin bounds из авторитетных источников, связать valuation с
точным state/command digest. Проверяются eligibility, units, reduce-only,
notional, position delta, gross portfolio, leverage, margin, concentration, loss,
pending USD/count. USD в этом контракте — оценка риска, а не переименование
native quantity; generic RiskSettings и лимит тестового сценария не используются.
Нет оценки — DENIED. Freshness ограничена пятью секундами, live B4 revision
проверяется до commit; saved JSON не восстанавливает live capability.

Остаток B5: production policy/valuation provider и композиция с recovery host,
settlement с partial-fill dedup, operational wiring автоматических breaker triggers,
обязательный physical owner для WS/FIX и закрытие
всех legacy/direct bypass paths. Результат `Reserved` не должен использоваться
как разрешение на сетевую отправку; отдельная send boundary ещё не реализована.
Обычные execution switches остаются false. Постоянная БД/роли не мигрируются.

Полный набор тестов выполняется перед закрытием всего B5. До этого — только
адресные проверки изменённого поведения и затронутых компонентов.

## Адресная проверка 2026-11-06

Составная проверка текущего изменения: 23 journal cases PASS в первом запуске и
28 risk cases PASS в последнем (18 limit cases и 10 actual PostgreSQL cases).
Это 51 различный case, а не полный прогон или закрытие B5.

- `.tradeops/postgres-reuse/20261106T113339-targeted-5851/`: 47 PASS / 1 FAIL
  из 48; failure — ожидание только `Denied` при истечении пятисекундной freshness
  во время 12-way concurrency. Лимит не превышен, было два резерва; часть отказов
  корректно получила `StaleBaseline`. Для проверки самого oversubscription тест
  использует управляемые часы; production freshness не ослаблена.
- `.tradeops/postgres-reuse/20261106T113817-targeted-23536/`: 28/28 PASS,
  0 skipped, exit 0, environment/cleanup PASS. Дополнительно проверено сохранение
  pending после новой recovery и после ACK/ambiguous outcome. Суммарный счётчик
  `pg_case_seconds` старого runner не распознаёт новый префикс risk test names;
  actual DB execution подтверждён TRX и fixture/cleanup, значение 0 не является
  признаком отсутствия PostgreSQL-проверок.

В обоих запусках guard до/после OK, peer container diff пуст, disposable test DBs
и roles отсутствуют после cleanup, retained только owned schema template. В первом
summary общий `environment_status=FAIL` наследует nonzero status тестов; отдельные
guard/cleanup файлы проверены. Первоначальный FAIL остаётся в evidence.
Mainnet, private store и пользовательский launchSettings не изменены.

## Durable controls и аудит admission

Продолжение B5 добавляет отдельные versioned tables/functions v1, не меняя старые
encodings. `OmsPostgresRiskControls` сохраняет kill switch и circuit breaker как
два независимых журнала-защёлки. Global применяется ко всем tenant/environment;
venue — к конкретным tenant/venue/environment; account дополнительно включает
account/subaccount. Новая recovery или создание нового экземпляра сервиса не
сбрасывают остановку. Снятие одного вида блокировки не снимает другой.
Все уровни относятся к одной authoritative risk БД. Данные global control не
федерируются между разными БД; будущая host-композиция обязана использовать один
и тот же authority store для всех обслуживаемых scopes. Эта реализация не
заявляет глобальную остановку процессов, подключённых к другим хранилищам.

Изменение требует exact scoped permission и настоящей роли БД. Реестр прав
настраивает только владелец БД; host не выдаёт прав и не выполняет migrations.
Возможны раздельные activate-only и clear permissions. При retry сравнивается
decision ID, предыдущая revision, действие, reason и actual actor; другой payload
не становится новым решением. Снятие по старой revision отвергается CAS.
Audit events нельзя UPDATE/DELETE/TRUNCATE. Migration Down отказывается удалять
непустые журналы и настройки прав.

Admission берёт shared transaction locks global → venue → account до journal
locks. Control writer берёт exclusive lock одного scope. Writer ждёт завершения
уже выполняющейся admission; после успешного commit остановки новая admission
возвращает `ControlBlocked`, не создавая команду/резерв. Это граница **резервирования**,
не доказательство остановки физического sender или отмены ранее созданных заявок.

В той же admission-транзакции сохраняется immutable sidecar со всеми шестью
control revisions. Любая смена revision, включая цикл activate → clear, делает
старый stamp неактуальным. Старые reservations без sidecar не получают stamp
при migration. `IsReservationControlCurrentAsync` проверяет только эту часть
состояния, никогда не является send permission и не заменяет live B4/owner gate.

Каждый возвращаемый результат валидной admission-заявки теперь имеет immutable
audit row: scope, exact command hash, результат, baseline run/state hash, actual DB
actor и timestamp. Success audit входит в транзакцию команды/резерва/stamp.
При отказе сначала откатываются tentative CAS/command writes, затем отдельно
фиксируется denial. Если аудит недоступен, операция завершится ошибкой, без
возврата одобрения и без молчаливого исчезновения отказа. Некорректные DTO и
исключения до получения классифицированного результата не выдаются за admission.

Политика stop v1: **block new risk, retain pending, no automatic cancel/flatten**.
Нет cancel-all, RPC побочных эффектов, освобождения резерва по ACK или прямого
operator override B4/risk blockers. Cancel/flatten требует отдельного безопасного
потока и recovery неизвестных исходов. Runtime/provider ещё не подключены;
автоматические triggers и физическая остановка sender остаются в B5.

Адресная проверка продолжения, 2026-11-06:

- `.tradeops/postgres-reuse/20261106T115905-targeted-13881/`: **47/47 PASS**,
  0 skipped, exit 0. 19 новых PostgreSQL control/audit/race cases и 28 затронутых
  admission/limit cases. Обе очередности commit race проверены через настоящий
  PostgreSQL advisory wait, без подмены транзакций моками.
- `.tradeops/postgres-reuse/20261106T120248-targeted-27318/`: **2/2 PASS**,
  0 skipped, exit 0. Actual migration downgrade/upgrade сохраняет прежнюю
  reservation без выдуманного control stamp; downgrade после audit отказывается
  уничтожать историю. Использован `FreshMigrations`, а не только template clone.

TRX проверены независимо от stdout. Нормализованные source hashes совпали с
manifest обоих запусков, новые исходники были staged до capture manifest.
Guard до/после, environment/cleanup и peer container diff PASS. Полный набор не
запускался. Это 49 адресных cases продолжения B5, а не закрытие всего этапа.
