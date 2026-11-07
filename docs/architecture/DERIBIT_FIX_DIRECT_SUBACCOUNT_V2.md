# FIX session v2 — ключ отдельного субаккаунта

Дата: 2026-11-07. Расширение [FIX v1](DERIBIT_FIX_EXECUTION_V1.md) и
[price-only amendment v2](DERIBIT_FIX_PRICE_AMEND_V2.md). Live **NOT RUN**.

## Причина и native identity

Read-only инвентаризация retained qualification store установила одну сохранённую
привязку: `account_id == subaccount_id`, encrypted native pin имеет тип
`subaccount`. Числовой native ID и содержимое журнала не публикуются. Перевод
этой привязки в main account или удаление subaccount label меняли бы scope.
Trading credentials для инвентаризации не расшифровывались.

[Deribit Subaccounts](https://support.deribit.com/hc/en-us/articles/25944616386973-Subaccounts)
описывает отдельные API keys, collateral и позиции субаккаунта; его собственный
ключ имеет доступ только к нему. [FIX Logon](https://docs.deribit.com/fix-api/production/logon)
аутентифицирует API Client ID и описывает несколько соединений одного субаккаунта.
Вывод для нашего ограниченного профиля: отдельный ключ субаккаунта можно
проверить fixed-origin account summary до FIX logon, затем использовать тот же
ключ в FIX без выдуманного account-routing tag. Это инженерный вывод по native
документации, а не подтверждённый live logon.

## Версия и границы

`OmsFixSessionKeyV1.SchemaVersion=1` сохраняет прежний main-account контракт и
отказывает при ненулевом Subaccount. Версия **2** требует ненулевой Subaccount,
точно равной Account. Account обозначает самостоятельный credential scope;
Subaccount сохраняет ранее сохранённый label. Main-key selection дочернего
счёта с отличающимися account/subaccount labels не поддержан.

Migration `20261107180000_AddFixSubaccountSessionV2` меняет CHECK sessions:
v1 + пустой subaccount либо v2 + непустой subaccount, равный account. Старые
session rows, JSON и SHA не переписываются. Store сравнивает schema и полный
key SHA; sender/target cursors и timestamps разделены полным scope. Identity
trigger не позволяет rebind/downgrade. Rollback отказывает при наличии v2 session.
Версии outbound D/F (1), G (2), native report и binding payloads не меняются.

FIX host выбирает session v2 только по pinned capture binding. Native transport
проверяет одновременно ID, точный тип `subaccount`, currency и segregated mode
до открытия FIX wire. Owner launcher проверяет encrypted pin/fingerprint и для
сохранённого subaccount требует Account=`observed-native:<native ID>`, затем
передаёт точно такой же Subaccount label дочернему host. Read-only configuration
check читает v2 session без повышения cursors/ownership.

Risk admission, actual B4 recovery, ingress barrier, physical owner, unknown/fill
stop и conservative amendment reservation остаются обязательными. Продуктовый
профиль ограничен ETH inverse future с доказанным 1 USD/contract; PM/cross,
переводы, родительский ключ и quantity amendment не включены. Обычный WebSocket
execution v1 сохраняет отказ для subaccount; будущая WS квалификация отдельна.

## Проверки

Новые cases проверяют версии/parent selection, точный native ID/type до wire,
opt-in host, реальные независимые PG cursors/restart/immutable identity и отказ
rollback, read-only preflight четырьмя identities и весь D→G→F→terminal recovery
через actual engine с ограниченными execution/protocol roles. Биржа scripted;
PG только owned test clones. Итоговые counts и evidence фиксируются в
[подготовке окружения](../testing/FIX_RETAINED_STORE_PREPARATION_2026-11-07.md).
