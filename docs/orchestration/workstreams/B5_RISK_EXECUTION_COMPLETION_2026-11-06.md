# B5 — обязательный риск-контроль и физический владелец отправки

Дата: 2026-11-06. Ветка: `TradeOps/B5-risk-admission`. Исходная база:
опубликованный B4 `24bcc0c`, handoff `a13ce9b`. Единица разработки — весь B5,
без отдельных закрытий его внутренних частей. **B5 завершён и квалифицирован
offline в указанной ниже границе:** 2246 актуальных test-case rows, 0 оставшихся
сбоев, 0 skipped; один полный прогон 2245/2245 и только адресная финальная правка
29/29 после него. Source/binary provenance, guard/peers/cleanup проверены отдельно.

## Реализованный результат

Обязательный `IOmsOrderIntentGateway` объединяет текущую live B4 qualification,
account policy/native valuation, atomic durable admission/reservation, attempt и
физическую отправку. У клиента нет параметра, превращающего JSON/READY/reference
в разрешение. Новый ingress, gap, epoch, чужая journal mutation, смена policy или
controls закрывают путь. Собственные admission transitions связаны с точными
run/generation/state/evidence versions, без ослабления B4 gate.

Проверяются instrument/account eligibility, native units, step/tick/price band,
per-order USD notional, conservative delta, cash/position/open-order exposure,
leverage/margin, reduce-only, concentration, UTC-day loss и pending USD/count.
Нет current versioned policy, native inputs, полномочий или свежего baseline — отказ.
Margin учитывает нелинейный cumulative объём всех pending/new/open orders; доступная
equity ограничена segregated collateral. Bounded cache содержит native observations
с точными command/state/pending hashes, а не approval.

Account-scoped reservation и command/outbox/CAS/control stamp/audit коммитятся
атомарно. Restart, partial fills, lost send/cancel ACK и unknown outcomes не
обнуляют pending и не вызывают resend. Только current terminal B4 order с direct
placement receipt и полной суммой отдельных deduplicated fills может retire reserve.
Адресная отмена доказанного собственного ордера не создаёт нового риска и не
освобождает parent reservation по ACK.

Durable independent kill/circuit latches действуют на global, venue/environment и
account scope; exact scoped DB-role RBAC, CAS/idempotence и immutable audit.
Runtime умеет только activate account circuit, без clear. Loss/existing-margin
breach и post-READY capture gap/disconnect/epoch change включают latched circuit.
Stop запрещает новые increasing-risk starts, допускает только явно запрошенную
проверенную own addressed cancel через ту же цепочку. Автоматических flatten и
cancel-all нет. Committed started attempt уже считается possibly-sent.

Физический sender держит OS-exclusive FileStream до abort/drain всех операций.
Immutable binding v2 закрепляет node, canonical local directory, его фактический
volume/device+file ID и ID самого lock file. Другой host/root/namespace, unlink и
recreate не дают второго владельца. Lease expiry/SQL disconnect не освобождают
OS handle. Перепривязка v1 по одному текстовому пути не принимается. Каждый
actual socket write требует одноразовый live permit и общей ingress barrier.
WS сейчас и будущий FIX используют один owner на venue/environment. SQL evidence
counter удерживается через physical send, чтобы посторонняя запись журнала не
коммитилась между последней проверкой версии и socket write.

Отдельный opt-in `Worker:Mode=DeribitOmsExecution` требует явного Enabled, local
provisioned lock file, current policy и раздельных capture/recovery/execution DB
identities. Least-privilege startup проверен настоящей ограниченной ролью БД;
execution не получает UPDATE recovery head или право менять свои permissions.
Старый adapter и terminal placement/cancel закрыты, terminal status всегда read-only.
Прежние real CLI execution-entrypoints отказывают до stdin/ключей/сети; scripted
legacy replay оставлен для offline contract compatibility.

## Квалифицированная граница

