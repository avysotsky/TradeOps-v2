# Продолжение TradeOps-v2 после B5 — B6 QuickFIX/n

Дата: 2026-11-06, Europe/Kiev. Репозиторий: `D:\Projects\TradeOps-v2`.
Этот handoff относится к следующему этапу после принятого B5. Перед началом
сверить Git/main, master roadmap и фактическую приёмку в отчёте B5.

B5 принят составной offline-приёмкой: один полный **2245/2245 PASS**, после
найденной final-review race только затронутые **29/29 PASS**, всего **2246**
актуальных cases без оставшихся сбоев/пропусков; output validators **30/30 + 30/30**,
458 source hashes, guard/peers/cleanup PASS. Windows file-ID proof 1/1 PASS.
Два red regression runs и ранние development FAIL сохранены, не переписаны.

## Что прочитать

1. `AGENTS.md`, [целевую архитектуру](../architecture/TARGET_PLATFORM_ARCHITECTURE.md)
   и начало [master roadmap](INSTITUTIONAL_PLATFORM_MASTER_ROADMAP_2026-10-25.md).
2. [Итог B5](workstreams/B5_RISK_EXECUTION_COMPLETION_2026-11-06.md) и
   [risk/controls/physical owner contract](../architecture/OMS_RISK_ADMISSION_V1.md).
3. [B4 recovery runtime](../architecture/OMS_RECOVERY_RUNS_V1.md),
   [план до QuickFIX/n](B4_TO_QUICKFIX_PLAN_2026-11-05.md),
   [изолированные PostgreSQL tests](../testing/OMS_POSTGRES_TESTS.md),
   [mainnet policy](../testing/OMS_MAINNET_QUALIFICATION_POLICY_2026-11-05.md).
4. Текущий код и существующий Issue #40; не создавать дубликат Issue/PR и не
   запускать GitHub Actions. Исторический #73 не является номером нового B6.

## Сохранённая граница B5

Обязательный account-scoped gateway: actual live B4 → current DB policy/native risk
→ atomic command/outbox/pending reservation → attempt/dispatch → final revalidation
→ one-use permit → owned physical transport → direct receipt → recovery.
Нет публичного конструктора permit; JSON/READY/reference/старый ACK не дают власти.
Gap/новый private ingress/restart/sмена controls закрывают live capability.

Durable global/venue/account kill/circuit, exact real-role RBAC и immutable audit.
Unknown/partial fills не освобождают риск; cancel ACK не terminal proof. Stop не
вызывает cancel-all/flatten. Own addressed manual cancel разрешена только тем же
gateway/owner и с native proof. Старые adapter/terminal/CLI execution paths закрыты.

Поддержан pinned main account, standard segregated BTC/ETH inverse futures,
native USD amount, limit/GTC и addressed cancel. Options/linear/PM/cross/subaccount/
replace/FIX order entry пока отказ. Нельзя молча превратить эти ограничения в
поддержку нового продукта только потому, что FIX умеет соответствующее сообщение.

`OmsSingleHostPhysicalFence` держит OS-exclusive файл на одном provisioned node,
грубее account — venue/environment. `oms_execution_bindings_v2` закрепляет
machine + canonical root + actual directory/file object identities. Runtime не
создаёт lock file, не перепривязывает node и не меняет свои права. Только local
fixed Windows filesystem или Linux ext4/XFS/Btrfs/tmpfs. SQL lease/advisory lock,
одинаковый textual path, overlay/network root или истёкшая heartbeat не являются
физическим fencing. Перед отпусканием owner: revoke → abort → drain → release.
Нет automatic multi-host takeover, reconnect/resend или HA qualification.

## Задание B6

Реализовать обязательный Deribit FIX 4.4 через **QuickFIX/n** в пределах существующей
модульной архитектуры, с current official Deribit FIX dictionary и фактическими
venue semantics. Сначала изучить уже имеющиеся FIX abstractions/configuration и
связи с B4/B5, затем реализовывать поведение, а не новый план вместо кода.

- Отдельный connector/session adapter, TLS/certificate validation, dictionary и
  custom tags, logon/heartbeat/test request/logout.
- Persistent bounded session/message store, sequence numbers, resend/gap fill,
  restart/recovery и явная обработка sequence reset; не resend неизвестный business
  intent как новую заявку.
- Versioned native FIX report contracts, ClOrdID/OrigClOrdID/OrderID mapping,
  dedup ExecID, partial fills/reject/cancel/unknown states и связь с OMS ledger.
  WS JSON-RPC receipt не является FIX ExecutionReport; не backfill по label.
- Та же обязательная B4/B5 qualification/admission и тот же physical owner.
  WS и FIX не получают независимых файловых locks/lease scopes. Каждый физический
  order write включая engine-initiated resend должен подчиняться доказанному owner;
  различать transport/session replay и повторный business intent.
- Opt-in host, separate least-privilege identities, defaults OFF и отсутствие
  обхода через legacy sender/стратегию/UI. Connector не зависит от EF/PostgreSQL/
  Infrastructure. Durable contracts изменять только versioned совместимо.
- Offline replay и реальные owned-PG проверки: duplicate ExecID, seq gaps/resets,
  unknown outcomes, crash/restart, partial fills, kill race и stale physical owner.

Разработку вести самостоятельно без субагентов/worker-чатов. Внутри этапа только
непосредственно затронутые проверки, полный набор после окончания всей реализации
перед закрытием. Не повторять полный B5 для знакомства или после маленькой правки.
Сборка Windows SDK с bounded последовательными параметрами из AGENTS; PostgreSQL
только owned reusable server/clones, resource guard/peers/cleanup обязательны.

## Торговля и публикация

Публикация проверенного кода в main разрешена, ветки `TradeOps/B6-<задача>`, commits
с `[skip ci]`. Без force/rebase/GitHub Actions/новых дублирующих PR. `.tradeops/`,
секреты и private account data не коммитить. Пользовательский launchSettings не
менять; его SHA-256 закреплён в отчёте B5/предыдущем handoff.

Постоянная mainnet БД/роли не мигрировались, live qualification B5 не выполнялась,
оба ordinary switches false. Согласованные ETH-PERPETUAL 1 USD/post-only/distance
10%/одна активная/до трёх placements не дают ассистенту права запускать новые
реальные placement/cancel; их выполняет владелец. Старый consumed marker не
сбрасывать. Ключ и ранее выданное разрешение повторно не запрашивать.

Локальный memory search был недоступен (OperationalError); продолжать по текущему
репозиторию/evidence, не ремонтировать память и не утверждать успешный recall.
Никаких фоновых LLM/API-вызовов для обработки памяти.
