# B3-01 — bounded live private-session proof, 2026-11-04

Ветка `TradeOps/b3-01-private-session-probe`, baseline main
`764990ef01395ac56b93a5ca4bb90d8c74b23e0d`. Продолжение
[#73](https://github.com/avysotsky/TradeOps-v2/issues/73), issue остаётся открытым.
[Контракт и запуск](../../architecture/DERIBIT_PRIVATE_SESSION_PROBE.md).

## Реализовано

- Отдельный CLI и Windows launcher с exact-source gate, build до расшифровки,
  защищённым qualification profile и передачей краткоживущих credentials через stdin.
  Повторный ввод сохранённого ключа не требуется. Смена key fingerprint или pin
  останавливает запуск; production account binding не активируется.
- Переиспользован существующий private lifecycle, добавлены необязательные aggregate
  diagnostics без IDs, имён, frames, access/refresh tokens и account payloads.
- Проверены main/subaccount pins в synthetic tests. Диагностический scope не
  представляет неизвестного parent account и не переносится в production hierarchy.
- Нормализация grants исправлена по фактическому live ответу: явные запреты остаются
  в запросе; обязательны connection + account:read + trade:read. Denied-категории
  могут отсутствовать в grant, одиночный bounded `name:` игнорируется как метка.
  Elevated/unknown/duplicate grants по-прежнему запрещены. Name не определяет счёт.
- Настоящие private events probe не принимает за успешный inbox commit: при событии
  он останавливается с отдельным NOT_STORED результатом. Реального DB store в CLI нет.

## Проверки окончательного кода

- Windows Release build PASS, 0 errors, 15 прежних analyzer warnings. Имеющиеся SDK
  и package cache; глобальная среда и global.json не менялись.
- **108/108 PASS**, 0 failed/skipped, подтверждено raw TRX и всеми outcomes:
  прежние 83 stream/host/loopback/real-PG cases + 9 probe cases + 16 scope-policy cases.
  Включены main/subaccount, отказ неправильному pin до subscribe, heartbeat ACK,
  запрет фиктивного store success и private data в output, отказ mixed settlement.
- Final case time 30,8123 s; PG cases 27,1890 s. Resource Guard до/после PASS,
  snapshots соседних контейнеров совпали; test server сохранил ID/start/restart.
  После тестов ни clones, ни generated roles/sessions; прежний template переиспользован.
- Форматы durable OMS v1/v2, namespaces/hashes, миграции и host defaults не менялись.
  Пользовательский launchSettings сохранён и не включён в Git.
- Live tool dependency graph не содержит Infrastructure, EF/Npgsql или реализации
  sender. Script запускает child с обоими execution switches false.

## Отдельный настоящий live результат

Exact live source: **`8f7dde8fdef8504252ac0b326b557b06052e603e`**.
Окно — 45 секунд после подтверждения подписок, штатная остановка завершилась успешно.

| Признак | Результат |
|---|---|
| Status | `LIVE_SESSION_PASS_NO_DURABLE_CAPTURE` |
| Epoch / auth / account pin / exact subscriptions | 1 / 1 / 1 / 1 |
| Heartbeat challenges / correlated replies | 2 / 2 |
| Typed problems | 0 |
| Public manifest candidates, BTC future/options | 921 |
| Normalized private events / capture returns | 0 / 0 |
| DB used / durable writes / mutating calls | false / 0 / 0 |
| Independently verified owner identity / account-wide coverage / trading readiness | false / false / false |

Это доказательство живого WSS протокола для существующего сохранённого credential
и совпадения HTTP-observed ID с private WS account summary. Это не независимая
аттестация владельца или полная account/parent mapping. За окно private events
не пришли: живой WS→PG commit этим результатом не доказан. Snapshot/history/fills
и coherent recovery B4 не выполнялись. Все epochs остаются RECOVERING.

Первый live запуск на `262809ad0f9af0e0f0932de116ef3ee2f8c8e0ad` безопасно отказал
на AuthRejected до private subscription: старый validator требовал явные :none
в grant и отвергал server label. Раздельные public/auth shape probes не сохраняли
raw auth/labels; более узкий trade:none запрос подтвердил omitted-denial encoding.
После исправления прогон на `c225feefb0a3a2215fb596a0c22a2c35d22c1f00` прошёл.
Финальная версия дополнительно требует IsCompletedSuccessfully при shutdown;
после неё повторены 108 cases и 45-секундный live прогон.

## Evidence и продолжение

Локальные private/защищённые материалы не публикуются:
`.tradeops/b3-private-session/` (sanitized live-result и CurrentUser-DPAPI profile),
`.tradeops/postgres-reuse/20261104T163955-b3-session-538/` (final TRX/source manifest),
`.tradeops/b3-account-binding/` (encrypted observation). Первичные 92-case и 108-case
прогоны сохранены отдельно. Actual IDs, names, fingerprints и credentials отсутствуют
в публичном отчёте. Исходники финального commit сверяются с normalized source manifest
и exact live source; последующая отчётная правка не меняет исполняемый код.

Test assembly SHA-256: `8ccee2d441afe6c4e08133b1c69e117c94d6ef8ad50492fe20817136f83d5370`.
Live executable SHA-256: `76de69a4d3e9bf08418e578bfdbb4baa26f223266fb86c69adb7361ec3db8003`.

Следующий B3 increment — отдельная live WS→durable inbox qualification с restricted
role в изолированной БД; отсутствие естественных событий отмечать явно, не создавать
ордера ради fixture. Production account hierarchy/binding и B4/B5 gates остаются
отдельными. Полный unrelated backend suite, browser E2E и торговые действия NOT_RUN.
