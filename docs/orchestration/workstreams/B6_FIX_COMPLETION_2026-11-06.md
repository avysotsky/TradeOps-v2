# B6 — QuickFIX/n и общая цепочка B4/B5

Реализация и приёмочный run: 2026-11-06; итоговая сверка: 2026-11-07, Europe/Kiev.
Ветка `TradeOps/B6-quickfix`, база B5 `c99090d`.

**B6 закрыт по реализации и offline-приёмке поддержанного режима.**
Live qualification/F3 остаются отдельным gate владельца. Main оставлен на B5.

## Реализованная граница

- Настоящий QuickFIX/n 1.14.1, fixed-endpoint TLS с chain/name/revocation validation,
  bounded framing, persistent protocol/message store, monotonic sequences/logon
  timestamp, heartbeat/test/logout, resend/gap fill без повторного business intent.
- Native FIX report/incident journal, canonical/hash verification, scoped native
  identity/dedup. Low-sequence PossDup тоже проверяется, даже если FromApp пропущен
  engine. Native event index сверяется с первичными reports.
- Versioned `DeribitFixCommandBindingV1` записывается до engine order entry в той же
  transaction, что dispatch-start. Исходная OMS-команда, hash, session/epoch,
  deterministic client ID, native units и собственный cancel parent не подменяются
  raw FIX caller input или поиском по label.
- Общий `DeribitNativeCommandView` существует в памяти; FIX не превращается в
  `DeribitJsonRpcCommandReceipt`. B4 RR reader читает bindings/reports/index/incidents
  вместе с commands/attempts/outbox/private facts/WS receipts. Nullable fields с
  `WhenWritingNull` сохраняют encoding старых recovery archives.
- Coherence, command resolution, settlement и own addressed cancel понимают оба
  источника evidence. FIX cumulative quantities только ограничивают WS/history
  ledger; они не прибавляются повторно к fills/positions и не заменяют fee proof.
- Один B5 physical owner для WS/FIX и всех engine writes. Ingress token открывается
  до engine lock, держится через fragments/gaps/native commit; fault закрывает
  baseline. WS epoch restart не стирает pending native ingress. Recovery, начатый
  до drain, не становится квалифицированным задним числом.
- SQL head/controls/counter удерживаются до фактического вызова socket operation
  одноразовым permit; затем commit до ожидания ответа. FromApp может сохранить
  native report до target cursor advance без counter deadlock. Failure отзывает
  owner до rollback, release OS authority — только после abort/drain.
- `Worker:Mode=DeribitOmsFixExecution`, два явных opt-in flags, четыре раздельные
  DB identities; runtime guards проверяют реальные права. Pin подтверждается
  fixed-origin read-only native API теми же credentials; public get_instrument
  даёт actual contract_size и свежие instrument terms. Reader/tick/TLS supervision
  fail-stop, maximum lifetime 240s, без reconnect/auto renewal.

Контракт, границы и provisioning: [DERIBIT_FIX_EXECUTION_V1](../../architecture/DERIBIT_FIX_EXECUTION_V1.md).

## Приёмка

Полный run: `.tradeops/postgres-reuse/20261106T195341-b6-final-3675/`.
Один полный **2351/2351 PASS**, **0 FAIL / 0 SKIP**, runner exit **0**.
Discovery 2336 + 15 runtime-expanded rows; длительность 35m50s.
Оба resource guard, неизменность peer containers, owned clones/roles/sessions cleanup
и итоговый environment status — **PASS**. После полного прогона код не менялся,
повторного полного запуска не было. Итоговая машинная сверка —
`.tradeops/b6-final-qualification.json`.

До полного запуска проверены непосредственно изменённые компоненты:

| Run | Результат | Предмет |
| --- | --- | --- |
| `20261106T193021-targeted-5426` | 4 PASS / 1 FAIL | fixture ошибочно пыталась recovery со старым poisoned capture после lost callback ACK |
| `20261106T193344-targeted-25041` | 23/23 PASS | native view/binding, opt-in host, actual engine + PG lifecycle; restart вместо старого capture |
| `20261106T193833-targeted-27316` | 34 PASS / 1 FAIL | unpooled protocol fixture создавала connections на каждом engine cursor read и нарушала B4 freshness |
| `20261106T194128-targeted-2732` | 21/21 PASS | bounded pools как в host; actual restricted execution/protocol identities; actual engine restart/no resend; ingress/kill; stricter report terms |
| `20261106T194509-targeted-32062` | 149/149 PASS | непосредственно затронутая совместимость B4/B5, readers, archives, risk/cancel/settlement и runtime roles |

Во всех перечисленных PG runs guard/peers/cleanup PASS; исходные FAIL сохранены.
Один ранний Windows build остановился до tests: fixture применяла `with` к обычному
классу DeribitOptions; исправлено созданием options. Сборки после исправления PASS.

Windows new host/evidence/refusal: **25/25 PASS**, exit 0,
`.tradeops/b6-windows-host-evidence/b6-windows-host-evidence.trx`.
Output validators перед full run: **30/30 + 30/30 PASS**, vault/venue не использованы.
Предыдущие checkpoint/ingress/physical-owner/TLS runs сохранены в
[документе состояния](B6_FIX_IMPLEMENTATION_STATE_2026-11-06.md).

Full runner сохранил **484 tracked source hashes**. Девять новых скомпилированных
файлов на момент старта ещё не были staged: их нормализованные hashes зафиксированы
в `supplemental-new-source-manifest.json` того же run; mtime каждого предшествует
compiled assembly. Все **493 source hashes** сверены с финальными файлами.
SHA-256 фактически запущенной test assembly совпал с текущим:
`3538a5d0a9b533076481a8e3e4f5b300f7e1d18814f34e7590b1163fa11dfdac`.

## Явные ограничения

Поддержан узкий qualified profile: pinned main account, standard segregated inverse
futures с независимо проверенным **1 USD/contract**, native USD limit/GTC и own
addressed cancel. BTC contract_size=10, options/linear/PM/cross/subaccount/replace
отвергаются. Отдельный compiled restricted Deribit profile не называется
vendor-issued XML или полным FIX44 dictionary. Hard sequence reset закрывает
выполнение; archival/generation re-provisioning не автоматические.

Unknown/partial, отсутствующий native response, неполный fill/fee ledger или
collision не освобождают риск и не создают READY. После callback fault нужен новый
capture/recovery; saved JSON или старый ACK не восстанавливают capability.
Нет cancel-all, flatten, automatic resend, HA/takeover или multi-host qualification.

Приёмка offline/scripted/owned-PG и actual loopback TLS не означает live Deribit
compatibility, F3 или trading readiness. Реальные placement/cancel не запускались,
постоянные БД/роли, keys/native pin, positions и consumed marker не менялись.
Оба ordinary execution switches остаются false. `launchSettings.json` не менялся:
SHA-256 `CBDDB50FE5C8379A82B3DD8B482696625C7F2F5DFB0058D2FD1D423653398183`.
NuGet vulnerability feed недоступен (NU1900); security audit не заявляется.

Live activation/qualification остаётся отдельным действием владельца по сохранённой
mainnet policy; это не новое разрешение ассистенту запускать торговые операции.
