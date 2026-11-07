# План до live-проверки FIX на сохранённом счёте

Дата: 2026-11-07. **Текущий статус: торговый запуск не готов, live D/G/F не выполнялся.**
План сверён с текущим кодом и evidence `02eaa7e`. Local memory search недоступен
(OperationalError); исторические чаты не использовались как подтверждение.

Уже выполнены FIX engine/B4/B5 integration и offline tests; в постоянной DB
38 migrations, четыре NOLOGIN roles и subaccount session v2. Не завершены
LOGIN/локальное подключение host, physical binding, current risk policy,
настоящая recovery qualification и неторговый FIX connection probe.

## Последовательность и критерии перехода

1. **Проверить реальный сохранённый счёт только чтением.** Использовать существующие
   vault/native pin; проверить native ID/type, текущий баланс, позиции, открытые
   заявки и margin mode. Упомянутые владельцем $8.92 — ориентир для проверки,
   не идентификатор счёта и не разрешённый торговый объём. Результат: fresh
   account-binding/initial-state evidence без торговых изменений.
2. **Подготовить рабочую конфигурацию host.** Подготовить и проверить локальный доступ
   Windows к существующей internal DB, четыре отдельные runtime identities,
   encrypted local profile и provisioned physical owner file/node binding.
   Не пересоздавать retained container и не менять глобальную среду. Весь новый
   код конфигурации, неторгового probe и launcher подготовить/адресно проверить
   до согласования DB changes. Обычные execution switches OFF.
3. **Подготовить один конкретный остаточный пакет настроек и risk policy.**
   Сохранить одобренные ETH-PERPETUAL / <=1 USD / post-only / >=10% distance /
   одна активная / <=3 placements за этап. Учесть технический дополнительный
   резерв price amendment. Недостающие параметры общего account risk нельзя
   выдумывать или повышать автоматически. Представить конкретные значения и
   DB changes одним пакетом на утверждение, затем применить согласованный пакет
   и проверить реальные ограниченные LOGIN connections без admin fallback.
   Уже применённые 18 migrations/NOLOGIN/session повторно не согласовывать.
4. **Получить реальное recovery и проверить FIX без order entry.** Разобрать
   сохранённые commands/native facts; получить coherent current orders/history/
   fills/positions/balances. Не стирать старые попытки и не передвигать outage
   anchor ради READY. Добавить отдельный режим TLS/logon/heartbeat/logout,
   запрещающий D/G/F и business replay до wire. Результат: реальное native
   account verification, актуальное recovery и принятый FIX logon без ордеров.
5. **Завершить готовый локальный запуск и приёмку.** Launcher сам читает local
   profile/vault, проверяет source, binding, policy, recovery и persistent budget;
   не требует от владельца ручных env settings/SQL. В разработке проверять
   затронутое, полный suite один раз после всех оставшихся code changes.
   Результат: configuration PASS, nontrading connection PASS, проверенный
   committed build и конкретный локальный entry point для владельца.
6. **Owner-launched live D→G→F.** Реальные финансовые order writes запускает
   владелец. Одна последовательность: Buy Limit GTC 1 USD post-only не ближе
   10% от свежего рынка в venue band; независимая проверка открытой заявки;
   native price-only G; независимая проверка новой цены; addressed F; terminal
   cancelled recovery с нулевым fill и сохранностью исходных позиций. При
   unknown/fill/timeout — остановка, без blind retry/flatten. Несовместимые
   venue constraints дают отказ; размер/инструмент/расстояние не расширяются.
7. **Принять фактический результат.** Сверить durable commands/bindings/native
   reports/private history/recovery и итоговые позиции/остаток заявок. Выдать
   отдельный PASS/FAIL/UNKNOWN для D, G, F и settlement. Только после этого
   переходить к отдельному плану и квалификации WebSocket.

Настройки, код, диагностику и разбор evidence готовит ассистент. Владелец
утверждает конкретные недостающие risk/configuration параметры и сам запускает
финансовый сценарий. Ассистент реальные order writes не выполняет.
Статус «готов к owner launch» выдаётся по результатам шагов 1–5, а не по
наличию schema или успешным offline tests.