Pinned Deribit main account, standard segregated BTC/ETH inverse futures,
native USD amount, limit/GTC placement и own addressed cancellation. Options,
linear, cross/portfolio margin, subaccount execution, replace и FIX order entry
пока отклоняются. Только один provisioned Windows/Linux узел на venue/environment,
без multi-host takeover. Local fixed Windows drive; Linux ext4/XFS/Btrfs/tmpfs.
Network/overlay/9p, cloned node identities и HA не квалифицированы.

Native risk observations/B4 usability ограничены пятью секундами; capacity/timeouts
дают отказ. Loss v1 — консервативный debit bound с комиссиями и отрицательными
session RPL/UPL без profit offsets, с возможным двойным запасом; это не точный net
PnL и не equity drawdown. Неотправленные/unknown резервы без terminal proof не
освобождаются по таймеру. Frontend F3, multi-venue V3, B6 FIX и production readiness
не объявляются выполненными этим отчётом.

Постоянные mainnet БД/роли не мигрировались, реальные заявки/отмены не отправлялись,
ключи не читались, старый consumed marker не сбрасывался. Оба ordinary execution
switches остаются false. Подготовка deployment/live qualification — отдельная
задача владельца по сохранённой mainnet policy.

## Проверки и evidence

В разработке выполнялись только проверки изменённого поведения/затронутых
компонентов, на owned PostgreSQL clones и scripted/loopback venue. Полный набор
зарезервирован до окончания всей реализации.

Сохранённые промежуточные результаты не переписаны:

- `20261106T113339-targeted-5851`: 47 PASS / 1 FAIL, исправлено тестовое ожидание
  срока baseline с управляемыми часами; journal cases уже прошли.
- `20261106T113817-targeted-23536`: 28/28 PASS.
- `20261106T115905-targeted-13881`: 47/47 PASS.
- `20261106T120248-targeted-27318`: 2/2 migration compatibility PASS.
- `20261106T130139-targeted-8158`: 91 PASS / 5 FAIL. Исправлены actual execution
  manifest, RequestMessage scripted HTTP response и нужное UPDATE intents для
  SELECT FOR UPDATE ограниченной роли; recovery-head UPDATE не выдавался.
- `20261106T131417-targeted-25824`: 57/57 PASS.
- `20261106T132431-targeted-1027`: 63/63 PASS.
- `20261106T132740-targeted-29005`: 8/8 PASS.
- `20261106T133307-targeted-27693`: actual TRX 12/12 PASS, wrapper FAIL/exit 1
  из-за ошибочного ожидаемого количества 24. Не переименован в PASS.
- `20261106T135314-targeted-17183`: 38/38 PASS, nonlinear cumulative margin,
  cash/delta/collateral bounds, pending hash/units и native reader.
- `20261106T135723-targeted-32178`: 10/10 PASS, policy schema/roles/cancel.
- `20261106T140423-targeted-9924`: 1/1 PASS, production B4 publication → actual
  DB policy → native risk reader → durable pending → actual WS transport parser,
  без подмены risk inputs готовым approval.
- `20261106T142109-targeted-18843`: 58/58 PASS, физические identities v2,
  unlink/recreate, replacement directory, actual child-process contention/crash,
  gateway, native inputs/transport, scoped roles и addressed cancel.

Каталоги находятся в ignored `.tradeops/postgres-reuse/`. При промежуточной сборке
также сохранялись compile errors и один runner failure из-за CRLF в shell script;
исправлены до приёмки, не скрыты. Windows-only проверка actual volume/file identities
сохранена в `.tradeops/b5-windows-physical-binding/b5-windows-physical-binding.trx`.
Числа `pg_case_seconds` относятся к исторической subset, не ко всем PG cases.

Полный `20261106T142405-b5-final-1018`: **2245/2245 PASS, 0 FAIL, 0 skipped**,
реальный exit code 0, guard/peers/cleanup PASS. Discovery 2230, ещё 15 data rows
расширились во время выполнения. Оба output validators: **30/30 + 30/30 PASS**.
Включены legacy PostgreSQL/API tests на том же owned isolated server, без пропуска
из-за отсутствующей переменной backend. Duration 32m57s, сумма case time 1879.93s.

