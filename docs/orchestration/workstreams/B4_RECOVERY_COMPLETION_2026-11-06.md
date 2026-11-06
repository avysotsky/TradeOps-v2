# B4 — восстановление счёта и публикация модели

Дата: 2026-11-06. Рабочая ветка: `TradeOps/B4-recovery-completion`.
Реализация B4 и offline-приёмка завершены. Это не live trading readiness и не
разрешение отправлять реальные ордера. Новые production migrations/roles не активировались.

## Реализованный результат

- Общий снимок команд, попыток, outbox, native receipts, private facts и решений
  оператора читается одной PostgreSQL-транзакцией. Область — полный account key.
- Global capture подтверждает native account pin и фактический ACK четырёх
  каналов: orders, trades, changes и portfolio. Событие инвалидирует текущую
  модель до завершения записи в БД; cash/position changes учитываются отдельно.
- Inventory охватывает все текущие открытые ордера, позиции и валюты без фильтра
  по известному manifest. Relevant universe включает также сохранённые команды
  и private facts; нулевой текущий баланс не исключает историю валюты.
- История проходит по всем required currency groups с kind any. Trade cursor —
  native opaque ID; дубли, конфликтующие факты, застрявшие страницы и неизвестные
  единицы не превращаются в успешное восстановление.
- Согласованность ограничена явной политикой `deribit-quiescent-recent-v1`:
  тихий actual replay, два согласованных полных inventory, native clock,
  проверенные histories/model и свежесть. Окно с overlap и временем чтения должно
  укладываться в 25 минут. Более длинный или недоказанный разрыв блокируется.
- Квалифицированная модель ордеров, deduplicated fills, позиций и балансов
  публикуется атомарно с документом сверки, head и lifecycle event. Started/
  ambiguous commands разрешаются только по существующим direct native receipts;
  attempts/outbox меняются той же транзакцией, повторной отправки нет.
- CAS версии журнала, generation/epoch и live revision защищают публикацию и
  потребление модели. Старый процесс, сохранённый JSON, новый факт или новая
  команда не возвращают полномочия старому результату. Рабочая свежесть сейчас
  ограничена пятью секундами; это не разрешение физически отправлять ордер.
- Исторические query facts остаются в immutable recovery checkpoint с исходными
  capture/query metadata. Следующий общий RR-снимок читает их вместе с inbox и
  новыми фактами. Они не превращаются в выдуманные WebSocket insertions. Restart
  за пределами overlap/native order retention не теряет найденные fills; поздний
  повтор из WebSocket дедуплицируется с архивом.
- Сохранённые вопросы и scoped operator decisions аудируются. Допускается
  принятие конкретного наблюдавшегося внешнего ордера с точным digest/fingerprint,
  без создания local command/receipt. Действует реальная роль БД, принятие можно
  отозвать; неизвестный исход команды, history gap и fill conflict не снимаются.
- Режим `Worker:Mode=DeribitAccountRecovery` запускает global capture и recovery
  после нового epoch либо явного refresh. После restart/reconnect началом окна
  служит последний опубликованный native checkpoint; пропустить часть интервала
  нельзя. Неизменённый BLOCKED не перезапускается бесконечно.
- Capture и recovery используют отдельные ограниченные роли. Режим не регистрирует
  legacy workers/order-entry clients, требует выключенных execution switches,
  не мигрирует БД, не выдаёт права и не подставляет администратора.

Контракты, таблицы и операционная композиция:
[durable recovery runs](../../architecture/OMS_RECOVERY_RUNS_V1.md).
Новые recovery schema-v1 документы впервые поставляются этой реализацией;
старые command v1, private-event v2 и receipt v1 encodings не менялись.

## Проверки

Внутри разработки запускались только изменённые компоненты. Основные evidence:

