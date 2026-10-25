# TO-v2-51 — manual exchange clock diagnostic without background polling

## Goal

Owner sees `Δ exchange → local` rise from ~250 ms to ~465 ms while the Deribit
RAW book continues to render correctly. There is no evidence that the
underlying timestamp subtraction changed between RAW versions.

The original stream metric remains untouched:

`observedDeltaMs = localBookSnapshotUnixMs - exchangeBookMessageTimestampUnixMs`

This includes differences between Windows and Deribit clocks plus delivery,
queuing, JSON parsing and book construction. It is **not** pure network latency.

## Operator-triggered, no background HTTPS traffic

Click **Проверить часы Deribit** inside the existing collapsed
`Диагностика рыночного потока` panel.

Only that click initiates one independent HTTPS GET to the selected
allowlisted Deribit environment:
- MAINNET: `https://www.deribit.com/api/v2/public/get_time`
- Testnet: `https://test.deribit.com/api/v2/public/get_time`

There is **no interval, timer, startup request, automatic retry or result cache**
in the clock-check workflow. Closing/opening the panel does not send requests.
The button is disabled while a request is pending. Each new click asks for a
fresh measurement. The clock endpoint is reachable only with the existing
localhost ephemeral operator key; fixed hosts, redirects/cookies/proxy
disabled, timeout 5 s, and sanitized errors only.

The full market subscription and RAW L2 event loop remain entirely
independent. No exchange credentials are required for `public/get_time` or
sent on this request. DPAPI remains protected and both trading execution
flags stay forced OFF.

## Estimate and meaning

Capture local Unix milliseconds `t0` immediately before the public GET;
`tx` is the public exchange server time; `r` is locally measured RTT:

`clockOffsetMs = t0 + r/2 - tx`

- Positive offset: Windows clock is ahead of Deribit.
- Negative offset: Windows clock is behind Deribit.
- The RAW book `Δ` displayed in the Depth header **continues updating** and
  is never replaced with a corrected value.
- When the button is clicked, record `rawDeltaAtCheckMs`. The panel then
  displays the fixed one-shot corrected estimate:
  `correctedAtCheckMs = rawDeltaAtCheckMs - clockOffsetMs`.
  It does **not** recompute an aging one-shot offset against future live deltas.
- The panel also shows the probe's timestamp and RTT. These are historical
  values until the operator clicks again. The midpoint approximation assumes
  reasonably symmetric network paths, so it is **not** a precise one-way
  network latency measurement.
- A negative corrected value is diagnostic; never clamp or silently hide it.
- Clock probe failures render a sanitized error in the panel; they never
  restart or invalidate the public RAW L2 subscription.

## Windows acceptance

1. SHA-pinned launcher builds .NET API, shared xUnit book/clock math tests,
   npm/TypeScript/React before decrypting the DPAPI vault.
2. Start mainnet RAW by default, confirm book and header Δ keep moving.
3. Open diagnostics. Prior to pressing the button, check no request has
   been made to `public/get_time`; probe values are `—`.
4. Click the button once. Expect only one GET to public server time, then a
   signed Windows-minus-Deribit clock estimate, RTT, fixed raw Δ at click,
   fixed corrected Δ and sample timestamp.
5. Leave panel open 1–2 minutes. There must be **no additional time requests**
   and the sampled values must remain unchanged while header raw Δ moves.
6. Click again and verify a new one-shot measurement.
7. Switch to 1/10/20 and back to RAW. No change to original L2 continuity
   behavior or new reconnect loops.
8. Keep PR Draft until actual Windows runtime testing confirms.

Reference: Deribit public API `public/get_time` returns current server Unix
time in milliseconds (https://docs.deribit.com/).
