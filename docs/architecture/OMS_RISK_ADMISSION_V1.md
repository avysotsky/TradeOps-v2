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
settlement с partial-fill dedup, audited denials, durable account/venue/global
breakers и kill switch/RBAC, обязательный physical owner для WS/FIX и закрытие
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