В финальном review найдена гонка: второй пред-send transaction сравнивал evidence
version, но не держал её row lock через socket write. Посторонний journal writer
мог коммитить в этом окне. Полный прогон сам эту нехватку покрытия не обнаружил.
Новая regression: `20261106T145843-targeted-19685` — 0 PASS / 1 FAIL; затем
`20261106T150030-targeted-13033` — 0 PASS / 1 FAIL с явным доказательством
`foreignCommitted=true`. Первое ожидание внутри fake transport скрывало причину
за Unknown, поэтому assertion перенесён наружу; оба FAIL сохранены.

Исправление использует уже существующую restricted SECURITY DEFINER функцию
`oms_lock_evidence_version_v1` во втором transaction и удерживает lock до окончания
physical send. Runtime не получает UPDATE evidence counter. Проверка требует
реальный PostgreSQL lock_timeout/55P03 у постороннего writer и успешную собственную
отправку. После этой правки выполнена только затронутая группа:
`20261106T150124-targeted-9095` — **29/29 PASS**, exit 0, guard/peers/cleanup PASS.
Это gateway 12, native transport/end-to-end 7, addressed cancel 7 и policy/actual
restricted role 3. Повторного полного прогона нет.

Независимая `.tradeops/verify_b5_acceptance.py` сверила все TRX rows по display name
и occurrence, сохранив runtime-expanded data rows. Итог: **2246 актуальных cases,
0 оставшихся FAIL, 0 skipped**, acceptance executions 2276 с учётом двух сохранённых
red regressions. Это составная приёмка, не один полный запуск финальной сборки.
Дополнительная Windows physical-binding проверка — **1/1 PASS**.

**458 source hashes** последнего affected run совпадают с текущими файлами
(нормализация CRLF→LF). Между full и final affected изменены ровно два source files:
`OmsPostgresExecutionGateway.cs` и `OmsExecutionGatewayPostgresTests.cs`. Red run
использовал исходный gateway полного прогона и уже окончательный regression test;
остальные source hashes совпадают. Проверено до пересборки и после неё:

- full test assembly SHA-256: `95d33be2fa2f236077b6c89b55282bf8d6d3f01f7c05d1a5cb4abb852cbd1173`;
- final test assembly SHA-256: `bd75867988c8ab54bb0557470d3f7e30b91d12b7119037edecb9af24bc96bad3`.

Сводный локальный аудит: `.tradeops/b5-final-qualification.json`, исходная проверка
full binary до rebuild: `.tradeops/b5-full-provenance-before-repair.json`.
Full discovery 2230/actual 2245; после regression discovery 2231/актуальных cases 2246.
Пользовательский `src/TradeOps.Api/Properties/launchSettings.json` не менялся и не
коммитился; SHA-256 `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.

## Контракты и следующий этап

Journal v1/private event v2/receipt v1/recovery v1 encoding сохранены. Новые
risk/control/policy/settlement/cancel contracts versioned; execution dispatch v2 и
binding v2 сохраняют прежние v1 tables без автоматического authority upgrade.
Rollback отказывается уничтожать immutable audit. Connector не зависит от
Infrastructure/EF/Npgsql. Полный [технический контракт B5](../../architecture/OMS_RISK_ADMISSION_V1.md).

Следующий этап — [B6 Deribit FIX 4.4 через QuickFIX/n](../CONTINUE_TRADEOPS_B6_2026-11-06.md), существующий
Issue #40. Нужны persistent session/message store, dictionary/TLS, seq/resend/gap
recovery, normalized native execution reports и тот же B4/B5/physical owner.
WS direct receipt не заменяет FIX ExecutionReport; B5 не даёт новому FIX отправителю
прав обходить единый owner или менять позиции при восстановлении.
