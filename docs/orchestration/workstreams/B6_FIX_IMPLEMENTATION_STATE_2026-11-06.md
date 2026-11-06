# B6 — состояние реализации FIX, не закрытие этапа

2026-11-06. Ветка `TradeOps/B6-quickfix`, база `main`/B5 `c99090d`.
**B6 IN_PROGRESS.** Полный B6 suite не запускался. Main пока содержит завершённый
B5; новые FIX-компоненты не регистрируются в host и не получают торговых полномочий.

## Дополнение после checkpoint 80fc7ce

Этап по-прежнему **IN_PROGRESS**, end-to-end FIX gateway/host ещё не собран.
В текущем коде устранены две prerequisite гонки и проверена отдельная DB-роль:

- `OmsPhysicalWritePermit.PhysicalIoInitiated` сигнализирует только после вызова
  actual socket operation под revision/owner barrier. Gateway удерживает SQL gates
  до сигнала, commit-ит и лишь затем ждёт ответ. Queued sender не освобождает locks;
  failure/timeout отзывает owner до rollback. Старый foreign-journal regression зелёный.
- `OmsExternalEconomicIngress` закрывает revision под `replaySync`, без фиктивного
  WS epoch/ordinal. `Receive` открывает ticket до engine lock, держит через fragments,
  queued sequence gap и synchronous native persistence. Abandon/fault закрывает baseline.
  WS epoch restart не стирает pending FIX bytes. Recovery, начатый во время pending,
  не квалифицируется задним числом после drain; требуется новый cut.
- Low-sequence native report проверяется journal callback до передачи в engine:
  skipped FromApp у старого PossDup не скрывает изменённый payload/collision.
- `OmsPostgresFixRuntimeGuardV1` проверяет отдельную protocol/native-capture роль:
  только SELECT, append native/protocol evidence и UPDATE трёх cursor/logon columns.
  Session seed/identity, commands/risk/recovery writes и DELETE/TRUNCATE запрещены.
  Guard пока самостоятельный: production host ещё не подключён.

Адресные offline/owned-PG runs, все exit/guard/peers/cleanup PASS:

- `20261106T190111-targeted-20355`: 34/34 PASS (ранний вариант).
- `20261106T190347-targeted-16233`: 36/36 PASS — gateway 13, actual engine 18,
  account revision 5; включает occupied engine lock и fragment/gap drain.
- `20261106T190622-targeted-14771`: 5/5 PASS — actual restricted FIX role,
  cursor store + native insert/dedup/collision и refusal лишних column grants.
- `20261106T190743-targeted-20558`: 8/8 PASS — native journal, включая actual
  engine low-sequence PossDup duplicate/collision.
- `20261106T190848-targeted-3512`: 6/6 PASS — revision, включая сохранение
  pending native ingress через WS epoch restart.

Составной актуальный охват этого дополнения: **50 уникальных cases, 0 FAIL/SKIP**.
Полный B6 suite не запускался; торговля не запускалась. Windows build PASS;
первый wrapper остановился до tests из-за доступа к WSL, затем owned runner
запущен с нужным доступом. NU1900 сохраняется; security audit не заявляется.
Старый `.tradeops/b6-checkpoint-verification.json` относится только к `80fc7ce`,
не подтверждает новый код; новые TRX/manifests лежат в указанных run directories.

Обязательный остаток: versioned native command binding/direct evidence и общий
in-memory view; чтение FIX evidence/bindings в B4 RR transaction; coherence,
resolution, settlement и own cancel обоих transports; actual fixed-origin pin,
metadata, supervised TLS transport и opt-in host с раздельными identities;
end-to-end PG/fault qualification и только затем полный B6 suite. Не объявлять
проверку prerequisites завершением B6 или live readiness. Main остаётся на B5.

## Фактически реализовано

- Закреплены `QuickFIXn.Core` + `QuickFIXn.FIX44` **1.14.1**. Существующий Windows
  SDK, bounded sequential build; SDK/global.json не менялись. Restore успешен;
  vulnerability feed NuGet был недоступен (NU1900), security audit не заявляется.
