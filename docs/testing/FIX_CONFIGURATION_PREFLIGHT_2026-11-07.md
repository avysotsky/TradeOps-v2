# Безопасная проверка конфигурации FIX host

Дата: 2026-11-07. Расширение `a243ffc`, ветка `TradeOps/B6-quickfix`.
**LIVE NOT RUN. Постоянные БД не использовались и не изменялись.**

```powershell
& D:\Projects\TradeOps-v2\scripts\deribit\Deribit-FixQualificationLocal.ps1 -Action Preflight
```

Команда проверяет настройку без DPAPI vault, биржевого ключа, подключения к Deribit,
hosted workers, capture lease или physical execution owner. Она не создаёт роли,
таблицы, policy, FIX-сессию или lock file. Режим Status по умолчанию сохранён.

Если отсутствуют параметры, команда сразу возвращает полный список `Setting:*`
с `Passed=false`, `FIX_CONFIGURATION_CHECK_BLOCKED` и exit1, ещё до сборки и БД.
В текущем окружении отсутствуют все девять параметров:

| Назначение | Переменная окружения |
| --- | --- |
| Capture identity | `ConnectionStrings__TradeOpsCaptureDb` |
| Recovery identity | `ConnectionStrings__TradeOpsRecoveryDb` |
| Execution identity | `ConnectionStrings__TradeOpsExecutionDb` |
| Protocol identity | `ConnectionStrings__TradeOpsFixDb` |
| Уже подготовленный local lock root | `DeribitOms__LockRoot` |
| Уже provisioned FIX SenderCompID | `DeribitFix__SenderCompId` |
| OMS tenant scope | `DeribitCapture__Tenant` |
| OMS account scope | `DeribitCapture__Account` |
| Фактический сохранённый UTC outage anchor | `DeribitRecovery__InitialFromUtc` |

Значения не подставляются автоматически. Нельзя заменять outage anchor текущим
временем, выдавать admin identity за runtime role или повышать risk limits ради PASS.
Секреты оставлять в локальном хранилище/окружении, не присылать в чат или Git.

При наличии конфигурации запускается только early-return mode
`Worker:Mode=DeribitFixConfigurationCheck`, до сборки hosted services.
Четыре подключения принудительно имеют `default_transaction_read_only=on`, без
pooling/multiplexing; фактический `transaction_read_only` проверяется сервером.
Caller-supplied startup Options запрещены. Проверяются:

- Один DB endpoint/database, четыре различных configured и actual identities;
  `current_user=session_user`.
- Те же capture/recovery/execution/protocol role guards, включая exact
  activate-only account circuit permission. Capture permission SQL вынесен в чистую
  проверку: все три исходных SQL текста сохранены, ownership path их переиспользует.
- Schema-2 amendment metadata, exact provisioned session key/schema/hash/cursors
  через отменяемое async SELECT. Protocol store не создаётся и не продвигает cursors.
- Наличие текущей ETH policy и capacity минимум двух pending reservations/2 USD;
  это не native margin valuation или доказательство достаточности всего риска.
- Только существующие node/root/file identities против immutable DB pin.
  `Inspect` не вызывает `Prepare`, не создаёт файл и не резервирует physical owner.

Подключение ограничено 5s, запрос 10s, общий check deadline 45s. Причины неудачных
проверок — только безопасные категории, без exception messages или private values.
Launcher валидирует schema, коды, типы, причины, exit code и полный набор checks.

`FIX_CONFIGURATION_CHECK_PASS` означает только успешную проверку перечисленной
конфигурации. `NativeAccountVerified=false`, `LiveAuthorityEstablished=false`,
`VaultUsed=false`, `VenueUsed=false`, `TradingStarted=false` остаются обязательными.
Это не B4 qualification, финансовое разрешение, persistence/retention attestation
или live readiness. При настоящем owner launch все runtime gates проверяются заново.

## Проверки и evidence

Проверялось только изменённое поведение; новый полный набор не запускался.
Полная B6/amendment приёмка **2418/2418** относится к `a243ffc` и сохранена отдельно.

- `.tradeops/postgres-reuse/20261107T170126-targeted-47`: **39/39 PASS**, exit0,
  guard/peers/cleanup PASS. Включает 26 новых и 13 затронутых прежних cases.
  Четыре реальные ограниченные LOGIN roles используются только в owned clones.
  Positive/missing session/missing policy/лишний protocol grant/missing node;
  строки, cursors, файлы и отсутствие advisory ownership проверены до/после.
- Windows `.tradeops/fix-config-windows`: **22/22 PASS**, exit0.
- `Test-FixConfigurationDiagnostics.ps1`: **32/32 PASS**, без vault/venue/trading.
  Проверены ложный PASS, чужие/private поля, неверные типы, authority markers,
  неизвестные причины, отсутствующие/дублированные checks и exit mismatch.
- Реальный Worker CLI в diagnostic mode при включённых торговых флагах дочернего
  процесса: early return, девять missing settings, exit1, никакого hosted execution.
  `.tradeops/fix-config-worker-mode-proof.json`.
- Текущий launcher Preflight: BLOCKED/9/exit1, vault/venue/trading=false.
  `.tradeops/fix-config-current.json`.
- **511 source hashes** и test assembly SHA сверены; все изменённые scripts/src/tests
  покрыты одним manifest, включая оба FIX launcher scripts. Capture role SQL 3/3
  неизменён, пользовательский launchSettings сохранён.
  Audit: `.tradeops/fix-config-verification.json`; assembly SHA:
  `b00ba35b1941c28197dc3fc1f691f5f5b47aae2f44ecdffd1a9ca75b51e18160`.

Development FAIL сохранены: `20261107T163818-targeted-27291` — 34/35;
`20261107T164536-targeted-32221` — 38/39. Один дополнительный check не был доказан
в каждом прогоне; увеличены только diagnostic timeout budgets, sync session-store
construction заменён async metadata read, добавлены безопасные категории ошибок.
Итоговые 5/5 PG (`20261107T165615-targeted-11024`) и 39/39 зелёные;
права и условия role guards не ослаблялись. NU1900 остаётся, security audit не заявляется.

Следующий вход — путь к уже подготовленной локальной конфигурации либо её локальная
подготовка владельцем. Текущее отсутствие env settings не доказывает отсутствия
самих DB/ролей. Реальные D/G/F по-прежнему запускает владелец по
[процедуре](DERIBIT_FIX_LIVE_QUALIFICATION_READINESS_2026-11-07.md).
