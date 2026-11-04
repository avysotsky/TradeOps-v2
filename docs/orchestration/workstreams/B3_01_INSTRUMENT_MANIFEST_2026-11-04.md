# B3-01 — public instrument manifest preflight, 2026-11-04

Ветка `TradeOps/b3-01-instrument-manifest`, baseline main
`020c3abf66044215e1e4c6ac609e5a930132180b`, продолжение
[#73](https://github.com/avysotsky/TradeOps-v2/issues/73).
[Контракт и запуск](../../architecture/DERIBIT_INSTRUMENT_MANIFEST.md).

## Что изменилось

В `TradeOps.Connectors.Deribit` добавлен bounded public catalog reader и проверка
явного private capture plan по native names/amount units/currencies. Base, quote,
settlement и query currency сохраняются отдельно. Формат символа и contract_size
не используются для угадывания объёма. Неизвестные semantics, дубли и плохой ответ
не дают частичный manifest. Inactive metadata не отбрасывается.

Отдельный public-only CLI выводит только aggregate counts, timestamps и digest.
Он не читает vault, не создаёт привязку счёта, не подключает private API, Worker
или БД. Loader не включается в host автоматически; это подготовленный preflight
для последующей квалификации по доверенной конфигурации.

## Проверено

- Restore из установленного cache, bounded Windows Release builds CLI и test
  project: PASS, 0 errors; CLI 0 warnings, test project 15 прежних warnings.
- Ubuntu: **35/35 PASS**, 0 failed/skipped: 24 новых catalog cases и 11 прежних
  strict private-parser cases. Raw TRX counters/outcomes просмотрены отдельно.
  Native inverse/linear/options rules, fixed origins, запрос без credentials,
  malformed/oversize/duplicate/error responses, cancellation, immutable snapshot
  и configured-plan mismatch проверены без реальной сети в unit tests.
- Два отдельных настоящих public mainnet GET через новый CLI: BTC future —
  **13** записей (13 USD amount); BTC option — **908** (908 base-coin amount),
  оба `PUBLIC_METADATA_PASS`. Это снимок 2026-11-04, не постоянное число листингов.
  `privateCalls=0`, `mutatingCalls=0`, `accountCoverageQualified=false`.
- Resource Guard до/после PASS, redacted snapshots всех существующих контейнеров
  совпали. PostgreSQL не использовался, контейнеры/роли/БД не создавались.
- Durable OMS contracts, миграции, host defaults и runtime регистрации не менялись.
  Пользовательский launchSettings сохранён и исключён из commit.

Evidence: `.tradeops/b3-instrument-manifest/`, включая raw TRX, normalized source
manifest, public results и guard/container snapshots. SHA-256 test assembly:
`b7fcbae9c33018080f7df678ee1e5f8bed15190c336222659b8ee9545d4e59d5`.
Окончательный TRX: `tests-final/results.trx`; public smoke — `public-btc-*-final.json`.
Предварительный запуск остановился до tests из-за чтения отсутствующего Health
поля контейнера; только локальный reader snapshot исправлен на безопасный JSON
projection, затем выполнены все 35 cases. Это не failure production-кода.

## Оставшийся gate B3

Владелец выбрал **существующую серверную привязку**, без создания новой. Найденный
старый TerminalApi запуск передаёт read-only credentials напрямую, а vault хранит
только encrypted credential pair и environment. Это не полный AccountKey и не
независимо заданный expected remote account ID. В проверенных конфигурациях/локальных
хранилищах полного B3 binding пока не найдено; credential fingerprint старого
broker-evidence контракта не заменяет account identity.

Private live stream / live WS→PG qualification: **NOT_RUN**. Vault не расшифровывался,
новая account mapping не создавалась, постоянная БД не использовалась. Для этого
gate нужно найти или отдельно оформить подтверждённое расширение существующей
серверной привязки того же счёта, затем выполнить bounded private read-only acceptance
на изолированной БД. Не выводить expected account ID или секреты в публичные evidence.

#73 остаётся открытым. Public metadata proof не доказывает account-wide coverage,
coherent recovery B4, physical sender fencing B5 или trading readiness. Полный
unrelated backend suite и browser E2E в этой тематической итерации NOT_RUN.