- `DeribitQuickFixSessionV1` использует настоящий engine через публичный
  `AbstractInitiator`, свой немедленный `IResponder`, без reflection/fork и штатной
  socket queue. Session создаётся библиотекой; native sequence/resend/heartbeat
  lifecycle проверен на actual engine. Business replay подавлен через `DoNotSend`,
  engine отвечает gap fill; новый business intent не отправляется повторно.
- Versioned session/store contracts, provisioned-only PostgreSQL session identity,
  atomic message + sender cursor commit до write; target cursor монотонен, raw
  logon/auth не сохраняются. 10k messages/session; нет reset/purge/таймерного
  освобождения. Native generation reset пока не реализован, hard reset закрывает
  выполнение. Новая migration `20261106200000_AddOmsFixSessionStoreV1`.
- FIX endpoint фиксирован: `fix.deribit.com:9883` / `fix-test.deribit.com:9883`,
  TLS 1.2/1.3, platform chain/name/revocation validation. Только internal loopback
  test helper умеет ephemeral certificate pin; production config не имеет override.
- Bounded fragmented/coalesced frame reader: ASCII, length/checksum/truncation и
  receive/gap capacity. Сессия не пишет raw logs, logon включает `9001=N`,
  `9007=Y`, `9009=N`, `9010=N`, `9015=Y`; timestamp резервируется durable monotonic.
- `DeribitFixExecutionReportV1` / `DeribitFixCancelRejectV1`: отдельные native
  evidence contracts. Server `11`, original client `41`, order `37`, execution
  `17`, change `527` и trade match `880` не смешиваются. Семантический hash исключает
  connection/replay headers. Нет искусственного JSON-RPC receipt и прибавления
  FIX fills к уже учтённым WS/history positions.
- Native report/incident journal уже реализован: migration
  `20261106210000_AddDeribitFixNativeReportsV1`,
  `OmsPostgresDeribitFixReportJournalV1`, envelope v1, strict canonical JSON/hash,
  account/session/epoch/sequence binding, observation/native-event dedup, immutable
  collisions и evidence counter в том же transaction. Native event проверяется
  против исходного report, не только индексного hash. Actual FromApp callback
  сохраняет evidence до target cursor advance; lost callback ACK оставляет report
  в БД и старый cursor. Порт пока не подключён к Worker/B4 reader.
- USD interpretation консервативна: current docs смешивают описания value-order
  `152/38` и contract `14/151`. Пока полностью однозначен только отдельно проверенный
  **1 USD per inverse contract**. Metadata multiplier 10 (BTC) не становится USD
  approval. Contract size нельзя выводить из названия инструмента или min amount.
- Encoder строит `D` из исходной OMS-команды (не caller-provided raw FIX):
  deterministic `to-fix-<CommandId>`, `38` native USD + `854=0`, exact price/GTC,
  reject-post-only `6A` и reduce-only `E`. `F` использует доказанный native server
  ClOrdID как `41`, не выдумывает новый cancel ID в tag 11. Replace пока отказ.
- Один и тот же `OmsSingleHostPhysicalFence` для WS/FIX. Новый непублично создаваемый
  `OmsPhysicalSessionAuthority` защищает ВСЕ engine writes, включая admin/gap fill.
  Revoke и initiation имеют общий barrier; pending session write удерживает OS
  file до завершения даже при ошибке caller drain. Проверены actual OS binding и
  actual B4 capability на реальном PG + actual engine. Это тест физической границы,
  не production risk admission нового FIX sender.
- `DeribitFix44ProfileV1.xml` — **самостоятельно составленный restricted profile**
  по официальной production документации, не vendor-published XML и не весь FIX44.
  Hash проверяется против embedded resource. Dictionary/notices копируются в output
  и publish; QuickFIX license/required acknowledgement сохранены в ThirdPartyNotices.

## Проверки внутри разработки

Все venue-пакеты scripted или actual loopback TLS, PostgreSQL только owned clones.

