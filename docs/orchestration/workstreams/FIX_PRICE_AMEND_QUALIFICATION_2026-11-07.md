# FIX price amendment и подготовка owner qualification

Дата: 2026-11-07. Ветка `TradeOps/B6-quickfix`; исходный B6 `c1ac85b`.
Main остаётся на B5 `c99090d`. **LIVE NOT RUN**.

## Изменение

Добавлены native price-only G, последующая F, schema-2 binding/valuation/reservation,
отдельный protocol G store с общим atomic cursor и полный дополнительный резерв
риска до terminal recovery. Прежние v1 evidence/hashes не переписываются.
Native последующие fills связываются с уже доказанной ценовой revision, сохраняя
отказ для неизвестного G и прежний independent fill/fee ledger.

G проходит обычные B4/B5 admission, final revalidation, ingress barrier и physical
owner. Kill блокирует G; Cancel-after-G использует текущую доказанную native identity.
Нет прямого финансового отправителя внутри qualification-сценария.

Отдельный owner host выполняет D → price G → own F с current recovery после каждого
шага. Mainnet main-account pin/source commit, свежие quotes, 1 USD, post-only,
>=10% от bid, одна активная заявка, persistent stage budget<=3, stop-on-fill/unknown.
Budget берётся из immutable commands; restart не обнуляет расход и не повторяет
unknown. Базовый режим launcher — Status без vault/network/trading.

## Проверки

В разработке запускались только непосредственно затронутые проверки.

- `.tradeops/postgres-reuse/20261107T094031-targeted-27812`: 1/1 PASS,
  actual engine/owned PG D→G→F→recovery→retirement двух резервов.
- `.tradeops/postgres-reuse/20261107T095825-targeted-8354`: 76/76 PASS,
  limited roles, amendment unknown/reject/partial/kill, protocol restart/replay,
  owner scenario/market/source gates, stage arbitration и persistent unknown budget.
- `.tradeops/postgres-reuse/20261107T100512-targeted-16525`: 57 PASS / 2 FAIL из59.
  Оба — initial recovery cut пересёк native logon ingress. Исправлена fixture:
  bounded fresh-cut retry только для transient capture/timing blockers.
  Raw FAIL сохранён.
- Windows ранние scoped runs: 58 PASS / 2 FAIL из60, затем75 PASS /1 FAIL из76.
  Устранены зависимость synthetic metadata от wall clock и timezone fixture:
  LossFromUtc теперь явный UTC, вместо implicit local conversion из DateTime.Date.
  Raw FAIL сохранены; результат финальной проверки дополняется ниже.
- PowerShell parser PASS; выполнен только `-Action Status`: VaultUsed=false,
  TradingStarted=false. Ни один `-Action Run` здесь не запускался.
- `.tradeops/postgres-reuse/20261107T100841-targeted-17281`: 74/74 PASS после
  fresh-cut fixture fix; guard/peers/cleanup PASS, exit0.
- Windows `.tradeops/fix-qualification-windows-final`: **107/107 PASS**, 0 SKIP,
  exit0; включая UTC risk inputs и D/G/F compatibility. Discovery финальной сборки
  2403, ожидаемый executed count с runtime data expansion +15 = **2418**.

Итоговый один общий прогон:
`.tradeops/postgres-reuse/20261107T101200-b6-final-20782` — **2418/2418 PASS**,
0 FAIL/SKIP, exit0, VSTest 36m25s. Включает прежний набор и 67 дополнительных cases.
Оба diagnostic validators **30/30 + 30/30 PASS**. Guard до/после, неизменность
соседних сервисов, cleanup disposable DB/roles — **PASS**. Owned reusable server
сохранён; постоянные DB не использовались.

Независимая сверка `.tradeops/verify_fix_qualification.py`:
**504 LF-normalized source hashes + 1 supplemental launcher hash** совпали,
все изменённые scripts/src/tests покрыты. Launcher не входит в исторический
фиксированный capture_paths wrapper, поэтому отдельно сохранены его полный
snapshot и SHA в `.tradeops/fix-qualification-supplemental-source.json`.
Test assembly SHA-256:
`48d045a2d17a71d88899aa466a394eb7d9303e15c3add54bab7dd6cab84b4076`.
Состав тестов/TRX counts, Windows107, guard/peers/cleanup и неизменный пользовательский
launchSettings проверены; audit `.tradeops/fix-qualification-verification.json`.
Полная исходная приёмка B6 2351/2351 историческая; новое расширение принято отдельно.
NU1900: vulnerability feed недоступен, security audit не заявляется.

## Границы

Реальные Place/G/F не отправлялись. Credentials/pin не расшифровывались. Постоянные
DB migrations/roles/policy/session/node не менялись. Старый consumed marker и
пользовательский launchSettings сохранены. GitHub Actions не запускались.

Поддержано только изменение цены ETH-PERPETUAL при отсутствии fills. Изменение
количества, произвольные native FIX операции и live trading readiness не заявляются.
При остановке после placement собственная заявка может остаться; summary явно
показывает OwnOrderMayRemain/UnexpectedFill, не выполняется cancel по старой revision.

[Контракт v2](../../architecture/DERIBIT_FIX_PRICE_AMEND_V2.md),
[подготовка и команды владельца](../../testing/DERIBIT_FIX_LIVE_QUALIFICATION_READINESS_2026-11-07.md).
Следующий внешний gate — отдельное постоянное provisioning и owner live run;
после него анализировать durable evidence. FIX PASS не является WebSocket PASS.

Read-only проверка текущего process environment: все девять перечисленных в
процедуре host configuration variables отсутствуют. Значения секретов не читались
и не выводились; состояние постоянных DB/ролей этим не установлено. Launcher в
таком окружении откажет до vault и любых торговых действий.