| Проверяемое поведение | Результат | Локальный evidence |
|---|---|---|
| Публикация, версия журнала, invalidation до commit | 17/17 PASS | `20261105T235142-targeted-25925` |
| Реальные транзакции place/cancel resolution, label-only и late fill | 17/17 PASS | `20261105T235623-targeted-17952` |
| Авторизация принятия внешнего ордера, отзыв, неизменные hard blockers | 3/3 PASS после исправления test scope | `20261106T000448-targeted-9657` |
| Atomic rollback команд, dedup fills, непрерывность интервала, границы inventory | 25/25 PASS | `20261106T001050-targeted-9102` |
| Startup, ограниченные роли и версия журнала | 23 PASS; 3 integration cases потребовали исправления fake HTTP response | `20261106T001900-targeted-30466` |
| Actual production readers + restricted roles: reconnect/restart и unknown product | 3/3 PASS после исправления fake response | `20261106T002228-targeted-18599` |

Для перечисленных завершённых прогонов проверены реальные exit codes, TRX counts,
resource guard, неизменность соседних контейнеров и cleanup. Финальный runner
дополнительно перечисляет все созданные `to_*` роли и требует отсутствия любых
временных БД/ролей/сессий, кроме разрешённого reusable template.

**Итог: 2110 проверенных test-case rows, 0 оставшихся сбоев, 0 skipped.**
Один полный прогон, затем только адресные исправления; повтор всего набора не запускался.
Это составная приёмка, а не утверждение, что первый полный TRX был зелёным.

| Финальный прогон/исправление | Реальный результат | Evidence |
|---|---|---|
| Весь проект, включая PostgreSQL/API | 2084 PASS / 21 FAIL, всего 2105 | `20261106T072944-b4-final-18046` |
| Все выявленные сбои, затронутые ACL/runtime/rollback и 2 новых regression cases | 47/47 PASS | `20261106T075847-targeted-15099` |
| Retained archive и затронутые model/universe/reader/runtime | 66 PASS / 2 FAIL, всего 68 | `20261106T083626-targeted-12076` |
| Контролируемые часы restart fixture | 1 PASS / 1 FAIL, всего 2 | `20261106T084423-targeted-7680` |
| Непустой checkpoint за пределами lookup retention, с/без позднего WS-дубля | 2/2 PASS | `20261106T085033-targeted-15908` |

Первый discovery перечислил 2090 случаев; при выполнении data theories развернулись
до 2105. Ещё пять regression cases добавлены при устранении финальных вопросов.
Исправлены переносимость ASP.NET content roots Windows→Ubuntu, PUBLIC EXECUTE для
служебных функций, совместимость безопасного rollback и подготовка bounded pool
перед 16-way inbox race. Защиты сохранности данных и роли SELECT-only не ослаблены.

У 68-case команды ошибочно было указано ExpectedTests=74; её реальный TRX 66/2
сохранён как FAIL. Исправления подтверждены последующими адресными результатами;
ошибочное ожидание не превращено в фиктивный PASS.

Итоговый аудит `.tradeops/b4-final-qualification.json` независимо сверяет все TRX,
сохраняет повторяющиеся display names разных data rows, подтверждает закрытие
каждого сбоя, guard/cleanup/peer checks и совпадение **412** текущих source hashes.
После 68-case прогона изменялся только тестовый fixture; production-код совпадает
с проверенным деревом. Два output validators: **30/30 + 30/30 PASS**.

## Граница поставки

В этой работе не мигрировалась постоянная mainnet БД, не менялись её роли, не
выставлялись реальные заявки и не сбрасывался consumed marker старого one-off.
Owner-launched bounded OMS-сценарий остаётся отдельным строго ограниченным путём
по [согласованной политике](../../testing/OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md).
Старые ACK без direct receipt остаются нерешёнными; label/quantity similarity
не выдаётся за доказательство native binding.

Поддержанные единицы модели — inverse USD-native futures и base-native options.
Unsupported/expired metadata, linear position units, неизвестные продукты,
переполненные выборки и недостаточная история сохраняют BLOCKED. Чтение старого
архива после сбоя допустимо как аудит; runtime gate при этом не открывается.

После успешной приёмки следующий этап — **B5**: обязательные pre-trade/runtime
лимиты, pending exposure, kill switch и единый физический sender. Затем **B6**:
Deribit FIX 4.4 через QuickFIX/n и его fault/replay/session qualification. B4 READY
не заменяет эти условия и не включает торговые switches.
