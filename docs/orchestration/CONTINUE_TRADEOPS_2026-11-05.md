# Продолжение TradeOps-v2 после B4-03a

## Актуальное дополнение 2026-11-05 — B4-03a проверен

Код `9465a608db2070cff52caba3be2c6ed4905178d7`: отдельные `--provision-reader`
и `--read-snapshot`, без вызова старого migrating opener/торгового runner.
[Отчёт B4-03a](workstreams/B4_03A_RETAINED_READER_2026-11-05.md): **99/99 PASS**,
environment/cleanup PASS. Новый reader реально прочитал постоянный store через
SELECT-only роль: retained open/cancel facts/observations/problems **2/2/0**.
Journal commands/attempts **2/2** не изменён, venue requests **0**. Native binding
взят из прежнего encrypted pin; секреты/IDs не публиковались. Provisioning выполнен,
повторять его для обычного чтения не нужно. Reader запускается отдельно через
`scripts/deribit/Deribit-RecoveryReaderLocal.ps1 -Action ReadSnapshot` после bounded build.

**Следующий increment B4-03b:** bounded read-only collection открытых ордеров,
истории и отдельных fills за явный outage interval с overlap, pagination и явной
неполнотой. Использовать существующие key и native pin, без новых торговых действий.
Позиции/балансы, coherent replay, durable projection/recovery gate и fencing остаются.
Историческое чтение store не означает свежую сверку с биржей или B4/READY.

Ниже сохранена исходная точка передачи после B4-02 для истории; её утверждения
«B4-03 ещё не реализован» и первые три пункта ближайшего задания заменены этим
дополнением. Текущий Git и roadmap проверять перед продолжением.

Точка передачи от 2026-11-05. Источник текущих решений — [AGENTS.md](../../AGENTS.md),
[целевая архитектура](../architecture/TARGET_PLATFORM_ARCHITECTURE.md) и актуальные
примечания в начале [master roadmap](INSTITUTIONAL_PLATFORM_MASTER_ROADMAP_2026-10-25.md).
Проверить текущий Git: эта точка передачи может быть старше последующих изменений.

## Состояние на момент передачи

- Последний опубликованный код: main `66d23db012630558ef6f2708ed9b1b136c6ee2a5`.
  Это завершённый B4-02 с записью общего разрешения на дальнейшие публикации.
  Коммит самой точки передачи может быть новее. B4-03 ещё не реализован.
- B3: настоящий ETH-PERPETUAL ордер **1 USD** один раз выставлен и отменён.
  Исполнений не было, исходная позиция сохранилась. Постоянный журнал:
  commands/attempts=2/2, inbox/observations/incidents=2/2/0.
  [Отчёт](workstreams/B3_03_MAINNET_ETH_2026-11-04.md).
- B4-01: deterministic order/execution recovery reducer, scope/epoch/window/coverage,
  dedup и blocking discrepancies. Полный прогон 247/247; после добавления context
  результата повторены все 47 affected recovery cases.
- B4-02: application read port, PostgreSQL read-only repeatable-read snapshot,
  canonical SHA/schema/scope/metadata checks, inbox/observation linkage,
  quarantine/incidents блокируют projection. **87/87 PASS**, включая настоящий PG.
  [Отчёт](workstreams/B4_02_DURABLE_READER_2026-11-05.md),
  [контракт](../architecture/OMS_PRIVATE_RECOVERY_READER.md).
- Live binding нового reader к постоянному store, venue recovery collector,
  позиции/балансы, durable projections/gates и physical sender fencing не выполнены.
  B4/READY не заявлены, issue #73 открыт. Обычные execution switches остаются false.

## Ближайшая реализация: B4-03

1. Сделать отдельный opt-in read-only путь для нового reader к **существующему**
   постоянному mainnet qualification store. Проверять store ownership и account
   scope; не подменять production binding disposable clone и не выводить account
   identity из fingerprint/name. Секреты и native IDs не печатать.
2. Использовать SELECT-only runtime роль. Привязка чтения не должна молча запускать
   migration, торговый runner, повтор первого ордера или общую конфигурацию БД.
   Если нужна отдельная provisioning-операция — ограничить её проверенным owned
   store, отделить от чтения, квалифицировать на clones. Не менять соседние сервисы.
3. Реальным новым reader прочитать сохранённые open/cancel факты, проверить
   canonical integrity, полный scope и отсутствие blocking incidents. Сохранить
   только обезличенный результат. Не выдавать один старый DB snapshot за свежую
   сверку с биржей или completed B4.
4. Затем реализовать bounded read-only collection открытых ордеров, истории и
   отдельных fills за outage interval с overlap, pagination и явной неполнотой.
   Использовать существующий сохранённый ключ и encrypted native pin, без повторного
   запроса ключей, нового аккаунта или ожидания testnet. Позиции/балансы и coherent
   replay → durable projection/recovery gate являются следующими частями B4.

Не повторять прежний торговый harness. Разрешённый одноордерный case уже завершён,
attempt marker сохраняется. Новая публикация кода не означает новое торговое разрешение.

## Рабочие ограничения и разрешения

- Владелец 2026-11-05 явно разрешил: «Да, B4-02 и следующие проверенные этапы».
  Обычный push и безопасный fast-forward main следующих небольших проверенных этапов
  кода разрешены без нового запроса на каждый этап. Без force/rebase, Actions и
  публикации private data. Не создавать Issue/PR для существующей #73.
- Работать самостоятельно, без агентов/worker-чатов. Ветки `TradeOps/<этап>-<задача>`.
- V2 — Deribit, включая обязательный FIX 4.4/QuickFIX/n; другие venues — V3.
  Connector не зависит от Infrastructure/EF. Private facts v2 / observations v1
  и существующие migrations не менять без versioned compatibility решения.
- Не трогать пользовательский untracked `src/TradeOps.Api/Properties/launchSettings.json`.
  Не останавливать/reconfigure WSL/Docker, Comments/Command или соседние постоянные БД.
- Память через локальный recall в этой сессии недоступна (`OperationalError`).
  Продолжать по файлам/Git/evidence, не ремонтировать её и не утверждать, что она
  была прочитана. Никаких фоновых LLM/API memory processing.

## Проверки и локальные материалы

Сборка: существующий `D:\DotNet\dotnet.exe`, Release, `--no-restore -m:1 -nr:false
-p:BuildInParallel=false -p:UseSharedCompilation=false`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`.
Текущий целевой suite: `scripts/testing/Run-OmsPostgresTests.ps1 -Suite b4-reader`.
Не повторять проверки без изменения кода или новой причины. WSL тяжёлые операции
только с Resource Guard до/после и snapshots/cleanup собственных test resources.

Локальная, не публикуемая памятка: `.tradeops/CONTINUE_PRIVATE_2026-11-05.md`.
В ней только пути и технические указатели; ключи и native account IDs туда не писать.
Точные результаты/TRX/source hashes находятся в `.tradeops/`; последний проверенный
reader suite — `.tradeops/postgres-reuse/20261105T081630-b4-reader-10972/`, 87 PASS.

Завершить каждую следующую итерацию обновлением актуальных notes roadmap и отдельным
workstream report. Обновлять эту точку передачи, если по ней ещё продолжают работу.