- `20261106T173639-targeted-14820`: **18 PASS / 1 FAIL из 19**. Ошибка fixture:
  corruption byte мог совпасть с исходной цифрой длины. Исправлено условной заменой
  на другую цифру. FAIL сохранён.
- `20261106T174307-targeted-18943`: **21/21 PASS** — native report parser 11,
  TLS 4, corrupt frame regressions 6. Exit/guard/peers/cleanup PASS.
- Следующая сборка остановилась до tests: readonly `AccountKey.Account` нельзя
  менять через `with`; fixture исправлен созданием нового scoped AccountKey.
- `20261106T175017-targeted-19849`: **42/42 PASS** — actual engine 16,
  command encoder 10, actual physical-owner/B4 boundary 4, затронутый B5 gateway 12.
  Exit/guard/peers/cleanup PASS.
- `20261106T180445-targeted-32189`: **6/6 PASS** — native journal, replay между
  epoch/sequence/connections, collisions, corrupted semantic index, actual engine
  retain-before-cursor и потеря ACK после commit. Guard/peers/cleanup PASS.
- Windows session/TLS: первый **19 PASS / 1 FAIL из 20**, затем несколько
  диагностических TLS-only **3 PASS / 1 FAIL из 4**; raw FAIL сохранены в
  `.tradeops/b6-windows-*`. В ограниченном запуске Schannel client не получал SSPI
  credentials; при запуске с нормальным доступом Windows выявлено, что Schannel
  server не поддерживает ephemeral test key. Исправлена именно fixture: временный
  PFX import без PersistKeySet, zeroing memory и Dispose, без trust-store install.
  Test helper нормализует IPv4-mapped loopback. Protocol выбирает ОС, но до любых
  FIX/auth bytes проверяется negotiated TLS 1.2/1.3; chain/name/revocation не ослаблены.
  **Финальные Windows TLS 4/4 PASS**:
  `.tradeops/b6-windows-tls-final/b6-windows-tls-final.trx`.
  Actual session engine 16 уже прошёл Windows в исходном 20-case run.
- `20261106T182004-targeted-12671`: **4/4 PASS** изменённого TLS на Ubuntu,
  exit/guard/peers/cleanup PASS.
- `20261106T182325-targeted-16570`: **17/17 PASS** parser + journal после явного
  исключения неподтверждённого cross-connection ExecID 17 из semantic identity.
  Сам tag 17 сохраняется в report JSON; native `880`/`527` остаются отдельными.

Текущий объединённый охват: **54 новых B6 cases + 12 B5 owner cases**, без оставшихся
сбоев в проверенном поведении. Три PostgreSQL cursor/store cases прошли в первом
прогоне и их implementation после него не менялась. Это НЕ полный B6 acceptance.
Evidence под ignored `.tradeops/postgres-reuse/`; public reference snapshots —
`.tradeops/b6-reference/`. Local memory recall снова OperationalError; не ремонтировать.

Независимая сверка `.tradeops/verify_b6_checkpoint.py`:
**66 уникальных затронутых cases, 0 оставшихся FAIL, Windows 20/20 составных cases**.
Все **481 source hashes** последнего PG/portable snapshot совпали с текущими файлами,
сверен SHA-256 test assembly. Аудит — `.tradeops/b6-checkpoint-verification.json`.
Ни один полный B6 suite не запускался; B6 не закрывается этой сверкой.

## Следующая обязательная интеграция

1. Подключить уже реализованный native report journal к настоящему transport/host
   с exact session binding; добавить guard прав реальной ограниченной роли.
   Native persistence не означает, что FIX уже включён в B4 или risk gateway.
2. Versioned FIX command binding/direct evidence; общий *in-memory* view для B4/B5
   вместо подделки `DeribitJsonRpcCommandReceipt`. Совместимость journal/private/
   receipt/recovery encodings сохранить. Новые nullable fields на archived v1 DTO
   требуют `WhenWritingNull`, чтобы старые hashes не менялись. Getter properties
   могут сериализоваться — использовать methods/internal views, не скрытый JSON change.
