# Deribit cross/portfolio margin — согласованное расширение

2026-11-07. **IN PROGRESS; торговая поддержка ещё не реализована.**
Решение владельца после фактического read-only обследования: «Сохранить режим
счёта и добавить его поддержку в коде». Режим и позиции счёта не менять.
Сохранённый standalone subaccount/native pin остаётся тем же.

Это расширяет прежний standard-segregated scope B5/B6. Недостаточно удалить
проверку `segregated_sm` или выставить eligibility=true. Новые native observations,
policy eligibility, audit и совместимость должны проверяться отдельно.

## Native sources и единицы

[Portfolio Margin](https://support.deribit.com/hc/en-us/articles/25944756247837-Portfolio-Margin)
описывает общий стресс-расчёт X:PM и USD margin totals. Обратные представления
общей маржи в разных валютах нельзя складывать как независимые суммы.
[Account summary](https://docs.deribit.com/api-reference/account-management/private-get_account_summary)
даёт native mode/flags и optional cross-account USD totals. Их читать строго
после ID/type verification с тем же ключом и без subaccount selection.

[simulate_portfolio](https://docs.deribit.com/api-reference/account-management/private-simulate_portfolio)
считает гипотетические позиции; futures amounts указаны в USD. Поддержка не
должна смешивать эти единицы с base quantities из другого метода.
[pme/simulate](https://docs.deribit.com/api-reference/account-management/private-simulate)
даёт native matrix/margin details; для всего cross portfolio используется CROSS.
До использования в admission проверить фактические request/response semantics
и scope read-only на сохранённом счёте. Обе операции — расчёты, не заявки.

## Обязательная реализация

- Явный policy/valuation вариант X:PM. Существующие v1 standard contracts,
  JSON/SHA, архивы и запреты других режимов сохраняют прежнюю семантику.
- Общие USD equity/margin/available funds учитывать один раз; native projected
  margin, fees, adverse execution bounds и unsettled intents не пропускать.
  Не переносить формулы segregated SM в PM.
- Native simulations должны покрывать допустимые варианты исполнений уже
  ожидающих intent и новой команды. Не считать ACK/price G освобождением риска
  и не разрешать снижение резерва по выгодному сценарию. Бounded inputs/scenarios,
  timestamps и отказ при неполных/неоднозначных данных обязательны.
- Тот же B4 recovery/ingress/current evidence, SQL admission и physical owner.
  Account inventory включает collateral currencies, в том числе нулевые и
  legacy assets. Документированные order/trade history endpoints принимают
  пять settlement currencies. Нужна явная versioned coverage модель; нельзя
  скрыть остальные inventory rows или объявить неподдержанную валюту пустой.
- Текущий order-entry профиль остаётся ETH inverse future с native USD quantity;
  quantity amendment/options/linear и смена margin model не включаются автоматически.
- Проверить native units, no double counting, hypothetical fill combinations,
  account/mode/equity changes, unknown/fill/restart и старую v1 compatibility.
  Во время разработки только affected checks; один full после завершения работ.

## Подготовка, уже проверенная отдельно

Есть live read-only native-pin/ETH snapshot и account-wide inventory. Это не
READY и не risk admission. Private values находятся только в ignored evidence.
Локальный fence object подготовлен без записи binding в DB. Windows loopback
route получил PostgreSQL SCRAM challenge без password/SQL; реле ограничено
проверенным owned container, loopback, lifetime и числом соединений. Ни роли
LOGIN, ни risk policy, ни дополнительный DB binding пока не активированы.

Общий [план до owner-launched live FIX](../testing/FIX_LIVE_EXECUTION_PLAN_2026-11-07.md)
сохраняется. Это согласование реализации текущего режима, а не разрешение
ассистенту отправлять финансовые order writes.

## Реализация policy и observations v2

Policy schema2 содержит обязательный `CrossPortfolio`: `cross_pm`, collateral
allowlist, радиус1–3 native USD, distance>=10% и явную метрику
`listed-derivatives-settlement-debits-v2`. Valuation3 относится к D, valuation4 —
к price-only G. Новые optional null-поля не попадают в прежний JSON/SHA.
Новая миграция39 только допускает policy schema2; retained DB остаётся на38.

Каждый целый delta от -R до +R включает кандидата, pending intents и незаполненную
заявку. Нет неттинга на ACK или освобождения по выгодному сценарию. Native USD
capital учитывается один раз; worst global/projected margin дополняется fees,
adverse inverse-price bound и stress. Cash/mode drift, неполные сценарии,
неизвестный активный залог, units/FX/timestamp ambiguity блокируют оценку.
Cache привязан к command/state/pending hashes; policy читается заново.

История включается явно `DeribitRecovery:HistoryCoverage` =
`deribit-listed-derivatives-settlements-v2`. Все collateral rows сохраняются.
История ордеров/сделок обходится для **всех пяти** derivative settlements,
включая нулевые. `UnqueriedCollateralCashCurrencies` явно перечисляет остальные
collateral assets, cash-history которых не запрашивалась. Неизвестные symbols,
currencies и неподдержанные settlements остаются blockers. Candidate schema2
и отдельная coherence policy отличают это покрытие от v1. Запрет публикации
после outage вне recent retention сохраняется.

Daily loss v2 — сумма отрицательных change/cashflow, абсолютных комиссий полного
дневного transaction log пяти derivative settlement currencies и отрицательных
session PnL, без зачёта прибыли. Это не полный account-wide cash-loss показатель
collateral-only currencies. Метрика и численные лимиты должны быть явной частью
остаточного пакета для владельца; другую метрику этот reader не принимает.

Native reads:8MiB суммарно, до64 запросов, до4 одновременно, старты не чаще150ms,
без retry, общий срок5s. Дочерние reads дожидаются завершения при отказе. TTL B4
и срок current baseline не продлеваются.

[get_margins](https://docs.deribit.com/api-reference/trading/private-get_margins)
описывает directional future bands: нижний ограничивает aggressive sell,
верхний — buy. Passive qualification buy ниже `min_price` допустим; сохраняются
tick, upper buy band, post-only, >=10% distance и policy deviation bound.

`Deribit-FixHostPrepareLocal.ps1 -Action ReadPmRisk` читает public units, full
native inventory и PM observations на pinned account. Inventory reference
помечен `diagnostic-unqualified-v2`: нет live revision/publication/permit,
pending из DB не читается. Это диагностика источников/latency, не admission/READY.
