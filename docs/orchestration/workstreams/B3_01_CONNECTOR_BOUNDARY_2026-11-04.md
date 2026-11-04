# B3-01 — граница модуля Deribit, 2026-11-04

Ветка `TradeOps/b3-01-connector-boundary`, исходная main `9d0a08ede39955f0f2af1cf63f2ddbc614827067`. Продолжение [#73](https://github.com/avysotsky/TradeOps-v2/issues/73), без нового Issue/PR.

## Изменение

[Решение владельца о целевой структуре](../../architecture/TARGET_PLATFORM_ARCHITECTURE.md) закреплено отдельно и связано с корневым AGENTS.md и master roadmap. Deribit-only V2 не ограничивает архитектуру будущей платформы с независимыми коннекторами, арбитражем и аналитикой.

В `src/TradeOps.Connectors.Deribit` перенесены семь существующих файлов: options, private capture plan, socket, оба partial-файла stream и оба partial-файла parser. Единственное изменение содержимого этих файлов — namespace `TradeOps.Connectors.Deribit`. Обновлены project references, solution, imports существующих потребителей и test internals access.

Новая сборка ссылается только на Application/Domain и Logging.Abstractions. Фактический восстановленный dependency graph содержит Domain, Application, Logging.Abstractions и DependencyInjection.Abstractions; Infrastructure, EF/Npgsql и ASP.NET отсутствуют. Самостоятельная сборка коннектора проверяет эту границу компиляцией. Core не ссылается на коннектор; persistence остаётся за capture port. Runner evidence теперь включает новый модуль и затронутые project/DI/solution inputs.

Это частичное выделение: legacy REST/DI остаются в Infrastructure, а capture v2/coordinator пока Deribit-specific. Новых общих multi-venue DTO и transport bus в этой итерации нет. Runtime-регистрация нового capture host не включается. Durable payload schemas, event namespaces/digest, migrations, Orders/Fills и execution switches не менялись.

## Реально выполненные проверки

- Restore из установленного package cache, без изменения SDK/global.json или package versions.
- Standalone connector Release build: PASS, 0 warnings/errors.
- Release build существующего test project и его зависимостей: PASS, 0 errors, 15 прежних analyzer warnings.
- Существующий `b3-stream` набор в Ubuntu: **52/52 PASS**, 0 failed/skipped, подтверждено сырым TRX и отдельными результатами. Cases 9,0374 s; PostgreSQL pipeline 5,3613 s. Это scripted RPC lifecycle/parser/account switch, legacy compatibility/DI, настоящий loopback WS и настоящий PostgreSQL capture под restricted application role.
- Сравнение семи перенесённых файлов с baseline: только namespace; Application/Oms, Domain и persistence не изменены. Локальные ссылки проверены.
- Resource Guard до/после PASS; snapshots соседних контейнеров совпадают. Owned reusable test server сохранил ID/start time/restart count; после тестов нет clones/roles/sessions, прежний schema template переиспользован.
- Пользовательский launchSettings сохранил исходный SHA-256 и исключён из commit.

Локальные evidence: `.tradeops/connector-boundary/`, `.tradeops/postgres-reuse/20261104T150021-b3-stream-5975/`. SHA-256 test assembly: `6062a7755e6e740080b39ec75eb456445ff204f2e788e860dd773f3c34f1a0ea`. Текущий source manifest включает новый модуль; соответствие опубликованному commit проверяется при публикации.

Полный unrelated backend suite, browser E2E, live Deribit, host activation и реальные торговые действия: **NOT_RUN**. Это проверка модульной границы и прежнего offline/PG поведения, не доказательство trading readiness.

## Продолжение

#73 остаётся открытым. Следующий gate B3 — доверенная серверная credential/account/instrument binding и единственный host-владелец private stream, затем отдельная live read-only qualification. До coherent recovery B4 поток остаётся RECOVERING. Risk/physical sender fencing B5 и обязательный FIX 4.4 B6 следуют по roadmap.
