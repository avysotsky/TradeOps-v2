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
