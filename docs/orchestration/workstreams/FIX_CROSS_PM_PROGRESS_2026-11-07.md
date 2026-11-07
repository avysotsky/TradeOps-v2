# FIX cross-PM — текущая реализация и адресная проверка

2026-11-07. **IN PROGRESS; не live readiness и не закрытие расширения.**
Продолжение согласованного сохранения cross/portfolio margin на pinned standalone
subaccount. Исходный B6/offline завершён отдельно; main остаётся на B5.

## Подключено

- Policy schema2, valuation3 для D/4 для price-only G, hashed native evidence,
  обязательные <=1USD/post-only bounds и совместимость прежнего JSON/SHA.
- Native global USD PM simulations для каждого delta[-R,+R], pending/open intents,
  projected margin, fees, inverse-price/stress bounds, scoped debit traversal.
  Общий капитал не суммируется по нормализованным валютным представлениям.
- Composite risk reader с повторным чтением policy/pending и hash-bound cache;
  worker подключает PM port. Addressed cancel принимает только валидную policy2.
- Явное versioned collateral/derivative-history coverage: полная collateral
  inventory сохраняется, все пять derivative settlements обходятся независимо
  от holdings. Не покрытая cash history перечислена отдельно. Неизвестное не
  объявляется пустым; долгий outage остаётся блокирующим.
- Publication/archive reader принимает проверенное v2 покрытие и повторный
  recovery из сохранённого checkpoint. Runtime TTL/live revision не расширены.
- Ограниченный shared read budget, уникальные RPC IDs и общий byte ceiling при
  concurrency. Owner scenario исправлен по directional native price bands.
- ReadPmRisk — диагностическое реальное чтение pinned account/public units,
  без БД, command persistence, gateway, live capability или отправки заявок.

Контракт, метрика loss и ограничения: [cross-PM v2](../../architecture/DERIBIT_CROSS_PORTFOLIO_MARGIN_V2.md).
Новая миграция39 подготовлена и проверена только на test clones. Retained DB
остаётся на38, четыре роли NOLOGIN; policy/node binding не применены.

## Evidence

Составная адресная проверка — **245 уникальных cases, PASS**, без skips:

- Windows core66; native observation/core60; collateral/core/startup67;
  budget/owner scenario/market30; host diagnostic3; configuration preflight22.
- PostgreSQL/recovery/cancel/migration/policy71:
  `.tradeops/postgres-reuse/20261107T213255-targeted-4022`.
- После review исправлена v2 archive selection и добавлен повторный recovery:
  только затронутые4/4, `.tradeops/postgres-reuse/20261107T213903-targeted-16516`.
  Все **537 source hashes** этого финального targeted capture совпали. В capture71
  ровно четыре ожидаемых последующих source differences, покрытых последними4.
- Guard, peer-container comparison, clone/role/session cleanup PASS в обоих
  принятых PG captures; retained test server сохранён. TRX и реальные exit0 сверены.
- Начальный compile failure тестового fixture исправлен до запуска cases; raw
  output сохранён. Предварительные19/19 не используются как полный source capture:
  новые файлы ещё не были staged. Затем полный affected71 выполнен с их capture.
- `launchSettings.json` сохранён; SHA256
  `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

Это не full suite. Прежний full2418 относится к a243ffc. Один новый full — после
завершения всего оставшегося кода, перед общей приёмкой расширения.

## Обязательный остаток

Реальная проверка нового risk-observation path/latency; encrypted runtime profile,
управляемый loopback relay и конкретный остаточный SQL/node/risk пакет; реальное
разрешение сохранённого recovery/outage evidence без стирания или сдвига anchor;
неторговый FIX TLS/logon/heartbeat/logout с запретом business writes/replay;
готовый owner launcher и финальная общая приёмка. Затем владелец запускает
ограниченный live D→G→F; ассистент реальные финансовые order writes не выполняет.

## Реальное чтение risk inputs

После commit `98f5291` выполнен `ReadPmRisk` на существующем native pin:
`NATIVE_PM_RISK_OBSERVATION_PASS`,3 сценария,2933ms. Private artifact и source/DLL
hashes сохранены в `.tradeops/fix-host/latest-readpmrisk.json`. Нет DB writes,
business messages или live authority. Это проверка реальных источников, не READY.

Дополнительный `ReadPmStress` использует два **явно гипотетических** pending USD
и читает семь native сценариев[-3,+3]. Private artifact отдельно сохраняет эту
диагностическую предпосылку; реальные DB reservations не создаются/не читаются.
Адресный host diagnostic4/4 и PS AST/Status PASS; это замена прежних3 diagnostic
cases, не новый full run и не разрешение финансового запуска.
