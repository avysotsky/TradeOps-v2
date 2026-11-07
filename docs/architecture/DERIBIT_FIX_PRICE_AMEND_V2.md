# Deribit FIX: price-only amendment v2

Дата: 2026-11-07. Расширение исходной ограниченной реализации B6 `c1ac85b`.
Live qualification этим документом не заявляется.

## Поддержанный переход

ETH-PERPETUAL, main account, inverse future с проверенным contract_size=1 USD:
`Place (D, aggregate version 0) → Replace (G, version 1) → Cancel (F, version 2)`.
Replace меняет только цену существующей открытой собственной заявки без fills.
Количество, native units, side, label, post-only, reduce-only, тип и TIF сохраняются.
Те же правила допускают следующую доказанную ценовую revision; сценарий квалификации
выполняет ровно одно изменение. Изменение количества и остальные продукты не поддержаны.

G адресуется сохранённым server OrigClOrdID 41; новый пользовательский 11 не создаётся.
Отправляются Symbol, Side, OrderQty, QtyType=0, OrdType=2, Price и исходные ExecInst.
Это venue-specific семантика [Deribit G](https://docs.deribit.com/fix-api/production/order-cancel-replace).
Ответ считается доказанным только при exact approved terms, прежнем OrderID,
исходном client ID, текущем connection epoch, нулевом cumulative fill, новом change ID
и более позднем venue timestamp. Общий FIX ExecType=5 не предполагается:
проверяются [native reports](https://docs.deribit.com/fix-api/production/execution-reports).

Reject, partial fill и потерянный ответ не дают разрешения повторить G или отправить
отмену по старой revision. Включая reject-post-only: нельзя предполагать, что исходная
заявка всё ещё открыта. Требуются recovery и отдельный разбор фактического исхода.

## Durable compatibility

Существующие v1 JSON, hashes, D/F rows и private observations не переписываются.
Migration `20261107010000_AddFixPriceAmendV2` добавляет schema_version=2 для native
command bindings и risk reservations, отдельную append-only таблицу
`oms_fix_amend_messages_v2` для G. Сессия/cursors прежние: запись G и продвижение
sender атомарны под тем же session lock. Reader объединяет v1/v2, проверяет hash;
business replay D/F/G подавляется engine и заменяется protocol gap fill.
Rollback при наличии v2 evidence запрещён.

Schema-2 binding хранит текущий native parent report. Parent должен соответствовать
непосредственно предыдущей aggregate version, тому же intent/order/scope и сохранённому
report. Нельзя выдать новый report за WS receipt или изменить старый durable contract
добавлением сериализуемых derived fields.

## B4/B5 и риск

G проходит обычный gateway: актуальная B4 revision → admission → command/outbox/
полный дополнительный risk reservation → final locks/revalidation → одноразовый
permit → фактический I/O → commit SQL → native retain → восстановление.
Прежний резерв остаётся до terminal proof; риск консервативно учитывает обе записи.
Policy должна допускать два pending commands и соответствующую сумму резервов,
хотя физически это одна заявка. Runtime не увеличивает лимиты автоматически.
Valuation/reservation schema2 требуется именно для Replace; Place сохраняет schema1.
Kill/circuit блокирует G. Own addressed cancel изменённой заявки допускается через
тот же gateway с текущим native parent и existing cancel-under-kill policy.

Последующие partial/filled/status reports связываются с уже доказанной native
ценовой revision и проверяются против полного B4 ledger; они не становятся
нулевым fill ACK изменения. Без предшествующего доказательства G новая цена
остаётся unresolved. Это позволяет восстановить исполнение после успешного G,
сохраняя отказ при неизвестном исходе самого изменения.

Recovery разрешает изменение цены только по доказанной цепочке native revisions и
venue времени. Позднее наблюдение не может вернуться к старой цене; side/quantity/
units не ослаблены. Fills/fees не суммируются повторно из FIX. Оба резерва освобождаются
только после current terminal recovery и полного deduplicated fill ledger.
Ingress barrier, SQL release-after-I/O и OS physical fencing остаются общими WS/FIX.

## Host и роли

Прежний `DeribitOmsFixExecution` не запускает квалификацию автоматически.
Отдельный `DeribitFixOwnerQualification` требует OwnerLaunch, mainnet main-account pin
и exact expected/observed source commit. Прикладной сценарий использует gateway,
без прямого доступа к FIX socket.

К прежним разрешениям protocol role нужны SELECT/INSERT на `oms_fix_amend_messages_v2`;
UPDATE/DELETE/TRUNCATE не выдаются. Остальные роли сохраняют прежние границы B6.
Миграции, policy, immutable session key и физические node objects подготавливаются
отдельно; host и launcher их не создают. [Процедура и live gate](../testing/DERIBIT_FIX_LIVE_QUALIFICATION_READINESS_2026-11-07.md).