3. B4 RR ledger должен читать новые reports/bindings в ТОЙ ЖЕ транзакции; coherence,
   command resolution, settlement и own cancel должны понимать оба транспорта.
   FIX cumulative/last quantities не добавлять вторично в positions/fills из WS/history;
   FIX reports не содержат полного fee proof и не заменяют обязательную историю.
4. **Предотвратить deadlock:** B5 gateway держит evidence counter через response,
   а FIX FromApp обязан retain report ДО target cursor advance. Если report journal
   повышает тот же counter, sender ждёт FromApp, FromApp ждёт sender transaction.
   Нельзя отключать evidence bump или ослаблять B4. Нужен сигнал от одноразового
   permit о ФАКТИЧЕСКОМ начале I/O: удержать head/controls/counter до этого сигнала,
   затем commit SQL и await response. Не release после простого вызова async метода,
   пока реальный write ещё может быть queued. Старый regression foreign journal
   mutation между final check и I/O должен остаться зелёным.
5. **Immediate FIX ingress barrier:** одной `recovery.RequestRecovery()` недостаточно
   для гонки с последним socket write. Нужен отдельный external economic ingress
   token у capture coordinator под тем же `replaySync`: begin до DB, pending до
   durable commit; qualified revision не создаётся при pending. Не присваивать FIX
   фиктивный WS epoch/receipt ordinal. Queued seq gap удерживает блокировку до drain;
   fault закрывает baseline до нового квалифицированного recovery. Новый report,
   появившийся до WS delivery, должен остановить stale physical permit.
   Begin должен происходить на native socket ingress **до engine sync/queue**, а не
   только в FromEarlyIntercept: writer может уже держать engine lock и ещё не начать
   физическую отправку. При fragmented frame pending удерживается до полного
   framing/drain; conservatively можно учитывать все FIX bytes, включая heartbeat.
   Low-sequence PossDup engine может не доставить в FromApp: проверить known semantic
   replay/коллизию отдельно, не потерять изменённый native payload под старым seq.
6. Соединить fixed-origin read-only native account pin + TLS wire + reader/tick/drain
   supervision + gateway. Key/client ID не подтверждают native account сами по себе.
   Configured metadata — из actual public get_instrument (contract_size), не fake.
   Отдельный opt-in mode, separate least-privilege identities и guard прав новых
   таблиц. Runtime не мигрирует/не seed-ит policy/session/binding и не меняет права.
7. Добавить реальные end-to-end PG + scripted/loopback FIX fault cases: durable
   admission до wire, unknown response/cancel, restart, report lost-ACK, native dedup,
   ingress/kill/owner race, no auto resend. Завершить весь B6 и лишь затем один full
   suite. Owner-launched live qualification остаётся отдельным gate, без новых
   реальных placements/cancels данным ассистентом.

## Первичные источники, проверенные 2026-11-06

- [Deribit production overview/endpoints](https://docs.deribit.com/fix-api/production/overview.md)
- [Logon](https://docs.deribit.com/fix-api/production/logon.md)
- [NewOrderSingle](https://docs.deribit.com/fix-api/production/new-order-single.md)
- [Cancel](https://docs.deribit.com/fix-api/production/order-cancel-request.md)
- [Execution reports](https://docs.deribit.com/fix-api/production/execution-reports.md)
- [Sequence reset](https://docs.deribit.com/fix-api/production/sequence-reset.md)
- [QuickFIX/n 1.14.1 source](https://github.com/connamara/quickfixn/tree/v1.14.1)
- [NuGet Core](https://www.nuget.org/packages/QuickFIXn.Core/1.14.1)
- [Issue #40, прочитан без изменения](https://github.com/avysotsky/TradeOps-v2/issues/40)
- [Microsoft SslStream/platform diagnostics](https://learn.microsoft.com/dotnet/core/extensions/sslstream-troubleshooting)

Работать самостоятельно, без субагентов/worker-чатов. Main не объявлять B6-ready.
Не трогать keys, consumed marker, постоянные БД, production сервисы, launchSettings.
