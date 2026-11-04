# B3-01 — trusted read-only host, 2026-11-04

Ветка `TradeOps/b3-01-readonly-host`, baseline main `3c033ea91ec281d90f029b8c9646ad042c7900bb`. Продолжение [#73](https://github.com/avysotsky/TradeOps-v2/issues/73); issue остаётся открытым. [Контракт и конфигурация host](../../architecture/DERIBIT_READONLY_CAPTURE_HOST.md).

## Поведение

Добавлен явный `Worker:Mode=DeribitReadOnlyCapture`: только capture worker, до регистрации любых legacy recovery/event/alert services. Default/Legacy поведение сохранено; неизвестный mode прекращает запуск. Оба execution switches должны быть явно false. Прежние настройки приложения новый mode не включают.

Сервер задаёт immutable binding: полный AccountKey, ожидаемый remote account ID/type, отдельные credentials и bounded instrument manifest. Endpoint выводится из Testnet/Mainnet. Config reload/UI не переключают действующий binding. Проверка реального account pin остаётся в коннекторе до release событий. Нового общего multi-venue DTO не вводилось; connector по-прежнему не зависит от Infrastructure.

Отдельная capture DB настройка обязательна, fallback к legacy DB запрещён. До venue socket проверяется ограниченная роль/privileges и берётся full-scope session advisory lock. Второй cooperating host на том же DB/scope отказывается до socket; shutdown освобождает owned session. Потеря соединения выявляется перед следующим capture, без скрытого reacquire; нормальный inbox dedup сохраняется, включая возможный commit race вокруг потери lease. Это **не physical-send fencing** и не мгновенное обнаружение partition в пустом потоке.

Terminal DB/auth/account/store failure завершает capture host; принудительно выбран StopHost, а executable возвращает nonzero при fault worker. Ошибки не содержат inner DB/remote messages, auth payloads и account values. Ни sender, ни Orders/Fills processors, ни migrations или UI endpoints в этом mode не регистрируются. Все epochs остаются RECOVERING до B4.

## Проверки окончательной версии

- Windows Release build: PASS, 0 errors, 15 прежних analyzer warnings; имеющийся SDK и package cache, без upgrades.
- Ubuntu `b3-host`: **83/83 PASS**, 0 failed/skipped, отдельно проверены raw TRX counters и все outcomes. Из них 26 startup/binding/composition, 5 real-PG host/ownership и прежние 52 stream/parser/coordinator/loopback/PG/legacy compatibility cases.
- Реально проверены: оба environment и main/subaccount binding; запрет неявных/true/malformed execution switches; snapshot конфигурации; отсутствие legacy send/processing services; host stop после role refusal; restricted-role commit только inbox/observations; отказ второму owner до socket; освобождение/reacquire при shutdown; разные account keys; отказ старому owner после termination только его owned test backend.
- Case time **31,0700 s**, суммарно PG cases **28,6196 s**. Первый прогон также 83/83 PASS; после review добавлены явные StopHost/nonzero и проверена окончательная версия.
- Resource Guard до/после PASS; snapshots соседних контейнеров совпали. Reusable test server сохранил ID/start time/restart count. После tests: ни clones, ни generated app roles, ни открытых test sessions; прежний template переиспользован. Durability settings не ослаблялись.
- Durable contract files, v1/v2 namespaces/digests и migrations не менялись. Пользовательский launchSettings сохранён и не включён в commit.

Локальные evidence окончательной версии: `.tradeops/postgres-reuse/20261104T152218-b3-host-7066/`, `.tradeops/b3-readonly-host/`. SHA-256 test assembly: `10ac3bd7667c76397ff1899153c79f6bdb31b48124d7411987ecf242e9b46cf5`. Manifest теперь включает Worker и новый connector binding; проверяется соответствие опубликованному source. Предварительный прогон: `.tradeops/postgres-reuse/20261104T151959-b3-host-5532/`.

## Оставшийся gate

**Live Deribit, deployment новой роли/host, реальная account/instrument qualification: NOT_RUN.** Никаких placement/cancel/replace. Полный unrelated backend suite и browser E2E не запускались: формат и UI не менялись. Это квалификация opt-in host на synthetic exchange + реальном PG, а не live/recovery/trading readiness.

Следующий шаг B3 — отдельная read-only acceptance по доверенной конфигурации и live coverage, с approved account mapping, dedicated restricted DB role и единственным deployment owner. Не использовать persistent dev DB ради acceptance. Затем B4 snapshots/history/fills и authoritative projection; B5 risk и physical sender fencing; обязательный FIX 4.4 B6 по roadmap.
