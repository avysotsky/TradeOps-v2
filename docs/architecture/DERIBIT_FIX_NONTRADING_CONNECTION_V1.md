# FIX — административная проверка соединения

2026-11-08, Europe/Kiev. Реализация/адресные тесты; реальный FIX logon ещё не
проводился. Retained DB сохраняет NOLOGIN roles и неготовый runtime profile.

`Worker:Mode=DeribitFixNontradingConnection` обрабатывается до hosted services и
execution gateway. Требуются `DeribitOms:Enabled=false`, `DeribitFix:Enabled=true`,
оба обычных trading switches=false, тот же native pin/session, четыре разные
ограниченные DB identities и существующий node/root/file binding.

Host проверяет реальные роли, получает тот же `OmsSingleHostPhysicalFence`,
capture ownership и scoped ingress coordinator. Worker имеет internal доступ к
фабрике session authority Infrastructure; общая public фабрика не добавляется.
Экономический write permit, gateway или order API этот режим не создаёт.

Переиспользуются QuickFIX/n engine, TLS wire, durable session store и native
journal. Независимый последний wire-прослойка допускает только A/0/1/2/4/5,
один полный bounded frame и не более64 admin writes. D/G/F и любой другой
application/replay frame блокируются до underlying I/O независимо от engine.
CancelOnDisconnect остаётся явно N, автоматического reset/reconnect нет.

PASS требует принятого logon, heartbeat с точным случайным TestReqID, ответа
на собственный logout, сохранённых sender/target cursors после этих событий,
отсутствия gap/business reports/blocked writes. Невалидные admin сообщения не
попадают в observer. Полученный business report сохраняется в native journal и
останавливает probe для отдельного recovery. Сырые FIX/auth frames не выводятся.

Logout и остановка дожидаются reader/ticker/I/O до освобождения physical owner.
Общий probe срок35s; host55s с local setup. Это не B4 READY, risk admission или
торговая qualification; результат всегда содержит TradingReady=false.

Адресные проверки:

- QuickFIX engine + новый probe33/33, затем изменённые probe15/15, включая
  standalone subaccount, foreign ingress, неверную heartbeat correlation,
  unsolicited logout и target-cursor commit failure.
- Exclusive host configuration9/9.
- Настоящие четыре restricted LOGIN identities, physical owner/contender,
  protocol cursors4/4 и три admin records без payload/auth material —1/1 PASS
  на owned clone: `.tradeops/postgres-reuse/20261107T221003-targeted-21093`.
  Commands/attempts/outbox/reservations/FIX bindings/reports остаются пустыми;
  protocol role не может INSERT oms_commands. Guard/peers/cleanup PASS.
- Raw предыдущие три fixture FAIL сохранены: отсутствовал scope permission,
  ожидался другой тип OS lock refusal, холодные connections/test deadline.
  Production guards, physical fencing и B4/risk TTL не ослаблялись.

Отдельно исправлена final qualification buy-проверка непосредственно перед
physical I/O: directional native band применяется к buy без ошибочного sell
minimum. Freshness/tick/quantity/post-only/distance/scope не ослаблены;34/34
scenario/transport cases PASS. Это не запуск реальных D/G/F.
